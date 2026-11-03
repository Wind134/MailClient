using System.Net;
using System.Text;

namespace MailClient.Core.Search;

/// <summary>高亮区间（UTF-16 下标）。</summary>
public readonly record struct HighlightSpan(int Start, int Length)
{
    public int End => Start + Length;
}

public static class HighlightSpans
{
    /// <summary>
    /// 计算 <paramref name="text"/> 中所有搜索词的命中区间：忽略大小写的子串匹配，重叠 / 相邻的区间合并，按位置排序。
    /// </summary>
    public static IReadOnlyList<HighlightSpan> Compute(string? text, IEnumerable<string> terms)
    {
        if (string.IsNullOrEmpty(text))
        {
            return Array.Empty<HighlightSpan>();
        }

        var raw = new List<HighlightSpan>();
        foreach (var term in terms)
        {
            if (string.IsNullOrEmpty(term))
            {
                continue;
            }

            var index = 0;
            while (index <= text.Length - term.Length)
            {
                var found = text.IndexOf(term, index, StringComparison.OrdinalIgnoreCase);
                if (found < 0)
                {
                    break;
                }

                raw.Add(new HighlightSpan(found, term.Length));
                index = found + Math.Max(1, term.Length);
            }
        }

        if (raw.Count == 0)
        {
            return Array.Empty<HighlightSpan>();
        }

        raw.Sort((a, b) => a.Start != b.Start ? a.Start.CompareTo(b.Start) : b.Length.CompareTo(a.Length));
        var merged = new List<HighlightSpan>();
        var current = raw[0];
        for (var i = 1; i < raw.Count; i++)
        {
            var next = raw[i];
            if (next.Start <= current.End)
            {
                current = new HighlightSpan(current.Start, Math.Max(current.End, next.End) - current.Start);
            }
            else
            {
                merged.Add(current);
                current = next;
            }
        }

        merged.Add(current);
        return merged;
    }

    public static bool HasHit(string? text, IEnumerable<string> terms) => Compute(text, terms).Count > 0;
}

/// <summary>为只在正文命中的邮件生成一行带上下文的摘要。</summary>
public static class SnippetBuilder
{
    /// <summary>
    /// 在 <paramref name="body"/> 里找到第一个命中词，截取其前后各约 <paramref name="context"/> 个字符，
    /// 空白折叠成单个空格，截断处加省略号。没有命中返回 null。
    /// </summary>
    public static string? Build(string? body, IEnumerable<string> terms, int context = 36, int maxLength = 140)
    {
        if (string.IsNullOrEmpty(body))
        {
            return null;
        }

        var spans = HighlightSpans.Compute(body, terms);
        if (spans.Count == 0)
        {
            return null;
        }

        var first = spans[0];
        var start = Math.Max(0, first.Start - context);
        var end = Math.Min(body.Length, Math.Max(first.End, first.Start) + context);
        if (end - start > maxLength)
        {
            end = start + maxLength;
        }

        // 不要把代理对切开
        if (start > 0 && start < body.Length && char.IsLowSurrogate(body[start]))
        {
            start--;
        }

        if (end < body.Length && char.IsLowSurrogate(body[end]))
        {
            end++;
        }

        var slice = CollapseWhitespace(body[start..end]);
        return (start > 0 ? "…" : string.Empty) + slice + (end < body.Length ? "…" : string.Empty);
    }

    private static string CollapseWhitespace(string text)
    {
        var sb = new StringBuilder(text.Length);
        var lastSpace = false;
        foreach (var c in text)
        {
            if (char.IsWhiteSpace(c))
            {
                if (!lastSpace)
                {
                    sb.Append(' ');
                }

                lastSpace = true;
            }
            else
            {
                sb.Append(c);
                lastSpace = false;
            }
        }

        return sb.ToString().Trim();
    }
}

/// <summary>在 HTML 的文本节点里把命中的词包上 &lt;mark&gt;；不碰标签、属性、&lt;style&gt; / &lt;script&gt; 内容和实体。</summary>
public static class HtmlHighlighter
{
    public const string MarkOpen = "<mark class=\"mc-hit\">";
    public const string MarkClose = "</mark>";

