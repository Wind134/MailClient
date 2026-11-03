using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using MailClient.Core;

namespace MailClient.ViewModels;

public enum MailboxNodeKind
{
    Account,
    Folder,
    AllInboxes,
    Flagged,
    Unread,
    Outbox,
    Drafts
}

/// <summary>左侧导航的一个节点（账号 / 文件夹 / 收藏项）。</summary>
public sealed partial class MailboxNodeViewModel : ObservableObject
{
    public MailboxNodeViewModel(MailboxNodeKind kind, string title, string glyph)
    {
        Kind = kind;
        Title = title;
        Glyph = glyph;
    }

    public MailboxNodeKind Kind { get; }
    public string Title { get; }
    public string Glyph { get; }
    public Guid? AccountId { get; init; }
    public string? FolderId { get; init; }
    public string? EmailAddress { get; init; }
    /// <summary>账号 ProviderId（如 gmail.oauth / microsoft.graph）；文件夹节点从其所属账号带下来。</summary>
    public string? ProviderId { get; init; }
    public bool IsDemo { get; init; }
    public SpecialFolder? Special { get; init; }
    public ObservableCollection<MailboxNodeViewModel> Children { get; } = new();

    [ObservableProperty]
    private int _unreadCount;

    /// <summary>账号颜色（#RRGGBB）；仅账号节点使用，null 表示用默认色。</summary>
    [ObservableProperty]
    private string? _colorHex;

    /// <summary>账号头像上的字母（显示名首字符）；仅账号节点使用。</summary>
    public string AvatarLetter { get; init; } = string.Empty;

    /// <summary>是否显示右侧数字徽标（「未读」与「所有收件箱」数字相同时隐藏，避免窄栏里两个图标+同一个数字像重复）。</summary>
    [ObservableProperty]
    private bool _showBadge = true;

    /// <summary>
    /// 是否展示 Gmail 分类标签（主要/交易/更新/促销）。
    /// 仅单个 Gmail 收件箱；「所有收件箱」与 Outlook/QQ/163/IMAP 不显示，列表也不按 Category 过滤。
    /// </summary>
    public bool ShowsCategories =>
        Kind == MailboxNodeKind.Folder
        && Special == SpecialFolder.Inbox
        && IsGmailProvider(ProviderId);

    public static bool IsGmailProvider(string? providerId) =>
        string.Equals(providerId, GmailEndpoints.ProviderId, StringComparison.OrdinalIgnoreCase);

    public bool IsSelectable => Kind != MailboxNodeKind.Account;

    public static string GlyphFor(SpecialFolder special) => special switch
    {
        SpecialFolder.Inbox => "\uE715",
        SpecialFolder.Drafts => "\uE70F",
        SpecialFolder.Sent => "\uE725",
        SpecialFolder.Archive => "\uE8B7",
        SpecialFolder.Junk => "\uE730",
        SpecialFolder.Trash => "\uE74D",
        SpecialFolder.Flagged => "\uE7C1",
        SpecialFolder.Outbox => "\uE724",
        _ => "\uE8B7"
    };

    public static string TitleFor(SpecialFolder special, string fallback) => special switch
    {
        SpecialFolder.Inbox => UiStrings.FolderInbox,
        SpecialFolder.Drafts => UiStrings.FolderDrafts,
        SpecialFolder.Sent => UiStrings.FolderSent,
        SpecialFolder.Archive => UiStrings.FolderArchive,
        SpecialFolder.Junk => UiStrings.FolderJunk,
        SpecialFolder.Trash => UiStrings.FolderTrash,
        SpecialFolder.Outbox => UiStrings.FolderOutbox,
        _ => fallback
    };

    public static int OrderFor(SpecialFolder special) => special switch
    {
        SpecialFolder.Inbox => 0,
        SpecialFolder.Drafts => 1,
        SpecialFolder.Sent => 2,
        SpecialFolder.Archive => 3,
        SpecialFolder.Junk => 4,
        SpecialFolder.Trash => 5,
        _ => 10
    };
}
