using System.Text;
using System.Text.RegularExpressions;
using MailClient.Core;
using MimeKit;

namespace MailClient.Providers.Imap;

/// <summary>MimeKit 解析辅助：预览文本、发件人、中文编码兜底与附件名修复（无网络）。</summary>
public static partial class MimeMessageMapper
{
    private static readonly Encoding StrictUtf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
    private static readonly Encoding Gb18030Strict;
    private static readonly Encoding Gb18030Permissive;

    static MimeMessageMapper()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        Gb18030Strict = Encoding.GetEncoding("gb18030", EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
        Gb18030Permissive = Encoding.GetEncoding("gb18030");
    }

    [GeneratedRegex(@"charset\s*=\s*[""']?\s*([A-Za-z0-9_\-]+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex HtmlMetaCharsetRegex();

    public static string BuildPreview(MimeMessage message, int maxChars = 200)
    {
        ArgumentNullException.ThrowIfNull(message);
        var (text, html) = ExtractTextBodies(message);
        return HtmlPreview.BuildPreviewFromParts(text, html, maxChars);
    }

    public static string TruncatePreview(string? text, int maxChars = 200)
        => HtmlPreview.Truncate(text, maxChars);

    public static (string Address, string? Name) GetFrom(MimeMessage message)
    {
        var from = message.From.Mailboxes.FirstOrDefault();
        if (from is null)
        {
            return (string.Empty, null);
        }

        return (from.Address, string.IsNullOrWhiteSpace(from.Name) ? null : from.Name);
    }

    public static string FormatFromDisplay(string address, string? name)
    {
        if (!string.IsNullOrWhiteSpace(name))
        {
            return $"{name} <{address}>";
        }

        return address;
    }

    public static bool HasAttachments(MimeMessage message)
    {
        return message.Attachments.Any() ||
               message.BodyParts.OfType<MimePart>().Any(p => p.IsAttachment);
    }


    /// <summary>从 MIME 抽出预览、正文（优先 HTML）与附件元数据。</summary>
    public static MimeContent ExtractContent(MimeMessage message, int previewMaxChars = 200)
    {
        ArgumentNullException.ThrowIfNull(message);
        var (text, html) = ExtractTextBodies(message);

        // BodyText：优先真实 text/plain；没有则从 HTML 剥出可读文本（不再把 CSS 写进去）
        string bodyText;
        if (!string.IsNullOrWhiteSpace(text) && !HtmlPreview.LooksLikeHtmlMarkup(text) && !HtmlPreview.LooksLikeCss(text))
        {
            bodyText = text;
        }
        else if (!string.IsNullOrWhiteSpace(html))
        {
            bodyText = HtmlPreview.StripToText(html);
            if (HtmlPreview.LooksLikeCss(bodyText))
            {
                bodyText = HtmlPreview.HtmlToPreview(html, maxChars: 8_000);
            }
        }
        else
        {
            bodyText = text ?? string.Empty;
        }

        var attachments = ListAttachmentInfo(message, includeInlineBytes: true);
        string? bodyHtml = string.IsNullOrWhiteSpace(html) ? null : html;
        if (bodyHtml is not null && CidHtmlRewriter.ContainsUnresolvedCid(bodyHtml))
        {
            bodyHtml = CidHtmlRewriter.Rewrite(bodyHtml, attachments);
        }

        var preview = HtmlPreview.BuildPreviewFromParts(text, html, previewMaxChars);
        return new MimeContent(preview, bodyText, bodyHtml, attachments);
    }

    public static IReadOnlyList<MessageAttachmentInfo> ListAttachmentInfo(MimeMessage message, bool includeInlineBytes = false)
    {
        ArgumentNullException.ThrowIfNull(message);
        var list = new List<MessageAttachmentInfo>();
        foreach (var part in message.BodyParts.OfType<MimePart>())
        {
            var isAttachment = part.IsAttachment ||
                               (part.ContentDisposition?.Disposition?.Equals("attachment", StringComparison.OrdinalIgnoreCase) == true);
            var isInline = !isAttachment &&
                           (!string.IsNullOrEmpty(part.ContentId) ||
                            part.ContentDisposition?.Disposition?.Equals("inline", StringComparison.OrdinalIgnoreCase) == true);
            if (!isAttachment && !isInline)
            {
                continue;
            }

            // 跳过纯正文 text/plain / text/html（非附件）
            if (!isAttachment && part.ContentType?.IsMimeType("text", "plain") == true)
            {
                continue;
            }

            if (!isAttachment && part.ContentType?.IsMimeType("text", "html") == true)
            {
                continue;
            }

            var fileName = RepairFileName(part.FileName);
            if (string.IsNullOrWhiteSpace(fileName))
            {
                fileName = isInline ? $"inline-{list.Count + 1}" : $"attachment-{list.Count + 1}";
            }

            var contentId = string.IsNullOrWhiteSpace(part.ContentId) ? null : part.ContentId.Trim('<', '>');
            byte[]? bytes = null;
            long size = part.Content?.Stream is { CanSeek: true } s ? Math.Max(0, s.Length) : 0;
            // 内嵌图（有 Content-ID）在拉全文时解码进内存，供 cid → data URI
            if (includeInlineBytes && !string.IsNullOrEmpty(contentId) && part.Content is not null)
            {
                using var ms = new MemoryStream();
                part.Content.DecodeTo(ms);
                bytes = ms.ToArray();
                if (size <= 0)
                {
                    size = bytes.LongLength;
                }
            }

            list.Add(new MessageAttachmentInfo(
                FileName: fileName,
                ContentType: part.ContentType?.MimeType,
                SizeBytes: size,
                ContentId: contentId,
                IsInline: isInline && !isAttachment,
                RemoteId: null,
                ContentBytes: bytes));
        }

        return list;
    }

    /// <summary>按文件名在 MIME 中找附件并写出到流。</summary>
    public static bool TryWriteAttachment(MimeMessage message, string fileName, Stream destination)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        ArgumentNullException.ThrowIfNull(destination);
        var wantCid = CidHtmlRewriter.NormalizeContentId(fileName);
        foreach (var part in message.BodyParts.OfType<MimePart>())
        {
            var repairedName = RepairFileName(part.FileName);
            var nameMatch = string.Equals(part.FileName, fileName, StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(repairedName, fileName, StringComparison.OrdinalIgnoreCase);
            var cid = string.IsNullOrWhiteSpace(part.ContentId) ? null : part.ContentId.Trim('<', '>');
            var cidMatch = cid is not null &&
                           (string.Equals(cid, fileName, StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(CidHtmlRewriter.NormalizeContentId(cid), wantCid, StringComparison.OrdinalIgnoreCase));
            if (!nameMatch && !cidMatch)
            {
                continue;
            }

            if (part.Content is null)
            {
                continue;
            }

            part.Content.DecodeTo(destination);
            return true;
        }

        return false;
    }

    /// <summary>兼容旧调用：剥 HTML 为纯文本。列表预览请改用 <see cref="HtmlPreview.HtmlToPreview"/>。</summary>
    public static string StripHtml(string? html) => HtmlPreview.StripToText(html);

    /// <summary>
    /// 抽取 text/plain 与 text/html 正文，并对中文旧编码（GBK / GB2312 / GB18030）做兜底解码：
    /// 支付宝、银行账单等自动邮件常漏写 MIME charset 或误标为 utf-8，直接读 <see cref="TextPart.Text"/> 会变成大量乱码（）。
    /// </summary>
    private static (string? Text, string? Html) ExtractTextBodies(MimeMessage message)
    {
        string? html = null;
        string? text = null;

        foreach (var part in message.BodyParts.OfType<TextPart>())
        {
            if (part.IsAttachment)
            {
                continue;
            }

            if (string.IsNullOrWhiteSpace(html) && part.IsHtml)
            {
                html = DecodeTextPart(part);
            }
            else if (string.IsNullOrWhiteSpace(text) && part.IsPlain)
            {
                text = DecodeTextPart(part);
            }

            if (!string.IsNullOrWhiteSpace(html) && !string.IsNullOrWhiteSpace(text))
            {
                break;
            }
        }

        html ??= message.HtmlBody;
        text ??= message.TextBody;
        return (text, html);
    }

    internal static string DecodeTextPart(TextPart part)
    {
        if (part.Content is null)
        {
            return part.Text ?? string.Empty;
        }

        using var ms = new MemoryStream();
        part.Content.DecodeTo(ms);
        var bytes = ms.ToArray();
        if (bytes.Length == 0)
        {
            return string.Empty;
        }

        var declared = part.ContentType?.Charset?.Trim();
        var metaCharset = part.IsHtml ? DetectHtmlMetaCharset(bytes) : null;

        // 1. 明确声明了非 UTF-8 / 非 ASCII / 非 Latin-1 的编码（如 gbk / gb2312 / gb18030 / big5 / shift_jis）
        if (!string.IsNullOrEmpty(declared) && !IsDefaultOrUtf8Like(declared))
        {
            var enc = ResolveEncoding(declared);
            if (enc is not null)
            {
                return enc.GetString(bytes);
            }
        }

        // 2. 先尝试严格 UTF-8：若字节完全合法且不是误标，直接按 UTF-8 解码
        try
        {
            return StrictUtf8.GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            // 字节不是合法 UTF-8：进入兜底解码
        }

        // 3. 优先尊重 HTML <meta charset=...>（例如支付宝邮件头漏标或误标 utf-8，但 HTML 内写了 charset=gbk）
        if (!string.IsNullOrEmpty(metaCharset) && !IsDefaultOrUtf8Like(metaCharset))
        {
            var metaEnc = ResolveEncoding(metaCharset);
            if (metaEnc is not null)
            {
                return metaEnc.GetString(bytes);
            }
        }

        // 4. 尝试严格 GB18030（超集，完整覆盖 GB2312 与 GBK）
        try
        {
            return Gb18030Strict.GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            return Gb18030Permissive.GetString(bytes);
        }
    }

    /// <summary>
    /// 修复被误按 ISO-8859-1 解码的附件文件名（例如原始 GBK 字节被解成 “Ö§¸¶±¦½»Ò×Ã÷Ï¸.zip”）。
    /// 仅在字符串全为 Latin-1 字节且存在连续高位字节时尝试按 UTF-8 / GB18030 还原，避免误伤法语/德语等正常 Latin-1 文件名。
    /// </summary>
    public static string? RepairFileName(string? fileName)
    {
        if (string.IsNullOrEmpty(fileName))
        {
            return fileName;
        }

        var hasConsecutiveHighBytes = false;
        var prevHigh = false;
        for (var i = 0; i < fileName.Length; i++)
        {
            var c = fileName[i];
            if (c > 0xFF)
            {
                return fileName; // 已含非 Latin-1 字符（如正常中文），无需修复
            }

            var isHigh = c >= 0x80;
            if (isHigh && prevHigh)
            {
                hasConsecutiveHighBytes = true;
            }

            prevHigh = isHigh;
        }

        if (!hasConsecutiveHighBytes)
        {
            return fileName;
        }

        var rawBytes = Encoding.Latin1.GetBytes(fileName);

        // 先试严格 UTF-8
        try
        {
            var utf8 = StrictUtf8.GetString(rawBytes);
            if (ContainsCjkOrNonLatin1(utf8))
            {
                return utf8;
            }
        }
        catch (DecoderFallbackException)
        {
        }

        // 再试严格 GB18030（覆盖 GB2312 / GBK）
        try
        {
            var gbk = Gb18030Strict.GetString(rawBytes);
            if (ContainsCjk(gbk))
            {
                return gbk;
            }
        }
        catch (DecoderFallbackException)
        {
        }

        return fileName;
    }

    private static string? DetectHtmlMetaCharset(byte[] bytes)
    {
        var prefixLen = Math.Min(bytes.Length, 4096);
        var head = Encoding.ASCII.GetString(bytes, 0, prefixLen);
        var match = HtmlMetaCharsetRegex().Match(head);
        return match.Success ? match.Groups[1].Value.Trim() : null;
    }

    private static bool IsDefaultOrUtf8Like(string charset)
        => charset.Equals("utf-8", StringComparison.OrdinalIgnoreCase) ||
           charset.Equals("utf8", StringComparison.OrdinalIgnoreCase) ||
           charset.Equals("us-ascii", StringComparison.OrdinalIgnoreCase) ||
           charset.Equals("ascii", StringComparison.OrdinalIgnoreCase) ||
           charset.Equals("iso-8859-1", StringComparison.OrdinalIgnoreCase) ||
           charset.Equals("latin1", StringComparison.OrdinalIgnoreCase);

    private static Encoding? ResolveEncoding(string charset)
    {
        if (charset.Equals("gb2312", StringComparison.OrdinalIgnoreCase) ||
            charset.Equals("gbk", StringComparison.OrdinalIgnoreCase) ||
            charset.Equals("gb18030", StringComparison.OrdinalIgnoreCase) ||
            charset.Equals("gb_2312-80", StringComparison.OrdinalIgnoreCase) ||
            charset.Equals("x-gbk", StringComparison.OrdinalIgnoreCase))
        {
            return Gb18030Permissive;
        }

        try
        {
            return Encoding.GetEncoding(charset);
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    private static bool ContainsCjk(string text)
    {
        foreach (var c in text)
        {
            if (c is >= '\u4E00' and <= '\u9FFF' or >= '\u3400' and <= '\u4DBF')
            {
                return true;
            }
        }

        return false;
    }

    private static bool ContainsCjkOrNonLatin1(string text)
    {
        foreach (var c in text)
        {
            if (c > 0xFF)
            {
                return true;
            }
        }

        return false;
    }
}

/// <summary>MIME 解析结果：预览 + 正文 + 附件元数据。</summary>
public sealed record MimeContent(
    string Preview,
    string BodyText,
    string? BodyHtml,
    IReadOnlyList<MessageAttachmentInfo> Attachments);
