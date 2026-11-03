namespace MailClient.Core.Search;

/// <summary>搜索词作用的字段。</summary>
public enum SearchField
{
    /// <summary>主题 / 发件人 / 预览 / 正文 任一处。</summary>
    Any,
    From,
    To,
    Subject
}

/// <summary>一个搜索词（单词，或带引号的短语）。</summary>
public sealed record SearchTerm(SearchField Field, string Text, bool IsPhrase = false);

/// <summary>解析后的搜索查询：各条件之间是 AND 关系。</summary>
public sealed record SearchQuery(
    IReadOnlyList<SearchTerm> Terms,
    bool? HasAttachment = null,
    bool? IsUnread = null,
    bool? IsFlagged = null,
    DateOnly? After = null,
    DateOnly? Before = null)
{
    public static SearchQuery Empty { get; } = new(Array.Empty<SearchTerm>());

    public bool IsEmpty => Terms.Count == 0 && HasAttachment is null && IsUnread is null && IsFlagged is null && After is null && Before is null;

    /// <summary>
    /// 在某个显示位置需要高亮的词：不限字段的词总是高亮；限定了字段的词（subject: / from:）只在对应字段高亮。
    /// <paramref name="target"/> = <see cref="SearchField.Any"/> 表示预览 / 正文（只高亮不限字段的词）。
    /// </summary>
    public IReadOnlyList<string> TermsFor(SearchField target)
        => Terms
            .Where(t => t.Field == SearchField.Any || (target != SearchField.Any && t.Field == target))
            .Select(t => t.Text)
            .Where(t => t.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
}
