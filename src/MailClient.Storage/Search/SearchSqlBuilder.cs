using MailClient.Core.Search;

namespace MailClient.Storage.Search;

/// <summary>构造好的搜索条件：WHERE 片段（以 m 为 messages 的别名）、参数，以及每个词选用的匹配方式（便于测试 / 诊断）。</summary>
public sealed record SearchSql(
    IReadOnlyList<string> Clauses,
    IReadOnlyList<KeyValuePair<string, object>> Parameters,
    IReadOnlyList<(SearchTerm Term, MatchStrategy Strategy)> Routes);

/// <summary>把 <see cref="SearchQuery"/> 翻译成 SQL 条件：每个词按 <see cref="SearchRouter"/> 走 trigram / FTS 前缀 / LIKE。</summary>
public static class SearchSqlBuilder
{
    private const string AllColumns = "Subject FromName FromAddress Preview BodyText";

    public static SearchSql Build(SearchQuery query, bool trigramAvailable, TimeZoneInfo? timeZone = null)
    {
        var clauses = new List<string>();
        var parameters = new List<KeyValuePair<string, object>>();
        var routes = new List<(SearchTerm, MatchStrategy)>();
        var trigram = new List<string>();
        var prefix = new List<string>();
        var n = 0;

        foreach (var term in query.Terms)
        {
            var strategy = SearchRouter.Route(term, trigramAvailable);
            routes.Add((term, strategy));
            switch (strategy)
            {
                case MatchStrategy.Trigram:
                    trigram.Add($"{ColumnFilter(term.Field)} : {SearchRouter.QuoteFts(term.Text)}");
                    break;
                case MatchStrategy.FtsPrefix:
                    prefix.Add($"{ColumnFilter(term.Field)} : {SearchRouter.QuoteFts(term.Text)}*");
                    break;
                default:
                    var name = $"$like{n++}";
                    parameters.Add(new(name, "%" + EscapeLike(term.Text) + "%"));
                    clauses.Add(LikeClause(term.Field, name));
                    break;
            }
        }

        if (trigram.Count > 0)
        {
            parameters.Add(new("$trgm", string.Join(" AND ", trigram)));
            clauses.Add("m.rowid IN (SELECT rowid FROM messages_trgm WHERE messages_trgm MATCH $trgm)");
        }

        if (prefix.Count > 0)
        {
            parameters.Add(new("$ftsp", string.Join(" AND ", prefix)));
            clauses.Add("m.rowid IN (SELECT rowid FROM messages_fts WHERE messages_fts MATCH $ftsp)");
        }

        if (query.HasAttachment == true)
        {
            clauses.Add("m.HasAttachments = 1");
        }

        if (query.IsUnread is { } unread)
        {
            clauses.Add(unread ? "m.IsRead = 0" : "m.IsRead = 1");
        }

        if (query.IsFlagged is { } flagged)
        {
            clauses.Add(flagged ? "m.IsFlagged = 1" : "m.IsFlagged = 0");
        }

        var tz = timeZone ?? TimeZoneInfo.Local;
        if (query.After is { } after)
        {
            parameters.Add(new("$after", SqliteHelpers.Fmt(StartOfDay(after, tz))));
            clauses.Add("m.ReceivedAt >= $after");
        }

        if (query.Before is { } before)
        {
            parameters.Add(new("$before", SqliteHelpers.Fmt(StartOfDay(before, tz))));
            clauses.Add("m.ReceivedAt < $before");
        }

        return new SearchSql(clauses, parameters, routes);
    }

    /// <summary>某天在用户时区的 00:00，转成 UTC 瞬间（与库里按 UTC 保存的时间比较）。</summary>
    public static DateTimeOffset StartOfDay(DateOnly day, TimeZoneInfo tz)
    {
        var local = day.ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified);
        if (tz.IsInvalidTime(local))
        {
            local = local.AddHours(1);
        }

        return new DateTimeOffset(local, tz.GetUtcOffset(local));
    }

    public static string EscapeLike(string text)
        => text.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("%", "\\%", StringComparison.Ordinal).Replace("_", "\\_", StringComparison.Ordinal);

    private static string ColumnFilter(SearchField field) => field switch
    {
        SearchField.From => "{FromName FromAddress}",
        SearchField.Subject => "{Subject}",
        _ => "{" + AllColumns + "}"
    };

    private static string LikeClause(SearchField field, string p) => field switch
    {
        SearchField.From => $"(IFNULL(m.FromName, '') LIKE {p} ESCAPE '\\' OR m.FromAddress LIKE {p} ESCAPE '\\')",
        SearchField.Subject => $"m.Subject LIKE {p} ESCAPE '\\'",
        SearchField.To => $"EXISTS (SELECT 1 FROM recipients r WHERE r.MessageId = m.Id AND (r.Address LIKE {p} ESCAPE '\\' OR IFNULL(r.Name, '') LIKE {p} ESCAPE '\\'))",
        _ => $"(m.Subject LIKE {p} ESCAPE '\\' OR IFNULL(m.FromName, '') LIKE {p} ESCAPE '\\' OR m.FromAddress LIKE {p} ESCAPE '\\' " +
             $"OR m.Preview LIKE {p} ESCAPE '\\' OR IFNULL(m.BodyText, '') LIKE {p} ESCAPE '\\')"
    };
}
