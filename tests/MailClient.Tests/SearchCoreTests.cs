using MailClient.Core;
using MailClient.Core.Search;
using MailClient.Storage.Search;
using MailClient.ViewModels;

namespace MailClient.Tests;

public class SearchQueryParserTests
{
    [Fact]
    public void Empty_And_Whitespace_Are_Empty_Queries()
    {
        Assert.True(SearchQueryParser.Parse(null).IsEmpty);
        Assert.True(SearchQueryParser.Parse("   ").IsEmpty);
    }

    [Fact]
    public void Free_Text_Words_Are_Separate_AND_Terms()
    {
        var q = SearchQueryParser.Parse("项目 进度  report");
        Assert.Equal(["项目", "进度", "report"], q.Terms.Select(t => t.Text));
        Assert.All(q.Terms, t => Assert.Equal(SearchField.Any, t.Field));
        Assert.All(q.Terms, t => Assert.False(t.IsPhrase));
    }

    [Fact]
    public void Quoted_Phrase_Stays_One_Term()
    {
        var q = SearchQueryParser.Parse("\"季度 报告\" 合同");
        Assert.Equal(2, q.Terms.Count);
        Assert.Equal("季度 报告", q.Terms[0].Text);
        Assert.True(q.Terms[0].IsPhrase);
        Assert.Equal("合同", q.Terms[1].Text);
    }

    [Fact]
    public void Operators_From_To_Subject_Set_Fields()
    {
        var q = SearchQueryParser.Parse("from:alice to:bob@x.com subject:报价 剩余");
        Assert.Collection(q.Terms,
            t => Assert.Equal(new SearchTerm(SearchField.From, "alice"), t),
            t => Assert.Equal(new SearchTerm(SearchField.To, "bob@x.com"), t),
            t => Assert.Equal(new SearchTerm(SearchField.Subject, "报价"), t),
            t => Assert.Equal(new SearchTerm(SearchField.Any, "剩余"), t));
    }

    [Fact]
    public void Operator_Values_Can_Be_Quoted()
    {
        var q = SearchQueryParser.Parse("subject:\"周报 第43周\" from:\"Linda Mao\"");
        Assert.Equal(new SearchTerm(SearchField.Subject, "周报 第43周", true), q.Terms[0]);
        Assert.Equal(new SearchTerm(SearchField.From, "Linda Mao", true), q.Terms[1]);
    }

    [Fact]
    public void Flags_And_Dates_Are_Parsed()
    {
        var q = SearchQueryParser.Parse("has:attachment is:unread is:flagged after:2026-10-01 before:2026/11/1");
        Assert.True(q.HasAttachment);
        Assert.True(q.IsUnread);
        Assert.True(q.IsFlagged);
        Assert.Equal(new DateOnly(2026, 10, 1), q.After);
        Assert.Equal(new DateOnly(2026, 11, 1), q.Before);
        Assert.Empty(q.Terms);
        Assert.False(q.IsEmpty);
    }

    [Fact]
    public void Read_And_Unflagged_Negate_The_Flags()
    {
        var q = SearchQueryParser.Parse("is:read is:unflagged");
        Assert.False(q.IsUnread);
        Assert.False(q.IsFlagged);
    }

    [Fact]
    public void Chinese_Operator_Names_And_Fullwidth_Colon_Work()
    {
        var q = SearchQueryParser.Parse("发件人：张伟 主题:合同");
        Assert.Equal(new SearchTerm(SearchField.From, "张伟"), q.Terms[0]);
        Assert.Equal(new SearchTerm(SearchField.Subject, "合同"), q.Terms[1]);
    }

    [Theory]
    [InlineData("10:30")]
    [InlineData("http://example.com")]
    [InlineData("foo:bar")]
    public void Unknown_Operators_Are_Plain_Text(string text)
    {
        var q = SearchQueryParser.Parse(text);
        var term = Assert.Single(q.Terms);
        Assert.Equal(SearchField.Any, term.Field);
        Assert.Equal(text, term.Text);
    }

