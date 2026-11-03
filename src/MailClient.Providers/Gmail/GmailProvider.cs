using MailClient.Auth;
using MailClient.Core;
using MailClient.Core.Providers;
using MailClient.Providers.Imap;

namespace MailClient.Providers;

/// <summary>
/// Gmail 提供商（<c>gmail.oauth</c>）：imap.gmail.com:993 / smtp.gmail.com:465，使用 Google OAuth 2.0 令牌做 XOAUTH2。
/// 内部复用 <see cref="GenericImapProvider"/>；文件夹同步请把 <see cref="Imap"/> 交给 <see cref="ImapSyncPipeline"/>。
/// Gmail 的特殊文件夹通过 SPECIAL-USE 属性映射（见 <see cref="FolderSpecialMapper"/>）：
/// [Gmail]/All Mail(\All)→归档，Spam(\Junk)→垃圾邮件，Sent Mail/Drafts/Trash，Starred(\Flagged)→已标记；Important 保持自定义文件夹。
/// </summary>
public sealed class GmailProvider : IMailProvider, IAsyncDisposable
{
    private readonly GenericImapProvider _imap;

    public GmailProvider(GmailAccountConfig config, IOAuthTokenSource tokenSource)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(tokenSource);
        Config = config;
        _imap = new GenericImapProvider(
            ToImapConfig(config),
            tokenSource,
            GmailEndpoints.ProviderId,
            config.UseGmailCategories);
    }

    /// <summary>便捷构造：用 <see cref="GmailTokenAcquirer"/>（凭据库保存 refresh token）。</summary>
    public GmailProvider(GmailAccountConfig config, GmailTokenAcquirer acquirer)
        : this(config, (IOAuthTokenSource)acquirer)
    {
    }

    public GmailAccountConfig Config { get; }

    public string ProviderId => GmailEndpoints.ProviderId;

    public Guid AccountId => Config.AccountId;

    /// <summary>底层 IMAP 提供商（给 <see cref="ImapSyncPipeline"/> 用）。</summary>
    public GenericImapProvider Imap => _imap;

    public static GenericImapAccountConfig ToImapConfig(GmailAccountConfig config)
        => new(
            AccountId: config.AccountId,
            EmailAddress: config.EmailAddress,
            DisplayName: config.DisplayName,
            Imap: new ImapServerSettings(GmailEndpoints.ImapHost, GmailEndpoints.ImapPort, MailSecureSocketOptions.SslOnConnect),
            Smtp: new SmtpServerSettings(GmailEndpoints.SmtpHost, GmailEndpoints.SmtpPort, MailSecureSocketOptions.SslOnConnect),
            CredentialVaultKey: config.EffectiveVaultKey,
            SyncBatchSize: config.SyncBatchSize);

    public Task ConnectAsync(CancellationToken cancellationToken = default) => _imap.ConnectAsync(cancellationToken);

    public Task DisconnectAsync(CancellationToken cancellationToken = default) => _imap.DisconnectAsync(cancellationToken);

    public Task<IReadOnlyList<MailFolder>> GetFoldersAsync(CancellationToken cancellationToken = default)
        => _imap.GetFoldersAsync(cancellationToken);

    public Task<IReadOnlyList<MailMessageSummary>> SyncFolderAsync(string folderId, CancellationToken cancellationToken = default)
        => _imap.SyncFolderAsync(folderId, cancellationToken);

    public Task SendAsync(OutgoingMailMessage message, CancellationToken cancellationToken = default)
        => _imap.SendAsync(message, cancellationToken);

    public ValueTask DisposeAsync() => _imap.DisposeAsync();
}
