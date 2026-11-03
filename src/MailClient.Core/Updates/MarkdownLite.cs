using System.Text.RegularExpressions;

namespace MailClient.Core.Updates;

public enum MarkdownBlockKind
{
    Heading,
    Bullet,
    Paragraph,
    Code
}

public sealed record MarkdownBlock(MarkdownBlockKind Kind, string Text, int Level = 0);

/// <summary>
/// 把发布说明（Markdown）转成一串简单的块，界面按标题 / 列表 / 段落渲染。
/// 不是完整的 Markdown：只处理标题、列表、代码块、段落，并去掉行内的 **粗体**、`代码`、[链接](地址)、图片和 HTML 标签。
/// </summary>
public static partial class MarkdownLite
{
    private const int MaxChars = 20_000;

    [GeneratedRegex(@"!\[[^\]]*\]\([^)]*\)")]
    private static partial Regex ImagePattern();

    [GeneratedRegex(@"\[([^\]]+)\]\([^)]*\)")]
    private static partial Regex LinkPattern();

    [GeneratedRegex(@"<[^>]+>")]
    private static partial Regex HtmlPattern();

    [GeneratedRegex(@"(\*\*|__)(.+?)\1")]
    private static partial Regex BoldPattern();

    [GeneratedRegex(@"(?<![\w*])\*(?!\s)([^*]+?)\*(?![\w*])")]
    private static partial Regex ItalicPattern();

    [GeneratedRegex(@"`([^`]+)`")]
    private static partial Regex CodePattern();

    [GeneratedRegex(@"^\s{0,3}(#{1,6})\s+(.*?)\s*#*\s*$")]
    private static partial Regex HeadingPattern();

    [GeneratedRegex(@"^\s*(?:[-*+]|\d+[.)])\s+(.*)$")]
    private static partial Regex BulletPattern();

    [GeneratedRegex(@"^\s*(?:-{3,}|\*{3,}|_{3,})\s*$")]
    private static partial Regex RulePattern();

    public static IReadOnlyList<MarkdownBlock> Parse(string? markdown)
    {
        var blocks = new List<MarkdownBlock>();
        if (string.IsNullOrWhiteSpace(markdown))
        {
            return blocks;
        }

        var text = markdown.Length > MaxChars ? markdown[..MaxChars] : markdown;
        var paragraph = new List<string>();
        var inCode = false;
        var code = new List<string>();

        void FlushParagraph()
        {
            if (paragraph.Count > 0)
            {
                var joined = Inline(string.Join(" ", paragraph));
                if (joined.Length > 0)
                {
                    blocks.Add(new MarkdownBlock(MarkdownBlockKind.Paragraph, joined));
                }

                paragraph.Clear();
            }
        }

        foreach (var rawLine in text.Replace("\r\n", "\n").Split('\n'))
        {
            var line = rawLine.TrimEnd();
            if (line.TrimStart().StartsWith("```", StringComparison.Ordinal))
            {
                if (inCode)
                {
                    if (code.Count > 0)
                    {
                        blocks.Add(new MarkdownBlock(MarkdownBlockKind.Code, string.Join("\n", code)));
                    }

                    code.Clear();
                }
                else
                {
                    FlushParagraph();
                }

                inCode = !inCode;
                continue;
            }

            if (inCode)
            {
                code.Add(line);
                continue;
            }

            if (line.Trim().Length == 0 || RulePattern().IsMatch(line))
            {
                FlushParagraph();
                continue;
            }

            var h = HeadingPattern().Match(line);
            if (h.Success)
            {
                FlushParagraph();
                var t = Inline(h.Groups[2].Value);
                if (t.Length > 0)
                {
                    blocks.Add(new MarkdownBlock(MarkdownBlockKind.Heading, t, h.Groups[1].Length));
                }

                continue;
            }

            var b = BulletPattern().Match(line);
            if (b.Success)
            {
                FlushParagraph();
                var t = Inline(b.Groups[1].Value);
                if (t.Length > 0)
                {
                    blocks.Add(new MarkdownBlock(MarkdownBlockKind.Bullet, t));
                }

                continue;
            }

            paragraph.Add(line.Trim());
        }

        if (inCode && code.Count > 0)
        {
            blocks.Add(new MarkdownBlock(MarkdownBlockKind.Code, string.Join("\n", code)));
        }

        FlushParagraph();
        return blocks;
    }

    /// <summary>去掉行内标记，保留文字。</summary>
    public static string Inline(string text)
    {
        var s = ImagePattern().Replace(text, string.Empty);
        s = LinkPattern().Replace(s, "$1");
        s = HtmlPattern().Replace(s, string.Empty);
        s = BoldPattern().Replace(s, "$2");
        s = ItalicPattern().Replace(s, "$1");
        s = CodePattern().Replace(s, "$1");
        return s.Trim();
    }
}