    [Theory]
    [InlineData("before:2026-13-45")]
    [InlineData("after:昨天")]
    [InlineData("has:fish")]
    [InlineData("is:happy")]
    public void Bad_Operator_Values_Fall_Back_To_Text(string text)
    {
        var q = SearchQueryParser.Parse(text);
        var term = Assert.Single(q.Terms);
        Assert.Equal(text, term.Text);
        Assert.Null(q.Before);
        Assert.Null(q.After);
        Assert.Null(q.HasAttachment);
        Assert.Null(q.IsUnread);
    }

    [Fact]
    public void Operator_Being_Typed_Without_Value_Is_Ignored()
    {
        Assert.True(SearchQueryParser.Parse("from:").IsEmpty);
        var q = SearchQueryParser.Parse("项目 from:");
        Assert.Equal("项目", Assert.Single(q.Terms).Text);
    }

    [Fact]
    public void TermsFor_Restricts_Field_Terms_To_Their_Display_Position()
    {
        var q = SearchQueryParser.Parse("from:alice subject:报价 合同");
        Assert.Equal(["合同"], q.TermsFor(SearchField.Any));
        Assert.Equal(["alice", "合同"], q.TermsFor(SearchField.From));
        Assert.Equal(["报价", "合同"], q.TermsFor(SearchField.Subject));
    }
}

public class SearchRouterTests
{
    private static SearchTerm T(string text, SearchField f = SearchField.Any) => new(f, text);

    [Theory]
    [InlineData("合同修改稿", true, MatchStrategy.Trigram)]
    [InlineData("合同修", true, MatchStrategy.Trigram)]
    [InlineData("合同", true, MatchStrategy.Like)]
    [InlineData("订", true, MatchStrategy.Like)]
    [InlineData("invoice", true, MatchStrategy.Trigram)]
    [InlineData("inv", true, MatchStrategy.Trigram)]
    [InlineData("pr", true, MatchStrategy.FtsPrefix)]
    [InlineData("a", true, MatchStrategy.FtsPrefix)]
    [InlineData("合同修改稿", false, MatchStrategy.Like)]
    [InlineData("invoice", false, MatchStrategy.FtsPrefix)]
    [InlineData("a b", false, MatchStrategy.Like)]
    [InlineData("a@b.c", false, MatchStrategy.Like)]
    public void Terms_Are_Routed_By_Length_Script_And_Trigram_Availability(string text, bool trigram, MatchStrategy expected)
        => Assert.Equal(expected, SearchRouter.Route(T(text), trigram));

    [Fact]
    public void To_Always_Uses_Like_Because_Recipients_Are_Not_In_Fts()
        => Assert.Equal(MatchStrategy.Like, SearchRouter.Route(T("bob@example.com", SearchField.To), true));

    [Fact]
    public void Length_Counts_Code_Points_Not_Utf16_Units()
    {
        Assert.Equal(2, SearchRouter.CodePointCount("😀😀"));
        Assert.Equal(MatchStrategy.Like, SearchRouter.Route(T("😀😀"), true));
    }

    [Fact]
    public void QuoteFts_Doubles_Embedded_Quotes()
        => Assert.Equal("\"a\"\"b\"", SearchRouter.QuoteFts("a\"b"));
}

public class SearchSqlBuilderTests
{
    [Fact]
    public void Trigram_Terms_Are_Combined_Into_One_Match_With_Column_Filters()
    {
        var sql = SearchSqlBuilder.Build(SearchQueryParser.Parse("合同修改稿 from:linda"), trigramAvailable: true);
        var p = Assert.Single(sql.Parameters, x => x.Key == "$trgm");
        var expr = (string)p.Value;
        Assert.Contains("\"合同修改稿\"", expr);
        Assert.Contains("\"linda\"", expr);
        Assert.Contains("AND", expr);
        Assert.Contains("messages_trgm MATCH $trgm", Assert.Single(sql.Clauses));
        Assert.All(sql.Routes, r => Assert.Equal(MatchStrategy.Trigram, r.Strategy));
    }

    [Fact]
    public void Short_Cjk_Uses_Escaped_Like()
    {
        var sql = SearchSqlBuilder.Build(SearchQueryParser.Parse("订%"), trigramAvailable: true);
        var like = Assert.Single(sql.Parameters, x => x.Key.StartsWith("$like"));
        Assert.Equal("%订\\%%", like.Value);
        Assert.Contains("ESCAPE", string.Join(" ", sql.Clauses));
    }

