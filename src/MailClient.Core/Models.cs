namespace MailClient.Core;

public enum SpecialFolder
{
    Inbox,
    Sent,
    Drafts,
    Trash,
    Flagged,
    Outbox,
    Archive,
    Junk,
    Custom
}

public enum AuthKind
{
    OAuth,
    AppPassword,
    Password
}

public enum RecipientKind
{
    To,
    Cc,
    Bcc
}

public enum OutboxStatus
{
    Pending,
    Sending,
    Sent,
    Failed
}

/// <summary>UI 分类壳：MVP 仅存盘，规则引擎二期。</summary>
public enum MailCategory
{
    Primary,
    Transactions,
    Updates,
    Promotions
}

public sealed record MailAccount(
    Guid Id,
    string DisplayName,
    string EmailAddress,
    string ProviderId);

/// <summary>本地持久化用的完整账号实体。</summary>
public sealed record AccountEntity(
    Guid Id,
    string DisplayName,
    string EmailAddress,
    string ProviderId,
    AuthKind AuthKind,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? LastSyncAt,
    bool IsEnabled);

public sealed record MailFolder(
    string Id,
    string Name,
    SpecialFolder Special,
    string? RemotePath,
    int UnreadCount);

public sealed record FolderEntity(
    string Id,
    Guid AccountId,
    string Name,
    SpecialFolder Special,
    string? RemotePath,
    int UnreadCount,
    int TotalCount,
    int SortOrder,
    string? SyncCursor = null,
    long? UidValidity = null,
    long? UidNext = null,
    long? HighestModSeq = null,
    DateTimeOffset? LastReconcileAt = null);

public sealed record MailMessageSummary(
    string Id,
    string FolderId,
    string Subject,
    string From,
    DateTimeOffset ReceivedAt,
    bool IsRead,
    bool IsFlagged,
    string Preview);

public sealed record MessageEntity(
    string Id,
    Guid AccountId,
    string FolderId,
    long? RemoteUid,
    string? RfcMessageId,
    string? ThreadKey,
    string Subject,
    string FromAddress,
    string? FromName,
    string Preview,
    string? BodyText,
    string? BodyHtml,
    DateTimeOffset? ReceivedAt,
    DateTimeOffset? SentAt,
    bool IsRead,
    bool IsFlagged,
    bool IsDraft,
    bool HasAttachments,
    MailCategory? Category,
    long SizeBytes,
    /// <summary>是否已向服务器取过完整正文并写入 BodyText/BodyHtml（空正文也算已取）。</summary>
    bool BodyFetched = false);

public sealed record RecipientEntity(
    long Id,
    string MessageId,
    RecipientKind Kind,
    string Address,
    string? Name);

public sealed record AttachmentEntity(
    string Id,
    string MessageId,
    string FileName,
    string? ContentType,
    long SizeBytes,
    string? ContentId,
    bool IsInline,
    string? LocalPath,
    /// <summary>Graph 附件 Id；IMAP 可空（下载时按文件名在 MIME 里匹配）。</summary>
    string? RemoteId = null);

public sealed record OutboxEntity(
    string Id,
    Guid AccountId,
    string PayloadJson,
    OutboxStatus Status,
    int Attempts,
    string? LastError,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? NextAttemptAt = null,
    string? RfcMessageId = null,
    bool IsPermanent = false,
    bool MaybeSent = false,
    bool SentCopyPending = false,
    int CopyAttempts = 0);

public sealed record CredentialsMetaEntity(
    Guid AccountId,
    string VaultKey);

public sealed record OutgoingMailMessage(
    string? AccountId,
    IReadOnlyList<string> To,
    IReadOnlyList<string> Cc,
    IReadOnlyList<string> Bcc,
    string Subject,
    string BodyHtml,
    IReadOnlyList<string> AttachmentPaths,
    string? BodyText = null,
    string? MessageId = null,
    string? InReplyTo = null,
    IReadOnlyList<string>? References = null,
    string? DraftId = null);
