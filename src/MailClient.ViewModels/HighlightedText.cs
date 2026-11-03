using MailClient.Core.Search;

namespace MailClient.ViewModels;

/// <summary>一段文字 + 其中要高亮的区间（搜索命中）；UI 用附加属性把它画到 TextBlock 上。</summary>
public sealed record HighlightedText(string Text, IReadOnlyList<HighlightSpan> Spans)
{
    public static HighlightedText Plain(string? text) => new(text ?? string.Empty, Array.Empty<HighlightSpan>());

    public static HighlightedText Of(string? text, IEnumerable<string>? terms)
    {
        var t = text ?? string.Empty;
        return terms is null ? Plain(t) : new HighlightedText(t, HighlightSpans.Compute(t, terms));
    }

    public bool HasHit => Spans.Count > 0;
}

/// <summary>列表各列要高亮的词：主题、发件人、预览 / 正文各自按搜索语法（subject: / from: / 自由词）取不同的词。</summary>
public sealed record SearchHighlightTerms(IReadOnlyList<string> Subject, IReadOnlyList<string> Sender, IReadOnlyList<string> Body, IReadOnlyList<string> All)
{
    public static SearchHighlightTerms Empty { get; } = new(Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>());

    public bool IsEmpty => All.Count == 0;

    public static SearchHighlightTerms From(SearchQuery query)
        => new(
            query.TermsFor(SearchField.Subject),
            query.TermsFor(SearchField.From),
            query.TermsFor(SearchField.Any),
            query.Terms.Where(t => t.Field != SearchField.To).Select(t => t.Text).Distinct(StringComparer.OrdinalIgnoreCase).ToList());
}
