using MailClient.Auth;
using MailClient.Core;
using MailClient.Core.Sending;
using MailClient.Providers.Graph;
using MailClient.Providers.Imap;

namespace MailClient.Providers.Setup;

/// <summary>底层通道类型。两种协议的能力差异很大，用它来表达“是哪一种”，而不是靠两个可空属性互相排除。</summary>
public enum ProviderKind
{
    /// <summary>IMAP 收 + SMTP 发（Gmail 也走这条通道）。</summary>
    Imap,

    /// <summary>Microsoft Graph HTTP API。</summary>
    Graph
}

/// <summary>
/// 一个账号已构造好的底层提供商，以及需要一并释放的令牌获取器。
/// 只能通过 <see cref="ForImap"/> / <see cref="ForGraph"/> 构造，因此不存在“两个都为空”或“两个都有”的非法状态。
/// </summary>
public sealed class ProviderHandle : IAsyncDisposable
{
    private readonly GenericImapProvider? _imap;
    private readonly MicrosoftGraphProvider? _graph;
    private readonly IDisposable? _owned;

    private ProviderHandle(ProviderKind kind, GenericImapProvider? imap, MicrosoftGraphProvider? graph, IDisposable? owned)
    {
        Kind = kind;
        _imap = imap;
        _graph = graph;
        _owned = owned;
    }

    public static ProviderHandle ForImap(GenericImapProvider imap, IDisposable? owned = null)
    {
        ArgumentNullException.ThrowIfNull(imap);
        return new ProviderHandle(ProviderKind.Imap, imap, null, owned);
    }

    public static ProviderHandle ForGraph(MicrosoftGraphProvider graph, IDisposable? owned = null)
    {
        ArgumentNullException.ThrowIfNull(graph);
        return new ProviderHandle(ProviderKind.Graph, null, graph, owned);
    }

    public ProviderKind Kind { get; }

    /// <summary>IMAP 通道支持 IDLE 推送；Graph 桌面端收不到 webhook，只能轮询。</summary>
    public bool SupportsIdle => Kind == ProviderKind.Imap;

    /// <summary>IMAP 提供商。账号不是 IMAP 通道时立即抛异常，而不是把空值散给调用方。</summary>
    public GenericImapProvider Imap => _imap
        ?? throw new InvalidOperationException($"该账号是 {Kind} 通道，没有 IMAP 提供商。");

    /// <summary>Graph 提供商。账号不是 Graph 通道时立即抛异常。</summary>
    public MicrosoftGraphProvider Graph => _graph
        ?? throw new InvalidOperationException($"该账号是 {Kind} 通道，没有 Graph 提供商。");

    public async ValueTask DisposeAsync()
    {
        if (_imap is not null)
        {
            await _imap.DisposeAsync().ConfigureAwait(false);
        }

        if (_graph is not null)
        {
            await _graph.DisposeAsync().ConfigureAwait(false);
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
            case ProviderIds.MicrosoftGraph:
            {
                var clientId = _settings.MicrosoftClientId;
                if (string.IsNullOrWhiteSpace(clientId))
                {
                    throw new ClassifiedFailureException(FailureKind.Configuration, "尚未配置 Microsoft ClientId（见 docs\\microsoft-graph.md）。");
                }

                var config = new MicrosoftGraphAccountConfig(
                    account.Id, account.EmailAddress, account.DisplayName, clientId, _settings.MicrosoftTenantId);
                var acquirer = new MicrosoftGraphTokenAcquirer(config, _store, windowHandleProvider: _windowHandle);
                return ProviderHandle.ForGraph(new MicrosoftGraphProvider(config, acquirer));
            }
            case ProviderIds.Gmail:
            {
                var clientId = _settings.GmailClientId;
                if (string.IsNullOrWhiteSpace(clientId))
                {
                    throw new ClassifiedFailureException(FailureKind.Configuration, "尚未配置 Google OAuth ClientId（见 docs\\gmail-oauth.md）。");
                }

                var config = new GmailAccountConfig(account.Id, account.EmailAddress, account.DisplayName, clientId, _settings.GmailClientSecret);
                var acquirer = new GmailTokenAcquirer(config, _store);
                var gmail = new GmailProvider(config, acquirer);
                return ProviderHandle.ForImap(gmail.Imap, acquirer);
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
                return ProviderHandle.ForImap(new GenericImapProvider(config, _store));
            }
        }
    }
}
