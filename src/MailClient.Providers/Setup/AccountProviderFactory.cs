using MailClient.Auth;
using MailClient.Core;
using MailClient.Core.Sending;
using MailClient.Providers.Graph;
using MailClient.Providers.Imap;

namespace MailClient.Providers.Setup;

/// <summary>一个账号的已构造好的底层提供商（IMAP 或 Graph）及其需要一并释放的令牌获取器。</summary>
public sealed class ProviderHandle : IAsyncDisposable
{
    private readonly IDisposable? _owned;

    public ProviderHandle(GenericImapProvider? imap, MicrosoftGraphProvider? graph, IDisposable? owned = null)
    {
        Imap = imap;
        Graph = graph;
        _owned = owned;
    }

    public GenericImapProvider? Imap { get; }

    public MicrosoftGraphProvider? Graph { get; }

    public async ValueTask DisposeAsync()
    {
        if (Imap is not null)
        {
            await Imap.DisposeAsync().ConfigureAwait(false);
        }

        if (Graph is not null)
        {
            await Graph.DisposeAsync().ConfigureAwait(false);
        }

        _owned?.Dispose();
    }
}

/// <summary>按账号设置（<see cref="AccountConnectionStore"/>、凭据库、应用设置）构造真实的提供商，供发件箱与写回 worker 使用。</summary>
public sealed class AccountProviderFactory
{
    private readonly ICredentialStore _store;
    private readonly AppSettingsStore _settings;
    private readonly AccountConnectionStore _connections;
    private readonly Func<IntPtr>? _windowHandle;

    public AccountProviderFactory(ICredentialStore store, AppSettingsStore settings, Func<IntPtr>? windowHandleProvider = null)
    {
        _store = store;
        _settings = settings;
        _connections = new AccountConnectionStore(store);
        _windowHandle = windowHandleProvider;
    }

    public AppSettingsStore Settings => _settings;

    public async Task<ProviderHandle> OpenAsync(AccountEntity account, CancellationToken cancellationToken)
    {
        switch (account.ProviderId)
        {
            case "microsoft.graph":
            {
                var clientId = _settings.MicrosoftClientId;
                if (string.IsNullOrWhiteSpace(clientId))
                {
                    throw new ClassifiedFailureException(FailureKind.Configuration, "尚未配置 Microsoft ClientId（见 docs\\microsoft-graph.md）。");
                }

                var config = new MicrosoftGraphAccountConfig(
                    account.Id, account.EmailAddress, account.DisplayName, clientId, _settings.MicrosoftTenantId);
                var acquirer = new MicrosoftGraphTokenAcquirer(config, _store, windowHandleProvider: _windowHandle);
                return new ProviderHandle(null, new MicrosoftGraphProvider(config, acquirer));
            }
            case Outbox.SentCopyPolicy.GmailProviderId:
            {
                var clientId = _settings.GmailClientId;
                if (string.IsNullOrWhiteSpace(clientId))
                {
                    throw new ClassifiedFailureException(FailureKind.Configuration, "尚未配置 Google OAuth ClientId（见 docs\\gmail-oauth.md）。");
                }

                var config = new GmailAccountConfig(account.Id, account.EmailAddress, account.DisplayName, clientId, _settings.GmailClientSecret);
                var acquirer = new GmailTokenAcquirer(config, _store);
                var gmail = new GmailProvider(config, acquirer);
                return new ProviderHandle(gmail.Imap, null, acquirer);
            }
            default:
            {
                var connection = await _connections.LoadAsync(account.Id, cancellationToken).ConfigureAwait(false)
                    ?? throw new ClassifiedFailureException(FailureKind.Configuration, "找不到该账号的服务器设置，请移除后重新添加。");
                if (connection.Imap is null || connection.Smtp is null)
                {
                    throw new ClassifiedFailureException(FailureKind.Configuration, "该账号缺少 IMAP/SMTP 服务器设置，请移除后重新添加。");
                }

                var config = new GenericImapAccountConfig(
                    account.Id, account.EmailAddress, account.DisplayName, connection.Imap, connection.Smtp,
                    AccountConnectionStore.ImapSecretKey(account.Id));
                return new ProviderHandle(new GenericImapProvider(config, _store), null);
            }
        }
    }
}
