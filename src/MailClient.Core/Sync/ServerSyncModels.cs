namespace MailClient.Core.Sync;

/// <summary>从服务器拉到的一封邮件的已读 / 星标状态（IMAP FLAGS）。</summary>
public sealed record ServerFlagState(long Uid, bool IsRead, bool IsFlagged, ulong ModSeq = 0);

/// <summary>本地库里某个文件夹中一封“来自服务器”的邮件的定位信息（对账用）。</summary>
public sealed record LocalUidRow(string MessageId, long Uid, bool IsRead, bool IsFlagged, string? GmMsgId, DateTimeOffset? ReceivedAt);

public sealed record RemovedRow(string MessageId, long Uid, string? GmMsgId);

public sealed record ServerFlagApplyResult(int Updated, IReadOnlyList<string> UpdatedMessageIds);

public sealed record UidValidityResetResult(int MessagesRemoved, IReadOnlyList<PendingOpEntity> DiscardedOps);

/// <summary>文件夹同步状态（只写同步相关的列，不动未读 / 总数）。</summary>
public sealed record FolderSyncState(
    string FolderId,
    string? SyncCursor,
    long? UidValidity,
    long? UidNext,
    long? HighestModSeq,
    DateTimeOffset? LastReconcileAt);

/// <summary>同一封 Gmail 邮件（同一个 X-GM-MSGID）在不同文件夹里的副本；用来挑出要保留的那一份。</summary>
public sealed record GmailCopyCandidate(string MessageId, SpecialFolder Folder, bool HasPendingOps);

public static class GmailDedupe
{
    /// <summary>保留优先级：有未写回操作的 &gt; 已删除 &gt; 垃圾邮件 &gt; 草稿 &gt; 收件箱 &gt; 已发送 &gt; 归档 &gt; 其他。</summary>
    public static GmailCopyCandidate PickKeeper(IReadOnlyList<GmailCopyCandidate> copies)
    {
        ArgumentNullException.ThrowIfNull(copies);
        return copies
            .OrderBy(c => c.HasPendingOps ? 0 : 1)
            .ThenBy(c => FolderRank(c.Folder))
            .ThenBy(c => c.MessageId, StringComparer.Ordinal)
            .First();
    }

    private static int FolderRank(SpecialFolder folder) => folder switch
    {
        SpecialFolder.Trash => 0,
        SpecialFolder.Junk => 1,
        SpecialFolder.Drafts => 2,
        SpecialFolder.Inbox => 3,
        SpecialFolder.Sent => 4,
        SpecialFolder.Archive => 5,
        _ => 6
    };
}
