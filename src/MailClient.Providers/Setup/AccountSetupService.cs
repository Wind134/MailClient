using System.Net.Http.Headers;
using System.Text.Json;
using MailClient.Auth;
using MailClient.Core;
using MailClient.Storage;

namespace MailClient.Providers.Setup;

/// <summary>添加 / 移除账号的副作用（网络、凭据库、数据库）。ViewModel 只依赖这个接口，便于单测。</summary>
public interface IAccountSetupService
{
    /// <summary>Gmail / Outlook：在系统浏览器（或 WAM）里登录，返回登录成功的邮箱地址。</summary>
    Task<SetupResult> SignInAsync(AccountDraft draft, CancellationToken cancellationToken);

    /// <summary>验证：IMAP + SMTP（或 Graph /me）。逐步通过 <paramref name="progress"/> 报告。</summary>
    Task<SetupResult> VerifyAsync(AccountDraft draft, IProgress<SetupProgress>? progress, CancellationToken cancellationToken);

    /// <summary>写入数据库与凭据库。</summary>
    Task<AccountEntity> SaveAsync(AccountDraft draft, CancellationToken cancellationToken);

    /// <summary>放弃未保存的草稿：清掉可能已写入的令牌。</summary>
    Task DiscardAsync(AccountDraft draft);

    /// <summary>删除账号：数据库（级联）+ 凭据库 + 尽力注销。</summary>
    Task RemoveAccountAsync(Guid accountId, CancellationToken cancellationToken);

    /// <summary>ISPDB 自动配置查询（best-effort；失败返回 null）。</summary>
    Task<AutoconfigResult?> LookupAutoconfigAsync(string email, CancellationToken cancellationToken);
}

public sealed class AccountSetupService : IAccountSetupService
{
    public static readonly TimeSpan StageTimeout = TimeSpan.FromSeconds(45);

    private const string GmailDocHint = "尚未配置 Google OAuth ClientId。请按 docs\\gmail-oauth.md 在 Google Cloud Console 创建「桌面应用」OAuth 客户端，然后把 ClientId 填到下面的输入框。";
    private const string GraphDocHint = "尚未配置 Microsoft ClientId。请展开添加账号页的「怎么设置」，在 Microsoft Entra 注册应用后把「应用程序(客户端) ID」填到下面的输入框。";

    private readonly MailDatabase _db;
    private readonly ICredentialStore _store;
    private readonly AppSettingsStore _settings;
    private readonly Func<IntPtr>? _windowHandle;
    private readonly Func<DateTimeOffset> _clock;
    private readonly AccountConnectionStore _connections;
    private readonly Lazy<HttpClient> _http = new(() => new HttpClient { Timeout = TimeSpan.FromSeconds(20) });

    public AccountSetupService(
        MailDatabase database,
        ICredentialStore store,
        AppSettingsStore settings,
        Func<IntPtr>? windowHandleProvider = null,
        Func<DateTimeOffset>? clock = null)
    {
        _db = database ?? throw new ArgumentNullException(nameof(database));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _windowHandle = windowHandleProvider;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
        _connections = new AccountConnectionStore(store);
    }

