namespace MailClient.ViewModels;

/// <summary>
/// ViewModel 侧用到的界面文字集中放这里（中文）。后续接入 .resw / 本地化时只需替换此类的取值来源。
/// XAML 中的静态文字目前直接写在页面里，同样可整体迁移到 x:Uid。
/// </summary>
public static class UiStrings
{
    public const string Primary = "主要";
    public const string Transactions = "交易";
    public const string Updates = "更新";
    public const string Promotions = "促销";

    public const string AllInboxes = "所有收件箱";
    public const string Flagged = "已标记";
    public const string Unread = "未读";
    public const string Favorites = "收藏夹";

    public const string FolderInbox = "收件箱";
    public const string FolderDrafts = "草稿";
    public const string FolderSent = "已发送";
    public const string FolderArchive = "归档";
    public const string FolderJunk = "垃圾邮件";
    public const string FolderTrash = "已删除邮件";
    public const string FolderOutbox = "发件箱";

    public const string NoSubject = "（无主题）";
    public const string Me = "我";
    public const string Yesterday = "昨天";
    public const string Morning = "上午";
    public const string Afternoon = "下午";

    public const string SentQueued = "邮件已加入发件箱，将在后台发送。";
    public const string Deleted = "邮件已删除";
    public const string Archived = "邮件已归档";
    public const string OutboxEmpty = "发件箱是空的";
    public const string MailSent = "邮件已发送";
    public const string MailSendFailed = "邮件发送失败";

    public const string DraftLabel = "草稿";
    public const string LocalDraftBadge = "本地草稿";
    public const string DraftDeleted = "草稿已删除";
    public const string DraftGone = "这份草稿已经不存在了。";
    public const string NoResults = "没有找到邮件";
    public const string EmptyFolder = "这里没有邮件";
    public const string ScopeFolder = "当前文件夹";
    public const string ScopeAccount = "当前账号";
    public const string ScopeAll = "全部邮箱";

    public static string SearchSummary(int count, string scope, bool truncated)
        => truncated ? $"仅显示前 {count} 封 · {scope}" : $"找到 {count} 封 · {scope}";

    public static string CategoryName(Core.MailCategory category) => category switch
    {
        Core.MailCategory.Primary => Primary,
        Core.MailCategory.Transactions => Transactions,
        Core.MailCategory.Updates => Updates,
        Core.MailCategory.Promotions => Promotions,
        _ => Primary
    };

    public static string UnreadCount(int count) => count > 0 ? $"{count} 封未读" : "全部已读";
}
