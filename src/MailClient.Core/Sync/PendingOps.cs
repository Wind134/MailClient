namespace MailClient.Core.Sync;

/// <summary>需要写回服务器的本地变更种类。</summary>
public enum PendingOpKind
{
    MarkRead,
    MarkUnread,
    Flag,
    Unflag,
    /// <summary>移动到另一个文件夹（归档 / 移入已删除 / 其他）。</summary>
    Move,
    /// <summary>永久删除（已在“已删除”里再删一次）。</summary>
    Delete
}

/// <summary>变更所属的“字段”：同一封邮件同一字段上只保留最新一条（最后写入者胜出）。</summary>
public enum PendingField
{
    Read,
    Flag,
    Location
}

public static class PendingOpKinds
{
    public static PendingField FieldOf(PendingOpKind kind) => kind switch
    {
        PendingOpKind.MarkRead or PendingOpKind.MarkUnread => PendingField.Read,
        PendingOpKind.Flag or PendingOpKind.Unflag => PendingField.Flag,
        _ => PendingField.Location
    };
}

/// <summary>本地先改、稍后写回服务器的一条变更。</summary>
/// <param name="SourceFolderId">操作开始时邮件所在的本地文件夹（IMAP 需要用它定位 UID）。</param>
/// <param name="TargetFolderId">Move 的目标本地文件夹。</param>
/// <param name="RemoteKey">服务器侧标识：IMAP = UID，Graph = 消息 Id；无法确定时为 null。</param>
public sealed record PendingOpEntity(
    long Id,
    Guid AccountId,
    string MessageId,
    PendingOpKind Kind,
    string? SourceFolderId,
    string? TargetFolderId,
    string? RemoteKey,
    int Attempts,
    string? LastError,
    DateTimeOffset? NextAttemptAt,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt)
{
    public PendingField Field => PendingOpKinds.FieldOf(Kind);
}

/// <summary>某封邮件上有哪些字段存在未写回的本地变更（同步合并用）。</summary>
[Flags]
public enum PendingFieldMask
{
    None = 0,
    Read = 1,
    Flag = 2,
    Location = 4,
    /// <summary>本地已请求永久删除：同步不应把它重新拉回来。</summary>
    Deleted = 8
}
