namespace MailClient.Core;

/// <summary>与 MailKit SecureSocketOptions 对齐的跨层枚举（避免 Core 依赖 MailKit）。</summary>
public enum MailSecureSocketOptions
{
    None = 0,
    Auto = 1,
    SslOnConnect = 2,
    StartTls = 3,
    StartTlsWhenAvailable = 4
}

public sealed record ImapServerSettings(
    string Host,
    int Port,
    MailSecureSocketOptions SocketOptions);

public sealed record SmtpServerSettings(
    string Host,
    int Port,
    MailSecureSocketOptions SocketOptions);

/// <summary>明文用户名/密码或授权码；通常由 ICredentialStore 取出后注入。</summary>
public sealed record ImapAccountSecrets(
    string UserName,
    string Password);

/// <summary>通用 IMAP/SMTP 账号运行时配置。</summary>
public sealed record GenericImapAccountConfig(
    Guid AccountId,
    string EmailAddress,
    string DisplayName,
    ImapServerSettings Imap,
    SmtpServerSettings Smtp,
    string? CredentialVaultKey,
    int SyncBatchSize = 50,
    int PreviewMaxChars = 200);

/// <summary>同步落库用的完整邮件项（比列表摘要多 UID/附件等字段）。</summary>
public sealed record MailMessageSyncItem(
    MailMessageSummary Summary,
    long RemoteUid,
    string? RfcMessageId,
    string FromAddress,
    string? FromName,
    bool HasAttachments,
    long SizeBytes,
    bool IsDraft,
    DateTimeOffset? SentAt,
    MailCategory? Category = null,
    string? GmMsgId = null,
    string? GmThrId = null,
    string? BodyText = null,
    string? BodyHtml = null,
    bool BodyFetched = false,
    IReadOnlyList<MessageAttachmentInfo>? Attachments = null);
