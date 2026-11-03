using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace MailClient.Core;

/// <summary>
/// 把 HTML / 纯文本收成列表用的短预览。营销邮件常把大段 CSS 放在 &lt;style&gt; 里，
/// 旧版只剥标签会把 CSS 当成摘要；这里先去掉 style/script/head/注释，再剥标签，并丢掉残留的样式文本。
/// </summary>
public static partial class HtmlPreview
{
    private const int DefaultMaxChars = 200;

    public static string BuildPreviewFromParts(string? plainText, string? html, int maxChars = DefaultMaxChars)
    {
        if (!string.IsNullOrWhiteSpace(plainText) && !LooksLikeCss(plainText) && !LooksLikeHtmlMarkup(plainText))
        {
            return Truncate(CollapseWhitespace(plainText), maxChars);
        }

        if (!string.IsNullOrWhiteSpace(html))
        {
            var fromHtml = HtmlToPreview(html, maxChars);
            if (!string.IsNullOrWhiteSpace(fromHtml) && !LooksLikeCss(fromHtml))
            {
                return fromHtml;
            }
        }

        if (!string.IsNullOrWhiteSpace(plainText))
        {
            if (LooksLikeHtmlMarkup(plainText))
            {
                return HtmlToPreview(plainText, maxChars);
            }

            var cleaned = Truncate(CollapseWhitespace(plainText), maxChars);
            return LooksLikeCss(cleaned) ? string.Empty : cleaned;
        }

        return string.Empty;
    }

    public static string HtmlToPreview(string? html, int maxChars = DefaultMaxChars)
    {
        if (string.IsNullOrWhiteSpace(html))
        {
            return string.Empty;
        }

        var text = StripToText(html);
        text = StripCssResidue(text);
        return Truncate(CollapseWhitespace(text), maxChars);
    }

    /// <summary>剥 HTML 得到纯文本（仍可能含 style 残留，预览请用 <see cref="HtmlToPreview"/>）。</summary>
    public static string StripToText(string? html)
    {
        if (string.IsNullOrWhiteSpace(html))
        {
            return string.Empty;
        }

        var cleaned = html;
        cleaned = HtmlCommentRegex().Replace(cleaned, " ");
        cleaned = StyleScriptHeadRegex().Replace(cleaned, " ");
        cleaned = BreakTagRegex().Replace(cleaned, " ");
        cleaned = BlockCloseRegex().Replace(cleaned, " ");
        cleaned = TagRegex().Replace(cleaned, " ");
        return WebUtility.HtmlDecode(cleaned);
    }

    public static string Truncate(string? text, int maxChars = DefaultMaxChars)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var normalized = CollapseWhitespace(text);
        if (normalized.Length <= maxChars)
        {
            return normalized;
        }

