using System.Text.RegularExpressions;

namespace MailClient.Core;

/// <summary>
/// 把 HTML 里的 <c>cid:</c> 内嵌图引用改写成 WebView2 能直接加载的 data URI。
/// Content-ID 会去掉 &lt;&gt;、做 URL 解码，大小写不敏感；找不到对应附件的 cid 原样保留。
/// </summary>
public static partial class CidHtmlRewriter
{
    // src="cid:..." / src='cid:...' / src=cid:...（无引号，直到空白或 >）
    [GeneratedRegex(
        @"src\s*=\s*(?:""(?<q>cid:[^""]+)""|'(?<q>cid:[^']+)'|(?<u>cid:[^\s>]+))",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SrcCidRegex();

    // CSS url(cid:...) / url("cid:...") / url('cid:...')
    [GeneratedRegex(
        @"url\(\s*(?:""(?<q>cid:[^""]+)""|'(?<q>cid:[^']+)'|(?<u>cid:[^)\s]+))\s*\)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex UrlCidRegex();

    public static bool ContainsUnresolvedCid(string? html)
    {
        if (string.IsNullOrEmpty(html))
        {
            return false;
        }

        return html.Contains("cid:", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>规范化 Content-ID：去空白、去角括号、URL 解码。</summary>
    public static string NormalizeContentId(string? contentId)
    {
        if (string.IsNullOrWhiteSpace(contentId))
        {
            return string.Empty;
        }

        var s = contentId.Trim();
        if (s.StartsWith("cid:", StringComparison.OrdinalIgnoreCase))
        {
            s = s[4..];
        }

        s = s.Trim().Trim('<', '>').Trim();
        try
        {
            s = Uri.UnescapeDataString(s);
        }
        catch (UriFormatException)
        {
            // 保留原串
        }

        return s;
    }

    public static string Rewrite(string html, IEnumerable<InlineCidImage> images)
    {
        ArgumentNullException.ThrowIfNull(html);
        ArgumentNullException.ThrowIfNull(images);

        var map = new Dictionary<string, InlineCidImage>(StringComparer.OrdinalIgnoreCase);
        foreach (var img in images)
        {
            var key = NormalizeContentId(img.ContentId);
            if (key.Length == 0 || img.Bytes is not { Length: > 0 })
            {
                continue;
            }

            map[key] = img;
        }

        if (map.Count == 0)
        {
            return html;
        }

        string ReplaceMatch(Match m)
        {
            var raw = m.Groups["q"].Success ? m.Groups["q"].Value : m.Groups["u"].Value;
            var key = NormalizeContentId(raw);
            if (!map.TryGetValue(key, out var img))
            {
                return m.Value; // 找不到：原样保留
            }

            var dataUri = ToDataUri(img);
            // 保持原引号风格
            if (m.Value.Contains('"', StringComparison.Ordinal))
            {
                return m.Value.StartsWith("url", StringComparison.OrdinalIgnoreCase)
                    ? $"url(\"{dataUri}\")"
                    : $"src=\"{dataUri}\"";
            }

            if (m.Value.Contains('\'', StringComparison.Ordinal))
            {
                return m.Value.StartsWith("url", StringComparison.OrdinalIgnoreCase)
                    ? $"url('{dataUri}')"
                    : $"src='{dataUri}'";
            }

            return m.Value.StartsWith("url", StringComparison.OrdinalIgnoreCase)
                ? $"url({dataUri})"
                : $"src={dataUri}";
        }

        var result = SrcCidRegex().Replace(html, ReplaceMatch);
        result = UrlCidRegex().Replace(result, ReplaceMatch);
        return result;
    }

    /// <summary>从附件元数据（含可选 ContentBytes）重写 HTML。</summary>
    public static string Rewrite(string html, IEnumerable<MessageAttachmentInfo> attachments)
    {
        ArgumentNullException.ThrowIfNull(attachments);
        return Rewrite(
            html,
            attachments
                .Where(a => !string.IsNullOrWhiteSpace(a.ContentId) && a.ContentBytes is { Length: > 0 })
                .Select(a => new InlineCidImage(a.ContentId!, a.ContentType, a.ContentBytes!)));
    }

    public static string ToDataUri(InlineCidImage img)
    {
        var mime = string.IsNullOrWhiteSpace(img.ContentType) ? "application/octet-stream" : img.ContentType!.Trim();
        // 去掉可能的 name= 参数
        var semi = mime.IndexOf(';');
        if (semi > 0)
        {
            mime = mime[..semi].Trim();
        }

        if (mime.Length == 0)
        {
            mime = "application/octet-stream";
        }

        var b64 = Convert.ToBase64String(img.Bytes);
        return "data:" + mime + ";base64," + b64;
    }
}

/// <summary>一枚可用于替换 cid: 的内嵌图。</summary>
public sealed record InlineCidImage(string ContentId, string? ContentType, byte[] Bytes);