    public static string Apply(string html, IEnumerable<string> terms)
    {
        if (string.IsNullOrEmpty(html))
        {
            return html;
        }

        var termList = terms.Where(t => !string.IsNullOrEmpty(t)).ToList();
        if (termList.Count == 0)
        {
            return html;
        }

        var sb = new StringBuilder(html.Length + 64);
        var i = 0;
        string? rawTextElement = null; // 正在 <style> / <script> 内

        while (i < html.Length)
        {
            if (html[i] == '<')
            {
                if (string.CompareOrdinal(html, i, "<!--", 0, 4) == 0)
                {
                    var endComment = html.IndexOf("-->", i + 4, StringComparison.Ordinal);
                    var stop = endComment < 0 ? html.Length : endComment + 3;
                    sb.Append(html, i, stop - i);
                    i = stop;
                    continue;
                }

                var close = FindTagEnd(html, i);
                if (close < 0)
                {
                    sb.Append(html, i, html.Length - i);
                    break;
                }

                var tag = html.Substring(i, close - i + 1);
                sb.Append(tag);
                i = close + 1;

                var name = TagName(tag, out var isClosing);
                if (rawTextElement is null && !isClosing && (name == "style" || name == "script"))
                {
                    rawTextElement = name;
                }
                else if (rawTextElement is not null && isClosing && name == rawTextElement)
                {
                    rawTextElement = null;
                }

                continue;
            }

            var next = html.IndexOf('<', i);
            var textEnd = next < 0 ? html.Length : next;
            var segment = html.Substring(i, textEnd - i);
            sb.Append(rawTextElement is null ? HighlightText(segment, termList) : segment);
            i = textEnd;
        }

        return sb.ToString();
    }

    private static string HighlightText(string segment, List<string> terms)
    {
        // 把实体（&amp; &#x4e2d;）整体当成一个字符：先解码得到纯文本，但要保留原样输出。
        // 做法：逐个“字符单元”（实体或普通字符）构建 plain 文本及其到原文的映射。
        var plain = new StringBuilder(segment.Length);
        var map = new List<(int Start, int End)>(segment.Length); // plain 下标 → 原文 [Start, End)
        var k = 0;
        while (k < segment.Length)
        {
            if (segment[k] == '&')
            {
                var semi = segment.IndexOf(';', k + 1, Math.Min(10, segment.Length - k - 1));
                if (semi > 0)
                {
                    var entity = segment.Substring(k, semi - k + 1);
                    var decoded = WebUtility.HtmlDecode(entity);
                    if (decoded != entity)
                    {
                        foreach (var c in decoded)
                        {
                            plain.Append(c);
                            map.Add((k, semi + 1));
                        }

                        k = semi + 1;
                        continue;
                    }
                }
            }

            plain.Append(segment[k]);
            map.Add((k, k + 1));
            k++;
        }

        var spans = HighlightSpans.Compute(plain.ToString(), terms);
        if (spans.Count == 0)
        {
            return segment;
        }

        var sb = new StringBuilder(segment.Length + spans.Count * 32);
        var cursor = 0;
        foreach (var span in spans)
        {
            var from = map[span.Start].Start;
            var to = map[span.End - 1].End;
            if (from < cursor)
            {
                from = cursor; // 同一实体被两个区间切到：向后让步
            }

            if (to <= from)
            {
                continue;
            }

            sb.Append(segment, cursor, from - cursor);
            sb.Append(MarkOpen).Append(segment, from, to - from).Append(MarkClose);
            cursor = to;
        }

        sb.Append(segment, cursor, segment.Length - cursor);
        return sb.ToString();
    }

    private static int FindTagEnd(string html, int start)
    {
        char? quote = null;
        for (var i = start + 1; i < html.Length; i++)
        {
            var c = html[i];
            if (quote is not null)
            {
                if (c == quote)
                {
                    quote = null;
                }
            }
            else if (c is '"' or '\'')
            {
                quote = c;
            }
            else if (c == '>')
            {
                return i;
            }
        }

        return -1;
    }

    private static string TagName(string tag, out bool isClosing)
    {
        isClosing = tag.Length > 1 && tag[1] == '/';
        var i = isClosing ? 2 : 1;
        var start = i;
        while (i < tag.Length && (char.IsLetterOrDigit(tag[i])))
        {
            i++;
        }

        return tag[start..i].ToLowerInvariant();
    }
}