    public async Task<SetupResult> SignInAsync(AccountDraft draft, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(draft);
        try
        {
            switch (draft.Kind)
            {
                case SetupProviderKind.Gmail:
                {
                    var clientId = PersistGmail(draft);
                    if (string.IsNullOrWhiteSpace(clientId))
                    {
                        return SetupResult.Fail(new SetupError(SetupErrorKind.MissingClientId, GmailDocHint), SetupStage.Login);
                    }

                    var config = GmailConfig(draft, clientId);
                    using var acquirer = new GmailTokenAcquirer(config, _store);
                    var token = await acquirer.AcquireAsync(allowInteractive: true, cancellationToken).ConfigureAwait(false);
                    var email = token.AccountUsername ?? draft.EmailAddress;
                    return SetupResult.Ok(email, null);
                }
                case SetupProviderKind.Outlook:
                {
                    var clientId = PersistMicrosoft(draft, out var tenant);
                    if (string.IsNullOrWhiteSpace(clientId))
                    {
                        return SetupResult.Fail(new SetupError(SetupErrorKind.MissingClientId, GraphDocHint), SetupStage.Login);
                    }

                    var acquirer = new MicrosoftGraphTokenAcquirer(
                        GraphConfig(draft, clientId, tenant), _store, windowHandleProvider: _windowHandle);
                    var token = await acquirer.AcquireAsync(allowInteractive: true, cancellationToken).ConfigureAwait(false);
                    return SetupResult.Ok(token.AccountUsername ?? draft.EmailAddress, null);
                }
                default:
                    return SetupResult.Ok(draft.EmailAddress);
            }
        }
        catch (Exception ex)
        {
            return SetupResult.Fail(SetupErrorClassifier.Classify(ex, draft.Kind, SetupStage.Login), SetupStage.Login);
        }
    }

