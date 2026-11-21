using System.Net.Http.Headers;
using System.Text.Json;
using MailClient.Auth;
using MailClient.Core;
using MailClient.Core.Auth;
using MailClient.Providers.Sync;
using MailClient.Storage;

namespace MailClient.Providers.Setup;

/// <summary>重新登录的结果。</summary>
public sealed record ReauthResult(bool Success, string? Error = null, bool Cancelled = false, bool AccountMismatch = false)
{
    public static ReauthResult Ok { get; } = new(true);
}

/// <summary>
/// 账号连接检查与重新登录（不删账号、不动本地邮件）。ViewModel 只依赖这个接口，便于单测。
/// </summary>
public interface IAccountAuthService
{
    /// <summary>
    /// 轻量连接检查（不弹浏览器）：Gmail / IMAP 连接并登录 IMAP；Outlook 静默取令牌并请求一次 Graph /me。
    /// </summary>
    Task<ConnectionCheckResult> CheckConnectionAsync(Guid accountId, CancellationToken cancellationToken);

    /// <summary>该账号重新登录时是否需要输入密码 / 授权码（IMAP 类账号）。</summary>
    Task<bool> RequiresPasswordAsync(Guid accountId, CancellationToken cancellationToken);

    /// <summary>
    /// 重新登录：Gmail 走浏览器授权、Outlook 走 MSAL 交互登录、IMAP 用 <paramref name="password"/> 验证后保存。
    /// 必须是同一个邮箱；否则返回 <see cref="ReauthResult.AccountMismatch"/> 且不覆盖原有凭据。
    /// </summary>
    Task<ReauthResult> ReauthenticateAsync(Guid accountId, string? password, CancellationToken cancellationToken);
}

public sealed class AccountAuthService : IAccountAuthService
{
    public static readonly TimeSpan CheckTimeout = TimeSpan.FromSeconds(30);

    private readonly MailDatabase _db;
    private readonly ICredentialStore _store;
    private readonly AppSettingsStore _settings;
    private readonly Func<IntPtr>? _windowHandle;
    private readonly AccountConnectionStore _connections;
    private readonly AccountProviderFactory _providers;
    private readonly Lazy<HttpClient> _http = new(() => new HttpClient { Timeout = TimeSpan.FromSeconds(20) });

    public AccountAuthService(MailDatabase database, ICredentialStore store, AppSettingsStore settings, Func<IntPtr>? windowHandleProvider = null)
    {
        _db = database ?? throw new ArgumentNullException(nameof(database));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _windowHandle = windowHandleProvider;
        _connections = new AccountConnectionStore(store);
        _providers = new AccountProviderFactory(store, settings, windowHandleProvider);
    }

    public async Task<ConnectionCheckResult> CheckConnectionAsync(Guid accountId, CancellationToken cancellationToken)
    {
        var account = await _db.GetAccountAsync(accountId, cancellationToken).ConfigureAwait(false);
        if (account is null)
        {
            return new ConnectionCheckResult(ConnectionStatus.Failed, "账号不存在或已删除。");
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(CheckTimeout);
        try
        {
            if (account.ProviderId == ProviderIds.MicrosoftGraph)
            {
                var acquirer = CreateGraphAcquirer(account);
                var token = await acquirer.AcquireAsync(allowInteractive: false, timeout.Token).ConfigureAwait(false);
                using var request = new HttpRequestMessage(HttpMethod.Get, "https://graph.microsoft.com/v1.0/me?$select=id");
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.AccessToken);
                using var response = await _http.Value.SendAsync(request, timeout.Token).ConfigureAwait(false);
                if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
                {
                    return new ConnectionCheckResult(ConnectionStatus.AuthFailed, "Microsoft 登录已失效，需要重新登录。");
                }

                response.EnsureSuccessStatusCode();
            }
            else
            {
                // Gmail（XOAUTH2，必要时先用 refresh token 刷新）与普通 IMAP：连接 + 登录即说明凭据有效
                await using var handle = await _providers.OpenAsync(account, timeout.Token).ConfigureAwait(false);
                await handle.Imap.ConnectAsync(timeout.Token).ConfigureAwait(false);
            }

            return ConnectionCheckResult.Healthy;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            var status = ConnectionFailureClassifier.Classify(ex);
            return new ConnectionCheckResult(status, Describe(ex, account, status));
        }
    }

    public async Task<bool> RequiresPasswordAsync(Guid accountId, CancellationToken cancellationToken)
    {
        var account = await _db.GetAccountAsync(accountId, cancellationToken).ConfigureAwait(false);
        return account is not null && account.ProviderId is not (ProviderIds.MicrosoftGraph or ProviderIds.Gmail);
    }

