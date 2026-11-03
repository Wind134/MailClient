using System.Net;
using System.Text;
using MailClient.Core;
using MailClient.Core.Services;

namespace MailClient.ViewModels;

public enum ComposeMode
{
    New,
    Reply,
    ReplyAll,
    Forward
}

/// <summary>
/// 打开写信窗口所需的预填信息。回复 / 转发时 <see cref="Body"/> 为空（用户只写自己的部分），
/// 原文引用放在 <see cref="QuotedHtml"/> / <see cref="QuotedText"/>，发送时自动附在正文后面。
/// </summary>
public sealed record ComposeRequest(
    ComposeMode Mode,
    Guid? AccountId,
    string To,
    string Cc,
    string Subject,
    string Body,
    string Bcc = "",
    string? InReplyTo = null,
    IReadOnlyList<string>? References = null,
    string? QuotedHtml = null,
    string? QuotedText = null,
    string? DraftId = null,
    IReadOnlyList<string>? Attachments = null,
    string? SourceMessageId = null)
{
    /// <summary>
    /// 写信窗口的单实例键（同一份草稿只开一个窗口）：服务器草稿 = "src:{邮件 Id}"（此时本地草稿可能还没保存），本地草稿 = "draft:{草稿 Id}"。
    /// 新邮件 / 回复 / 转发没有键（可以开多个）。
    /// </summary>
    public IReadOnlyList<string> WindowKeys
    {
        get
        {
            var keys = new List<string>(2);
            if (SourceMessageId is not null)
            {
                keys.Add("src:" + SourceMessageId);
            }

            if (DraftId is not null)
            {
                keys.Add("draft:" + DraftId);
            }

            return keys;
        }
    }

    /// <summary>从保存的草稿状态还原（账号、收件人、主题、正文、附件、回复上下文）。附件文件已不存在的跳过。</summary>
    public static ComposeRequest FromDraft(DraftContent draft)
        => new(
            Enum.TryParse<ComposeMode>(draft.Mode, ignoreCase: true, out var mode) ? mode : ComposeMode.New,
            draft.AccountId,
            draft.To,
            draft.Cc,
            draft.Subject,
            draft.Body,
            draft.Bcc,
            draft.InReplyTo,
            draft.References,
            draft.QuotedHtml,
            draft.QuotedText,
            draft.Id,
            (draft.Attachments ?? Array.Empty<string>()).Where(File.Exists).ToList(),
            draft.SourceMessageId);

    /// <summary>
    /// 由同步下来的服务器草稿打开：正文取已下载的纯文本，没有则只有预览（同步目前不下载正文，见 docs/drafts-and-search.md）。
    /// 会分配一个新的本地草稿 Id，并记下来源（发送 / 丢弃时一并删除服务器上的那份）。
    /// </summary>
    public static ComposeRequest FromServerDraft(MessageEntity message, IReadOnlyList<RecipientEntity> recipients, string newDraftId)
    {
        string Join(RecipientKind k) => string.Join("; ", recipients.Where(r => r.Kind == k).Select(r => r.Address));
        return new ComposeRequest(
            ComposeMode.New,
            message.AccountId,
            Join(RecipientKind.To),
            Join(RecipientKind.Cc),
            string.IsNullOrWhiteSpace(message.Subject) || message.Subject is "(无主题)" or UiStrings.NoSubject ? string.Empty : message.Subject,
            message.BodyText ?? message.Preview ?? string.Empty,
            Join(RecipientKind.Bcc),
            DraftId: newDraftId,
            SourceMessageId: message.Id);
    }

    public bool HasQuote => !string.IsNullOrEmpty(QuotedText) || !string.IsNullOrEmpty(QuotedHtml);

    public static ComposeRequest New(Guid? accountId = null) => new(ComposeMode.New, accountId, "", "", "", "");

    public static ComposeRequest Reply(MessageEntity message, IReadOnlyList<RecipientEntity> recipients, string myAddress, bool all)
    {
        var to = new List<string> { message.FromAddress };
        var cc = new List<string>();
        if (all)
        {
            foreach (var r in recipients)
            {
                if (string.Equals(r.Address, myAddress, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(r.Address, message.FromAddress, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                (r.Kind == RecipientKind.Cc ? cc : to).Add(r.Address);
            }
        }

        var (html, text) = BuildQuote(message, forward: false);
        var parentId = CleanId(message.RfcMessageId);
        return new ComposeRequest(
            all ? ComposeMode.ReplyAll : ComposeMode.Reply,
            message.AccountId,
            string.Join("; ", to.Distinct(StringComparer.OrdinalIgnoreCase)),
            string.Join("; ", cc.Distinct(StringComparer.OrdinalIgnoreCase)),
            Prefix("Re: ", message.Subject),
            string.Empty,
            InReplyTo: parentId,
            References: parentId is null ? null : [parentId],
            QuotedHtml: html,
            QuotedText: text);
    }

    public static ComposeRequest Forward(MessageEntity message)
    {
        var (html, text) = BuildQuote(message, forward: true);
        return new ComposeRequest(
            ComposeMode.Forward, message.AccountId, "", "", Prefix("Fwd: ", message.Subject), string.Empty,
            QuotedHtml: html, QuotedText: text);
    }

    private static string? CleanId(string? id)
        => string.IsNullOrWhiteSpace(id) ? null : id.Trim().Trim('<', '>');

    private static string Prefix(string prefix, string subject)
    {
        var s = subject.Trim();
        return s.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? s : prefix + s;
    }

    /// <summary>生成原文引用：回复 = “某年某月某日，某人 写道：”+ 引用块；转发 = 转发头（发件人 / 日期 / 主题）+ 原文。</summary>
    public static (string Html, string Text) BuildQuote(MessageEntity m, bool forward)
    {
        var from = string.IsNullOrWhiteSpace(m.FromName) ? m.FromAddress : $"{m.FromName} <{m.FromAddress}>";
        var date = (m.ReceivedAt ?? m.SentAt) is { } d ? TimeFormatter.FormatFull(d) : string.Empty;
        var originalText = m.BodyText ?? m.Preview ?? string.Empty;
        var originalHtml = !string.IsNullOrWhiteSpace(m.BodyHtml)
            ? m.BodyHtml!
            : WebUtility.HtmlEncode(originalText).Replace("\r\n", "\n").Replace("\n", "<br>");

        var html = new StringBuilder("<br><br>");
        var text = new StringBuilder("\n\n");
        if (forward)
        {
            html.Append("<div>------ 转发的邮件 ------<br>")
                .Append("发件人：").Append(WebUtility.HtmlEncode(from)).Append("<br>")
                .Append("日期：").Append(WebUtility.HtmlEncode(date)).Append("<br>")
                .Append("主题：").Append(WebUtility.HtmlEncode(m.Subject)).Append("</div><br>")
                .Append("<div>").Append(originalHtml).Append("</div>");
            text.Append("------ 转发的邮件 ------\n发件人：").Append(from)
                .Append("\n日期：").Append(date)
                .Append("\n主题：").Append(m.Subject)
                .Append("\n\n").Append(originalText);
        }
        else
        {
            var header = string.IsNullOrEmpty(date) ? $"{from} 写道：" : $"{date}，{from} 写道：";
            html.Append("<div>").Append(WebUtility.HtmlEncode(header)).Append("</div>")
                .Append("<blockquote style=\"margin:0 0 0 .8ex;border-left:2px solid #ccc;padding-left:1ex\">")
                .Append(originalHtml).Append("</blockquote>");
            text.Append(header).Append('\n').Append(QuoteTextLines(originalText));
        }

        return (html.ToString(), text.ToString());
    }

    private static string QuoteTextLines(string original)
    {
        var sb = new StringBuilder();
        foreach (var line in original.Replace("\r\n", "\n").Split('\n'))
        {
            sb.Append("> ").Append(line).Append('\n');
        }

        return sb.ToString().TrimEnd('\n');
    }

    /// <summary>纯文本 → 简单 HTML（转义 + 换行）。</summary>
    public static string TextToHtml(string text)
        => "<div style=\"font-family:'Segoe UI','Microsoft YaHei UI',sans-serif;font-size:14px\">"
           + WebUtility.HtmlEncode(text).Replace("\r\n", "\n").Replace("\n", "<br>")
           + "</div>";
}