    public async Task<SetupResult> VerifyAsync(AccountDraft draft, IProgress<SetupProgress>? progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(draft);
        if (draft.Kind == SetupProviderKind.Outlook)
        {
            return await VerifyGraphAsync(draft, progress, cancellationToken).ConfigureAwait(false);
        }

        // Gmail（XOAUTH2）与普通 IMAP 共用同一套 IMAP + SMTP 验证
        GenericImapProvider? provider = null;
        try
        {
            ImapServerSettings imap;
            SmtpServerSettings smtp;
            if (draft.Kind == SetupProviderKind.Gmail)
            {
                var clientId = PersistGmail(draft);
                if (string.IsNullOrWhiteSpace(clientId))
                {
                    return SetupResult.Fail(new SetupError(SetupErrorKind.MissingClientId, GmailDocHint), SetupStage.Login);
                }

                var config = GmailConfig(draft, clientId);
                var acquirer = new GmailTokenAcquirer(config, _store);
                var gmail = new GmailProvider(config, acquirer);
                provider = gmail.Imap;
                imap = new ImapServerSettings(GmailEndpoints.ImapHost, GmailEndpoints.ImapPort, MailSecureSocketOptions.SslOnConnect);
                smtp = new SmtpServerSettings(GmailEndpoints.SmtpHost, GmailEndpoints.SmtpPort, MailSecureSocketOptions.SslOnConnect);
            }
            else
            {
                if (draft.Imap is null || draft.Smtp is null)
                {
                    return SetupResult.Fail(new SetupError(SetupErrorKind.Unknown, "缺少服务器设置。"), SetupStage.Imap);
                }

                imap = draft.Imap;
                smtp = draft.Smtp;
                var config = new GenericImapAccountConfig(
                    draft.Id, draft.EmailAddress, draft.DisplayName, imap, smtp, CredentialVaultKey: null);
                provider = new GenericImapProvider(
                    config, new ImapAccountSecrets(draft.EffectiveUserName, draft.Password ?? string.Empty));
            }

            var failed = await RunStageAsync(SetupStage.Imap, imap.Host, draft.Kind, progress, cancellationToken,
                ct => provider.VerifyImapAsync(ct)).ConfigureAwait(false);
            if (failed is not null)
            {
                return failed;
            }

            failed = await RunStageAsync(SetupStage.Smtp, smtp.Host, draft.Kind, progress, cancellationToken,
                ct => provider.VerifySmtpAsync(ct)).ConfigureAwait(false);
            return failed ?? SetupResult.Ok(draft.EmailAddress, draft.DisplayName);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return SetupResult.Fail(SetupErrorClassifier.Classify(ex, draft.Kind, SetupStage.Imap), SetupStage.Imap);
        }
        finally
        {
            if (provider is not null)
            {
                await provider.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private async Task<SetupResult> VerifyGraphAsync(AccountDraft draft, IProgress<SetupProgress>? progress, CancellationToken cancellationToken)
    {
        var clientId = PersistMicrosoft(draft, out var tenant);
        if (string.IsNullOrWhiteSpace(clientId))
        {
            return SetupResult.Fail(new SetupError(SetupErrorKind.MissingClientId, GraphDocHint), SetupStage.Login);
        }

        string? email = null;
        var failed = await RunStageAsync(SetupStage.Graph, "graph.microsoft.com", draft.Kind, progress, cancellationToken, async ct =>
        {
            var acquirer = new MicrosoftGraphTokenAcquirer(
                GraphConfig(draft, clientId, tenant), _store, windowHandleProvider: _windowHandle);
            var token = await acquirer.AcquireAsync(allowInteractive: false, ct).ConfigureAwait(false);
            using var request = new HttpRequestMessage(
                HttpMethod.Get, "https://graph.microsoft.com/v1.0/me?$select=displayName,mail,userPrincipalName");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.AccessToken);
            using var response = await _http.Value.SendAsync(request, ct).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
            var root = doc.RootElement;
            email = Prop(root, "mail") ?? Prop(root, "userPrincipalName") ?? token.AccountUsername ?? draft.EmailAddress;
        }).ConfigureAwait(false);

        return failed ?? SetupResult.Ok(email ?? draft.EmailAddress, draft.DisplayName);
    }

    private static string? Prop(JsonElement root, string name)
        => root.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(p.GetString())
            ? p.GetString()
            : null;

    /// <summary>执行一个验证阶段：报告进度、带超时；失败返回已分类的结果，成功返回 null。</summary>
    private static async Task<SetupResult?> RunStageAsync(
        SetupStage stage,
        string host,
        SetupProviderKind kind,
        IProgress<SetupProgress>? progress,
        CancellationToken cancellationToken,
        Func<CancellationToken, Task> action)
    {
        progress?.Report(new SetupProgress(stage, SetupStageStatus.Running));
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(StageTimeout);
        try
        {
            await action(timeout.Token).ConfigureAwait(false);
            progress?.Report(new SetupProgress(stage, SetupStageStatus.Succeeded));
            return null;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            var error = SetupErrorClassifier.Classify(new TimeoutException(), kind, stage, host);
            progress?.Report(new SetupProgress(stage, SetupStageStatus.Failed, error.Message));
            return SetupResult.Fail(error, stage);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            var error = SetupErrorClassifier.Classify(ex, kind, stage, host);
            progress?.Report(new SetupProgress(stage, SetupStageStatus.Failed, error.Message));
            return SetupResult.Fail(error, stage);
        }
    }

    public async Task<AccountEntity> SaveAsync(AccountDraft draft, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(draft);
        var now = _clock();
        var (providerId, authKind) = draft.Kind switch
        {
            SetupProviderKind.Gmail => (GmailEndpoints.ProviderId, AuthKind.OAuth),
            SetupProviderKind.Outlook => ("microsoft.graph", AuthKind.OAuth),
            SetupProviderKind.Qq or SetupProviderKind.NetEase => ("imap.generic", AuthKind.AppPassword),
            _ => ("imap.generic", AuthKind.Password)
        };

        var displayName = string.IsNullOrWhiteSpace(draft.DisplayName) ? draft.EmailAddress : draft.DisplayName.Trim();
        var account = new AccountEntity(draft.Id, displayName, draft.EmailAddress, providerId, authKind, now, now, null, true);

        // 先写凭据，再写数据库：数据库里出现的账号一定有可用的凭据。
        var vaultKey = draft.Kind switch
        {
            SetupProviderKind.Gmail => AccountConnectionStore.GmailTokenKey(draft.Id),
            SetupProviderKind.Outlook => AccountConnectionStore.GraphTokenKey(draft.Id),
            _ => AccountConnectionStore.ImapSecretKey(draft.Id)
        };

        var connection = new AccountConnection(draft.Kind, draft.EffectiveUserName, draft.Imap, draft.Smtp);
        await _connections.SaveAsync(
                draft.Id,
                connection,
                draft.IsOAuth ? null : draft.Password ?? string.Empty,
                cancellationToken)
            .ConfigureAwait(false);

        await _db.UpsertAccountAsync(account, cancellationToken).ConfigureAwait(false);
        await _db.UpsertCredentialsMetaAsync(new CredentialsMetaEntity(draft.Id, vaultKey), cancellationToken).ConfigureAwait(false);
        return account;
    }

    public Task DiscardAsync(AccountDraft draft)
    {
        ArgumentNullException.ThrowIfNull(draft);
        return _connections.DeleteAllAsync(draft.Id);
    }

    public async Task RemoveAccountAsync(Guid accountId, CancellationToken cancellationToken)
    {
        var accounts = await _db.ListAccountsAsync(cancellationToken).ConfigureAwait(false);
        var account = accounts.FirstOrDefault(a => a.Id == accountId);
        await _db.DeleteAccountAsync(accountId, cancellationToken).ConfigureAwait(false);

        // 尽力注销（Google 令牌吊销 / MSAL 账号移除）；失败不影响本地删除。
        try
        {
            if (account?.ProviderId == GmailEndpoints.ProviderId && _settings.GmailClientId is { Length: > 0 } gid)
            {
                using var acquirer = new GmailTokenAcquirer(
                    new GmailAccountConfig(accountId, account.EmailAddress, account.DisplayName, gid, _settings.GmailClientSecret), _store);
                await acquirer.SignOutAsync(CancellationToken.None).ConfigureAwait(false);
            }
            else if (account?.ProviderId == "microsoft.graph" && _settings.MicrosoftClientId is { Length: > 0 } mid)
            {
                var acquirer = new MicrosoftGraphTokenAcquirer(
                    new MicrosoftGraphAccountConfig(accountId, account.EmailAddress, account.DisplayName, mid, _settings.MicrosoftTenantId),
                    _store,
                    windowHandleProvider: _windowHandle);
                await acquirer.SignOutAsync(CancellationToken.None).ConfigureAwait(false);
            }
        }
        catch
        {
            // ignore
        }

        await _connections.DeleteAllAsync(accountId, CancellationToken.None).ConfigureAwait(false);
    }

    public Task<AutoconfigResult?> LookupAutoconfigAsync(string email, CancellationToken cancellationToken)
        => IspdbAutoconfig.LookupAsync(_http.Value, email, cancellationToken);

    private string? PersistGmail(AccountDraft draft)
    {
        var clientId = Normalize(draft.ClientId) ?? _settings.GmailClientId;
        if (Normalize(draft.ClientId) is { } entered)
        {
            var secret = Normalize(draft.ClientSecret);
            _settings.Update(s =>
            {
                s.GmailClientId = entered;
                s.GmailClientSecret = secret;
            });
        }

        return clientId;
    }

    private string? PersistMicrosoft(AccountDraft draft, out string tenant)
    {
        var entered = Normalize(draft.ClientId);
        if (entered is not null)
        {
            var tenantEntered = Normalize(draft.TenantId);
            _settings.Update(s =>
            {
                s.MicrosoftClientId = entered;
                if (tenantEntered is not null)
                {
                    s.MicrosoftTenantId = tenantEntered;
                }
            });
        }

        tenant = _settings.MicrosoftTenantId;
        return entered ?? _settings.MicrosoftClientId;
    }

    private GmailAccountConfig GmailConfig(AccountDraft draft, string clientId)
        => new(
            draft.Id,
            draft.EmailAddress ?? string.Empty,
            string.IsNullOrWhiteSpace(draft.DisplayName) ? draft.EmailAddress ?? string.Empty : draft.DisplayName,
            clientId,
            Normalize(draft.ClientSecret) ?? _settings.GmailClientSecret);

    private static MicrosoftGraphAccountConfig GraphConfig(AccountDraft draft, string clientId, string tenant)
        => new(
            draft.Id,
            draft.EmailAddress ?? string.Empty,
            string.IsNullOrWhiteSpace(draft.DisplayName) ? draft.EmailAddress ?? string.Empty : draft.DisplayName,
            clientId,
            tenant);

    private static string? Normalize(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