    [Fact]
    public void Latin_Short_Term_Uses_Fts_Prefix()
    {
        var sql = SearchSqlBuilder.Build(SearchQueryParser.Parse("pr"), trigramAvailable: true);
        var p = Assert.Single(sql.Parameters, x => x.Key == "$ftsp");
        Assert.Contains("\"pr\"*", (string)p.Value);
    }

    [Fact]
    public void To_Term_Checks_Recipients_Table()
    {
        var sql = SearchSqlBuilder.Build(SearchQueryParser.Parse("to:bob"), trigramAvailable: true);
        Assert.Contains("recipients", string.Join(" ", sql.Clauses));
    }

    [Fact]
    public void Flag_And_Date_Clauses_Are_Generated()
    {
        var sql = SearchSqlBuilder.Build(SearchQueryParser.Parse("has:attachment is:unread is:flagged after:2026-10-01 before:2026-11-01"), true, TimeZoneInfo.Utc);
        var all = string.Join(" AND ", sql.Clauses);
        Assert.Contains("HasAttachments = 1", all);
        Assert.Contains("IsRead = 0", all);
        Assert.Contains("IsFlagged = 1", all);
        Assert.Contains("ReceivedAt >= $after", all);
        Assert.Contains("ReceivedAt < $before", all);
        Assert.StartsWith("2026-10-01T00:00:00", (string)sql.Parameters.Single(x => x.Key == "$after").Value);
        Assert.StartsWith("2026-11-01T00:00:00", (string)sql.Parameters.Single(x => x.Key == "$before").Value);
    }

    [Fact]
    public void Dates_Are_Local_Midnight_Converted_To_Utc()
    {
        var tz = TimeZoneInfo.CreateCustomTimeZone("t", TimeSpan.FromHours(8), "t", "t");
        var sql = SearchSqlBuilder.Build(SearchQueryParser.Parse("after:2026-10-01"), true, tz);
        Assert.StartsWith("2026-09-30T16:00:00", (string)sql.Parameters.Single(x => x.Key == "$after").Value);
    }

    [Fact]
    public void Empty_Query_Has_No_Clauses()
        => Assert.Empty(SearchSqlBuilder.Build(SearchQuery.Empty, true).Clauses);
}

public class HighlightTests
{
    [Fact]
    public void Spans_Are_Case_Insensitive_Sorted_And_Merged()
    {
        var spans = HighlightSpans.Compute("Report 报告 report", ["REPORT", "报告"]);
        Assert.Equal([new HighlightSpan(0, 6), new HighlightSpan(7, 2), new HighlightSpan(10, 6)], spans);
    }

    [Fact]
    public void Overlapping_Terms_Merge_Into_One_Span()
    {
        var spans = HighlightSpans.Compute("abcdef", ["abcd", "cdef"]);
        Assert.Equal([new HighlightSpan(0, 6)], spans);
    }

    [Fact]
    public void Multiple_Occurrences_And_No_Match()
    {
        Assert.Equal(2, HighlightSpans.Compute("项目 项目", ["项目"]).Count);
        Assert.Empty(HighlightSpans.Compute("hello", ["xyz"]));
        Assert.Empty(HighlightSpans.Compute(null, ["x"]));
        Assert.Empty(HighlightSpans.Compute("abc", [""]));
        Assert.True(HighlightSpans.HasHit("abc", ["B"]));
    }

    [Fact]
    public void Snippet_Centers_On_First_Hit_And_Adds_Ellipses()
    {
        var body = new string('甲', 100) + "迁移" + new string('乙', 100);
        var s = SnippetBuilder.Build(body, ["迁移"], context: 10, maxLength: 140)!;
        Assert.StartsWith("…", s);
        Assert.EndsWith("…", s);
        Assert.Contains("迁移", s);
        Assert.True(s.Length < 30);
    }