        return normalized[..maxChars].TrimEnd() + "…";
    }

    public static bool LooksLikeCss(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var t = text.Trim();
        if (t.Contains("mso-table", StringComparison.OrdinalIgnoreCase) ||
            t.Contains("#outlook", StringComparison.OrdinalIgnoreCase) ||
            t.Contains("border-collapse", StringComparison.OrdinalIgnoreCase) ||
            t.Contains("-webkit-text-size-adjust", StringComparison.OrdinalIgnoreCase) ||
            t.Contains("body.generic", StringComparison.OrdinalIgnoreCase) ||
            t.Contains("mso-table-lspace", StringComparison.OrdinalIgnoreCase) ||
            t.Contains("table-layout:", StringComparison.OrdinalIgnoreCase) ||
            t.Contains("body, table", StringComparison.OrdinalIgnoreCase) ||
            t.Contains("table td", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var braces = 0;
        var colons = 0;
        var semis = 0;
        foreach (var ch in t)
        {
            switch (ch)
            {
                case '{' or '}':
                    braces++;
                    break;
                case ':':
                    colons++;
                    break;
                case ';':
                    semis++;
                    break;
            }
        }

        if (braces >= 2 && semis >= 2)
        {
            return true;
        }

        if (braces >= 1 && colons >= 3 && semis >= 2)
        {
            return true;
        }

        if (CssSelectorStartRegex().IsMatch(t))
        {
            return true;
        }

        // 常见预览污染：以 "-- table {" 这类注释式选择器开头
        if (t.StartsWith("-- ", StringComparison.Ordinal) && t.Contains('{'))
        {
            return true;
        }

        return false;
    }

    public static bool LooksLikeHtmlMarkup(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var t = text.TrimStart();
        return t.StartsWith("<!DOCTYPE", StringComparison.OrdinalIgnoreCase)
               || t.StartsWith("<html", StringComparison.OrdinalIgnoreCase)
               || t.StartsWith("<head", StringComparison.OrdinalIgnoreCase)
               || t.StartsWith("<body", StringComparison.OrdinalIgnoreCase)
               || t.StartsWith("<style", StringComparison.OrdinalIgnoreCase)
               || (t.Contains('<') && t.Contains('>') && t.Contains("</", StringComparison.Ordinal));
    }

    /// <summary>空显示名时回退到邮箱本地部分，再不行用完整邮箱。</summary>
    public static string ResolveDisplayName(string? proposed, string? emailAddress)
    {
        if (!string.IsNullOrWhiteSpace(proposed))
        {
            return proposed.Trim();
        }

        var email = (emailAddress ?? string.Empty).Trim();
        var at = email.IndexOf('@');
        if (at > 0)
        {
            return email[..at];
        }

        return string.IsNullOrEmpty(email) ? "未命名账号" : email;
    }

    private static string StripCssResidue(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return text;
        }

        // 去掉残留的 { ... } 样式块
        var withoutBlocks = CssBlockRegex().Replace(text, " ");
        // 按空白切开，丢掉像属性声明的碎片
        var parts = withoutBlocks.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        var sb = new StringBuilder(withoutBlocks.Length);
        foreach (var part in parts)
        {
            if (IsCssToken(part))
            {
                continue;
            }

            if (sb.Length > 0)
            {
                sb.Append(' ');
            }

            sb.Append(part);
        }

        return sb.ToString();
    }

    private static bool IsCssToken(string token)
    {
        if (token.Length == 0)
        {
            return false;
        }

        if (token.Contains('{') || token.Contains('}'))
        {
            return true;
        }

        // color:#fff;  / margin:0;  / mso-xxx
        if (token.Contains(':') && (token.EndsWith(';') || token.Contains('-')))
        {
            return true;
        }

        if (token.StartsWith('#') && token.Length > 1 && !char.IsLetter(token[1]))
        {
            // #fff / #000000 — 颜色，不是话题标签文案
            return token.Skip(1).All(c => char.IsAsciiHexDigit(c));
        }

        if (token.StartsWith('.') && token.Length > 1 && token.Contains('{', StringComparison.Ordinal) == false
            && token.All(c => c == '.' || c == '-' || c == '_' || char.IsLetterOrDigit(c)))
        {
            // .wrapperMod 这类选择器碎片：若几乎全是标识符且含大写驼峰常见于 CSS class
            return token.Count(char.IsUpper) >= 1 && token.Length >= 8;
        }

        return false;
    }

    public static string CollapseWhitespace(string text)
    {
        var sb = new StringBuilder(text.Length);
        var prevWs = false;
        foreach (var ch in text)
        {
            if (char.IsWhiteSpace(ch))
            {
                if (!prevWs)
                {
                    sb.Append(' ');
                    prevWs = true;
                }
            }
            else
            {
                sb.Append(ch);
                prevWs = false;
            }
        }

        return sb.ToString().Trim();
    }

    [GeneratedRegex(@"<!--.*?-->", RegexOptions.Singleline)]
    private static partial Regex HtmlCommentRegex();

    [GeneratedRegex(@"<\s*(style|script|noscript|head|title)\b[^>]*>.*?<\s*/\s*\1\s*>",
        RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex StyleScriptHeadRegex();

    [GeneratedRegex(@"<\s*br\s*/?\s*>", RegexOptions.IgnoreCase)]
    private static partial Regex BreakTagRegex();

    [GeneratedRegex(@"<\s*/\s*(p|div|tr|li|h[1-6]|table|td|th)\s*>", RegexOptions.IgnoreCase)]
    private static partial Regex BlockCloseRegex();

    [GeneratedRegex(@"<[^>]+>")]
    private static partial Regex TagRegex();

    [GeneratedRegex(@"\{[^{}]*\}", RegexOptions.Singleline)]
    private static partial Regex CssBlockRegex();

    [GeneratedRegex(@"^[#.\-_\w]+\s*\{")]
    private static partial Regex CssSelectorStartRegex();
}
