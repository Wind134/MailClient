using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using MailClient.Core;
using MailClient.Core.Sending;
using MimeKit;
using MimeKit.Utils;

namespace MailClient.Providers.Outbox;

/// <summary>
/// 把 <see cref="OutgoingMailMessage"/> 构造成标准 MIME：
/// 发件人（显示名 + 地址）、To/Cc/Bcc（严格解析）、Subject、Message-ID / Date、In-Reply-To / References、
/// text/plain + text/html 的 multipart/alternative，以及附件（缺失的文件会报错而不是悄悄丢掉）。纯函数，可单测。
/// </summary>
public static class OutgoingMimeBuilder
{
    public static MimeMessage Build(
        OutgoingMailMessage message,
        string fromAddress,
        string? fromDisplayName,
        DateTimeOffset date)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (!MailboxAddress.TryParse(fromAddress, out var from))
        {
            throw new ClassifiedFailureException(FailureKind.Configuration, $"发件地址无效：{fromAddress}");
        }

        var mime = new MimeMessage();
        mime.From.Add(new MailboxAddress(
            string.IsNullOrWhiteSpace(fromDisplayName) ? null : fromDisplayName.Trim(),
            from.Address));

        AddAll(mime.To, message.To, "收件人");
        AddAll(mime.Cc, message.Cc, "抄送");
        AddAll(mime.Bcc, message.Bcc, "密送");
        if (mime.To.Count + mime.Cc.Count + mime.Bcc.Count == 0)
        {
            throw new ClassifiedFailureException(FailureKind.RecipientRejected, "没有收件人。");
        }

        mime.Subject = message.Subject ?? string.Empty;
        mime.Date = date;

        var domain = from.Address.Contains('@') ? from.Address[(from.Address.IndexOf('@') + 1)..] : "localhost";
        mime.MessageId = string.IsNullOrWhiteSpace(message.MessageId)
            ? MimeUtils.GenerateMessageId(domain)
            : message.MessageId!.Trim().Trim('<', '>');

        if (!string.IsNullOrWhiteSpace(message.InReplyTo))
        {
            mime.InReplyTo = message.InReplyTo!.Trim().Trim('<', '>');
        }

        if (message.References is { Count: > 0 })
        {
            foreach (var reference in message.References.Where(r => !string.IsNullOrWhiteSpace(r)))
            {
                mime.References.Add(reference.Trim().Trim('<', '>'));
            }
        }

        var html = message.BodyHtml ?? string.Empty;
        var builder = new BodyBuilder
        {
            TextBody = message.BodyText ?? HtmlToText(html),
            HtmlBody = html
        };

        foreach (var path in message.AttachmentPaths ?? Array.Empty<string>())
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                continue;
            }

            if (!File.Exists(path))
            {
                throw new ClassifiedFailureException(FailureKind.MessageRejected, $"附件文件不存在：{Path.GetFileName(path)}", new FileNotFoundException(path));
            }

            builder.Attachments.Add(path);
        }

        mime.Body = builder.ToMessageBody();
        return mime;
    }

    /// <summary>解析 + 校验地址列表；返回第一个无效地址（没有则 null）。</summary>
    public static string? FindInvalidAddress(IEnumerable<string> addresses)
    {
        foreach (var a in addresses)
        {
            if (!MailboxAddress.TryParse(a, out var parsed) || !parsed.Address.Contains('@') || parsed.Address.EndsWith('@'))
            {
                return a;
            }
        }

        return null;
    }

    /// <summary>用于“只有 HTML”的邮件生成纯文本备选。</summary>
    public static string HtmlToText(string html)
    {
        if (string.IsNullOrEmpty(html))
        {
            return string.Empty;
        }

        var s = Regex.Replace(html, @"<(script|style)\b[^>]*>.*?</\1>", string.Empty, RegexOptions.IgnoreCase | RegexOptions.Singleline);
        s = Regex.Replace(s, @"<br\s*/?>|</(p|div|tr|li|h[1-6]|blockquote)>", "\n", RegexOptions.IgnoreCase);
        s = Regex.Replace(s, @"<[^>]+>", string.Empty);
        s = WebUtility.HtmlDecode(s).Replace("\r\n", "\n");
        s = Regex.Replace(s, @"\n{3,}", "\n\n");
        return s.Trim();
    }

    /// <summary>回复 / 转发正文里的引用块（纯文本，每行加 "> "）。</summary>
    public static string QuoteText(string original)
    {
        var sb = new StringBuilder();
        foreach (var line in (original ?? string.Empty).Replace("\r\n", "\n").Split('\n'))
        {
            sb.Append("> ").Append(line).Append('\n');
        }

        return sb.ToString().TrimEnd('\n');
    }

    private static void AddAll(InternetAddressList target, IEnumerable<string>? addresses, string label)
    {
        foreach (var a in addresses ?? Array.Empty<string>())
        {
            if (!MailboxAddress.TryParse(a, out var parsed) || !parsed.Address.Contains('@'))
            {
                throw new ClassifiedFailureException(FailureKind.RecipientRejected, $"{label}地址无效：{a}");
            }

            target.Add(parsed);
        }
    }
}
