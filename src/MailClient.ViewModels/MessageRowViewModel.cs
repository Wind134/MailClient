using CommunityToolkit.Mvvm.ComponentModel;
using MailClient.Core;

namespace MailClient.ViewModels;

/// <summary>邮件列表中的一行。</summary>
public sealed partial class MessageRowViewModel : ObservableObject
{
    public MessageRowViewModel(MessageEntity entity, DateTimeOffset now, SearchHighlightTerms? highlight = null)
    {
        Id = entity.Id;
        AccountId = entity.AccountId;
        FolderId = entity.FolderId;
        ReceivedAt = entity.ReceivedAt ?? entity.SentAt ?? DateTimeOffset.MinValue;
        Sender = string.IsNullOrWhiteSpace(entity.FromName) ? entity.FromAddress : entity.FromName!;
        FromAddress = entity.FromAddress;
        Subject = string.IsNullOrWhiteSpace(entity.Subject) ? UiStrings.NoSubject : entity.Subject;
        Preview = entity.Preview;
        TimeText = TimeFormatter.FormatShort(ReceivedAt, now);
        HasAttachments = entity.HasAttachments;
        Category = entity.Category ?? MailCategory.Primary;
        Initials = MakeInitials(Sender);
        _isUnread = !entity.IsRead;
        _isFlagged = entity.IsFlagged;
        IsDraft = entity.IsDraft;
        IsLocalDraft = entity.Id.StartsWith(Storage.MailDatabase.DraftMessagePrefix, StringComparison.Ordinal);
        if (IsDraft)
        {
            // 草稿行的“发件人”就是自己，没有信息量：醒目地标成“草稿”
            Sender = UiStrings.DraftLabel;
            Initials = "草";
        }

        SubjectHl = HighlightedText.Of(Subject, highlight?.Subject);
        SenderHl = HighlightedText.Of(Sender, IsDraft ? null : highlight?.Sender);
        PreviewHl = HighlightedText.Of(Preview, highlight?.Body);
    }

    public string Id { get; }
    public Guid AccountId { get; }
    public string FolderId { get; }
    public DateTimeOffset ReceivedAt { get; }
    public string Sender { get; private set; }
    public string FromAddress { get; }
    public string Subject { get; }
    public string Preview { get; }
    public string TimeText { get; }
    public bool HasAttachments { get; }
    public MailCategory Category { get; }
    public string Initials { get; private set; }

    /// <summary>草稿（本地或服务器同步下来的）。</summary>
    public bool IsDraft { get; }

    /// <summary>只存在于本机的草稿（id 以 "draft:" 开头），列表里加“本地草稿”标记。</summary>
    public bool IsLocalDraft { get; }

    public HighlightedText SubjectHl { get; }
    public HighlightedText SenderHl { get; }

    [ObservableProperty]
    private HighlightedText _previewHl;

    /// <summary>预览里没有命中、但正文命中时：改用围绕命中处的正文片段作为预览。</summary>
    public void UseSnippet(string snippet, IEnumerable<string> terms) => PreviewHl = HighlightedText.Of(snippet, terms);

    [ObservableProperty]
    private bool _isUnread;

    [ObservableProperty]
    private bool _isFlagged;

    /// <summary>头像缩写：中文取第一个字，拉丁名取前两个单词首字母。</summary>
    public static string MakeInitials(string name)
    {
        var trimmed = name.Trim();
        if (trimmed.Length == 0)
        {
            return "?";
        }

        var words = trimmed.Split([' ', '.', '_', '-'], StringSplitOptions.RemoveEmptyEntries);
        if (words.Length >= 2 && words.All(w => char.IsLetter(w[0]) && w[0] < 0x2E80))
        {
            return string.Concat(words.Take(2).Select(w => char.ToUpperInvariant(w[0])));
        }

        return char.ToUpperInvariant(trimmed[0]).ToString();
    }
}
