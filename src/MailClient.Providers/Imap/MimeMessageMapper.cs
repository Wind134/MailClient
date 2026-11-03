using MailClient.Core;
using MimeKit;

namespace MailClient.Providers.Imap;

/// <summary>MimeKit 解析辅助：预览文本、发件人等（无网络）。</summary>
public static class MimeMessageMapper
{
    public static string BuildPreview(MimeMessage message, int maxChars = 200)
    {
        ArgumentNullException.ThrowIfNull(message);
        return HtmlPreview.BuildPreviewFromParts(message.TextBody, message.HtmlBody, maxChars);
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
        var html = message.HtmlBody;
        var text = message.TextBody;
        // 少数服务器把 alternative 嵌得很深，MimeKit 的 HtmlBody/TextBody 可能拿不到：再扫一遍正文部件
        if (string.IsNullOrWhiteSpace(html) || string.IsNullOrWhiteSpace(text))
        {
            foreach (var part in message.BodyParts.OfType<TextPart>())
            {
                if (part.IsAttachment)
                {
                    continue;
                }

                if (string.IsNullOrWhiteSpace(html) && part.IsHtml)
                {
                    html = part.Text;
                }
                else if (string.IsNullOrWhiteSpace(text) && part.IsPlain)
                {
                    text = part.Text;
                }
            }
        }

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
            if (!isAttachment && part.ContentType.IsMimeType("text", "plain"))
            {
                continue;
            }

            if (!isAttachment && part.ContentType.IsMimeType("text", "html"))
            {
                continue;
            }

            var fileName = part.FileName;
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
            var nameMatch = string.Equals(part.FileName, fileName, StringComparison.OrdinalIgnoreCase);
            var cid = string.IsNullOrWhiteSpace(part.ContentId) ? null : part.ContentId.Trim('<', '>');
            var cidMatch = cid is not null &&
                           (string.Equals(cid, fileName, StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(CidHtmlRewriter.NormalizeContentId(cid), wantCid, StringComparison.OrdinalIgnoreCase));
            if (!nameMatch && !cidMatch)
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
}

/// <summary>MIME 解析结果：预览 + 正文 + 附件元数据。</summary>
public sealed record MimeContent(
    string Preview,
    string BodyText,
    string? BodyHtml,
    IReadOnlyList<MessageAttachmentInfo> Attachments);
