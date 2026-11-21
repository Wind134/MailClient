namespace MailClient.Core;

/// <summary>邮件列表过滤条件（列表 / 搜索 / 分类页共用）。</summary>
public sealed record MessageListFilter(
    IReadOnlyList<string>? FolderIds = null,
    MailCategory? Category = null,
    bool UnreadOnly = false,
    bool FlaggedOnly = false,
    string? SearchText = null,
    int Limit = 200,
    Guid? AccountId = null,
    bool ExcludeTrashAndJunk = false);

/// <summary>Gmail 收件箱各分类的未读数（NULL 分类已计入 Primary）。</summary>
public sealed record CategoryUnreadCounts(int All, int Primary, int Transactions, int Updates, int Promotions)
{
    public static CategoryUnreadCounts Empty { get; } = new(0, 0, 0, 0, 0);

    public int For(MailCategory? category) => category switch
    {
        null => All,
        MailCategory.Primary => Primary,
        MailCategory.Transactions => Transactions,
        MailCategory.Updates => Updates,
        MailCategory.Promotions => Promotions,
        _ => 0
    };
}