    [Fact]
    public void Snippet_Collapses_Whitespace_And_Returns_Null_Without_Hit()
    {
        Assert.Equal("a b c 命中", SnippetBuilder.Build("a\n\n b\t c   命中", ["命中"]));
        Assert.Null(SnippetBuilder.Build("没有", ["x"]));
        Assert.Null(SnippetBuilder.Build(null, ["x"]));
    }

    [Fact]
    public void Html_Highlighter_Marks_Text_Nodes_Only()
    {
        var html = "<p class=\"report\">The <b>Report</b> says report</p><a href=\"x/report\">link</a>";
        var result = HtmlHighlighter.Apply(html, ["report"]);
        Assert.Contains("class=\"report\"", result);
        Assert.Contains("href=\"x/report\"", result);
        Assert.Equal(2, CountOf(result, HtmlHighlighter.MarkOpen));
        Assert.Contains($"<b>{HtmlHighlighter.MarkOpen}Report{HtmlHighlighter.MarkClose}</b>", result);
    }

    [Fact]
    public void Html_Highlighter_Skips_Style_Script_And_Entities()
    {
        var html = "<style>.report{}</style><script>var report;</script><p>a &amp; b report</p>";
        var result = HtmlHighlighter.Apply(html, ["report", "amp"]);
        Assert.Contains("<style>.report{}</style>", result);
        Assert.Contains("<script>var report;</script>", result);
        Assert.Contains("&amp;", result); // 实体不会被拆开
        Assert.DoesNotContain("&" + HtmlHighlighter.MarkOpen, result);
        Assert.Equal(1, CountOf(result, HtmlHighlighter.MarkOpen));
    }

    [Fact]
    public void Html_Highlighter_Without_Terms_Returns_Input()
    {
        const string html = "<p>abc</p>";
        Assert.Same(html, HtmlHighlighter.Apply(html, []));
    }

    [Fact]
    public void Highlighted_Text_Wraps_Spans()
    {
        var h = HighlightedText.Of("项目进度", ["进度"]);
        Assert.True(h.HasHit);
        Assert.Equal(new HighlightSpan(2, 2), Assert.Single(h.Spans));
        Assert.False(HighlightedText.Plain("abc").HasHit);
    }

    [Fact]
    public void Highlight_Terms_From_Query_Split_By_Field()
    {
        var t = SearchHighlightTerms.From(SearchQueryParser.Parse("from:alice subject:报价 合同"));
        Assert.Equal(["合同"], t.Body);
        Assert.Equal(["alice", "合同"], t.Sender);
        Assert.Equal(["报价", "合同"], t.Subject);
        Assert.False(t.IsEmpty);
        Assert.True(SearchHighlightTerms.From(SearchQueryParser.Parse("has:attachment")).IsEmpty);
    }

    private static int CountOf(string text, string value)
    {
        var n = 0;
        for (var i = text.IndexOf(value, StringComparison.Ordinal); i >= 0; i = text.IndexOf(value, i + value.Length, StringComparison.Ordinal))
        {
            n++;
        }

        return n;
    }
}

public class RecentSearchTests
{
    [Fact]
    public void Add_Dedupes_Ignoring_Case_Newest_First_And_Caps_At_Ten()
    {
        var store = new MemoryRecentSearchStore();
        for (var i = 0; i < 12; i++)
        {
            store.Add($"q{i}");
        }

        store.Add("  Q5 ");
        var all = store.GetAll();
        Assert.Equal(RecentSearchList.Max, all.Count);
        Assert.Equal("Q5", all[0]);
        Assert.Single(all, x => x.Equals("q5", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain("q0", all);
        store.Add("   ");
        Assert.Equal(RecentSearchList.Max, store.GetAll().Count);
        store.Clear();
        Assert.Empty(store.GetAll());
    }

    [Fact]
    public void Settings_Store_Persists_Across_Instances()
    {
        var path = Path.Combine(Path.GetTempPath(), $"mc-settings-{Guid.NewGuid():N}.json");
        try
        {
            new SettingsRecentSearchStore(new AppSettingsStore(path)).Add("合同 has:attachment");
            var again = new SettingsRecentSearchStore(new AppSettingsStore(path));
            Assert.Equal(["合同 has:attachment"], again.GetAll());
        }
        finally
        {
            File.Delete(path);
        }
    }
}