    public async Task<ReauthResult> ReauthenticateAsync(Guid accountId, string? password, CancellationToken cancellationToken)
    {
        var account = await _db.GetAccountAsync(accountId, cancellationToken).ConfigureAwait(false);
        if (account is null)
        {
            return new ReauthResult(false, "账号不存在或已删除。");
        }

        try
        {
            switch (account.ProviderId)
            {
                case ProviderIds.Gmail:
                {
                    var clientId = _settings.GmailClientId;
                    if (string.IsNullOrWhiteSpace(clientId))
                    {
                        return new ReauthResult(false, "还没有填写 Google 的 ClientId，无法登录 Gmail。");
                    }

                    using var acquirer = new GmailTokenAcquirer(
                        new GmailAccountConfig(account.Id, account.EmailAddress, account.DisplayName, clientId, _settings.GmailClientSecret), _store);
                    await acquirer.ReauthenticateAsync(cancellationToken).ConfigureAwait(false);
                    return ReauthResult.Ok;
                }
                case ProviderIds.MicrosoftGraph:
                {
                    if (string.IsNullOrWhiteSpace(_settings.MicrosoftClientId))
                    {
                        return new ReauthResult(false, "还没有填写 Microsoft 的应用（客户端）ID，无法登录 Outlook。");
                    }

                    var acquirer = CreateGraphAcquirer(account);
                    await acquirer.ReauthenticateAsync((token, ct) => IsSameGraphAccountAsync(account, token, ct), cancellationToken)
                        .ConfigureAwait(false);
                    return ReauthResult.Ok;
                }
                default:
                    return await ReauthImapAsync(account, password, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (AccountMismatchException mismatch)
        {
            return new ReauthResult(false, mismatch.Message, AccountMismatch: true);
        }
        catch (OperationCanceledException)
        {
            return new ReauthResult(false, "已取消重新登录。", Cancelled: true);
        }
        catch (Exception ex)
        {
            var kind = KindOf(account);
            var error = SetupErrorClassifier.Classify(ex, kind, SetupStage.Login);
            return new ReauthResult(false, error.Message, Cancelled: error.Kind == SetupErrorKind.Cancelled);
        }
    }

    private async Task<ReauthResult> ReauthImapAsync(AccountEntity account, string? password, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(password))
        {
            return new ReauthResult(false, "请输入密码或授权码。");
        }

        var connection = await _connections.LoadAsync(account.Id, ct).ConfigureAwait(false);
        if (connection?.Imap is null || connection.Smtp is null)
        {
            return new ReauthResult(false, "找不到该账号的服务器设置，请移除后重新添加。");
        }

        // 先用新密码验证 IMAP，通过了才写入凭据库（用户名沿用原来的，所以必然是同一个邮箱）
        var config = new GenericImapAccountConfig(
            account.Id, account.EmailAddress, account.DisplayName, connection.Imap, connection.Smtp, CredentialVaultKey: null);
        await using (var provider = new GenericImapProvider(config, new ImapAccountSecrets(connection.UserName, password)))
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(AccountSetupService.StageTimeout);
            try
            {
                await provider.VerifyImapAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new TimeoutException("连接收信服务器超时。");
            }
        }

        await _connections.SaveAsync(account.Id, connection, password, ct).ConfigureAwait(false);
        return ReauthResult.Ok;
    }

    /// <summary>MSAL 返回的用户名一般就是邮箱；对不上时再问一次 Graph /me（mail / userPrincipalName 任一匹配即可）。</summary>
    private async Task<bool> IsSameGraphAccountAsync(AccountEntity account, AccessTokenResult token, CancellationToken ct)
    {
        if (string.Equals(token.AccountUsername, account.EmailAddress, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "https://graph.microsoft.com/v1.0/me?$select=mail,userPrincipalName");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.AccessToken);
            using var response = await _http.Value.SendAsync(request, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return false;
            }

            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
            foreach (var name in new[] { "mail", "userPrincipalName" })
            {
                if (doc.RootElement.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String &&
                    string.Equals(p.GetString(), account.EmailAddress, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException)
        {
        }

        return false;
    }

    private MicrosoftGraphTokenAcquirer CreateGraphAcquirer(AccountEntity account)
        => new(
            new MicrosoftGraphAccountConfig(
                account.Id, account.EmailAddress, account.DisplayName, _settings.MicrosoftClientId ?? string.Empty, _settings.MicrosoftTenantId),
            _store,
            windowHandleProvider: _windowHandle);

    private static SetupProviderKind KindOf(AccountEntity account) => account.ProviderId switch
    {
        ProviderIds.Gmail => SetupProviderKind.Gmail,
        ProviderIds.MicrosoftGraph => SetupProviderKind.Outlook,
        _ => ProviderDetector.Detect(account.EmailAddress)?.Kind ?? SetupProviderKind.OtherImap
    };

    private static string Describe(Exception ex, AccountEntity account, ConnectionStatus status)
    {
        if (status == ConnectionStatus.AuthFailed)
        {
            return account.ProviderId switch
            {
                ProviderIds.Gmail => "Gmail 登录已失效，需要重新登录。",
                ProviderIds.MicrosoftGraph => "Microsoft 登录已失效，需要重新登录。",
                _ => "服务器拒绝了保存的密码或授权码，需要重新输入。"
            };
        }

        var stage = account.ProviderId == ProviderIds.MicrosoftGraph ? SetupStage.Graph : SetupStage.Imap;
        return SetupErrorClassifier.Classify(ex, KindOf(account), stage).Message;
    }
}
