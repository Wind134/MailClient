using System.Net.Http.Headers;
using System.Text.Json;
using MailClient.Core;

namespace MailClient.Auth;

/// <summary>
/// Gmail OAuth 令牌获取（Google OAuth 2.0 已安装应用流程：回环重定向 + PKCE + 系统浏览器）。
/// 流程：缓存的访问令牌仍有效 → 直接返回；快过期 → 用 refresh token 静默刷新；
/// 没有 refresh token（或已失效）→ 允许交互时拉起浏览器登录，否则抛 <see cref="GmailAuthRequiredException"/>。
/// refresh token 保存在 <see cref="ICredentialStore"/>（与 MSAL 缓存同一模式）。
/// </summary>
public sealed class GmailTokenAcquirer : IOAuthTokenSource, IDisposable
{
    /// <summary>距离过期不足该时长就提前刷新，避免把“马上过期”的令牌交给 IMAP。</summary>
    public static readonly TimeSpan RefreshSkew = TimeSpan.FromMinutes(2);

    private readonly GmailAccountConfig _config;
    private readonly GmailTokenStore _tokens;
    private readonly HttpClient _http;
    private readonly bool _ownsHttp;
    private readonly IBrowserLauncher _browser;
    private readonly Func<DateTimeOffset> _clock;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private GmailTokenSet? _cached;

    public GmailTokenAcquirer(
        GmailAccountConfig config,
        ICredentialStore credentialStore,
        HttpClient? httpClient = null,
        IBrowserLauncher? browserLauncher = null,
        Func<DateTimeOffset>? clock = null)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        ArgumentNullException.ThrowIfNull(credentialStore);
        if (string.IsNullOrWhiteSpace(config.ClientId))
        {
            throw new ArgumentException("GmailAccountConfig.ClientId 不能为空（请先在 Google Cloud Console 创建桌面应用 OAuth 客户端）。", nameof(config));
        }

        _tokens = new GmailTokenStore(credentialStore, config.EffectiveVaultKey);
        _ownsHttp = httpClient is null;
        _http = httpClient ?? new HttpClient();
        _browser = browserLauncher ?? new SystemBrowserLauncher();
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    /// <summary>令牌是否需要（提前）刷新。</summary>
    public static bool NeedsRefresh(DateTimeOffset expiresOn, DateTimeOffset now, TimeSpan? skew = null)
        => expiresOn - now <= (skew ?? RefreshSkew);

    /// <summary>是否已有保存的 refresh token（即不必再弹浏览器）。</summary>
    public async Task<bool> HasStoredLoginAsync(CancellationToken cancellationToken = default)
        => await _tokens.LoadAsync(cancellationToken).ConfigureAwait(false) is not null;

    /// <summary><see cref="IOAuthTokenSource"/>：后台同步 / 发信用，不会弹浏览器。</summary>
    public async Task<OAuthAccessToken> GetAccessTokenAsync(CancellationToken cancellationToken = default)
    {
        var result = await AcquireAsync(allowInteractive: false, cancellationToken).ConfigureAwait(false);
        return new OAuthAccessToken(result.AccountUsername ?? _config.EmailAddress, result.AccessToken, result.ExpiresOn);
    }

    public async Task<AccessTokenResult> AcquireAsync(bool allowInteractive = true, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var set = _cached ??= await _tokens.LoadAsync(cancellationToken).ConfigureAwait(false);

            if (set is not null && !NeedsRefresh(set.ExpiresOn, _clock()))
            {
                return ToResult(set);
            }

            if (set is not null)
            {
                try
                {
                    set = await RefreshAsync(set, cancellationToken).ConfigureAwait(false);
                    return ToResult(set);
                }
                catch (GoogleOAuthException ex) when (ex.IsInvalidGrant)
                {
                    // refresh token 已被撤销 / 过期（Testing 模式下 7 天）：清掉，走重新登录。
                    _cached = null;
                    await _tokens.DeleteAsync(cancellationToken).ConfigureAwait(false);
                    if (!allowInteractive)
                    {
                        throw new GmailAuthRequiredException("Gmail 登录已失效，请重新登录。", ex);
                    }
                }
            }

            if (!allowInteractive)
            {
                throw new GmailAuthRequiredException("尚未登录 Gmail，请先完成 Google 授权。");
            }

            set = await InteractiveLoginAsync(cancellationToken).ConfigureAwait(false);
            return ToResult(set);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>退出登录：尽力撤销 refresh token，并删除本地保存的令牌。</summary>
    public async Task SignOutAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var set = _cached ?? await _tokens.LoadAsync(cancellationToken).ConfigureAwait(false);
            _cached = null;
            if (set is not null)
            {
                try
                {
                    using var content = new FormUrlEncodedContent(new Dictionary<string, string> { ["token"] = set.RefreshToken });
                    using var _ = await _http.PostAsync(GmailEndpoints.RevocationEndpoint, content, cancellationToken).ConfigureAwait(false);
                }
                catch (HttpRequestException)
                {
                    // 撤销失败（如离线）不应阻止本地退出。
                }
            }

            await _tokens.DeleteAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private AccessTokenResult ToResult(GmailTokenSet set)
        => new(set.AccessToken, set.ExpiresOn, set.Email ?? _config.EmailAddress, HomeAccountId: null);

    private async Task<GmailTokenSet> RefreshAsync(GmailTokenSet current, CancellationToken ct)
    {
        var form = new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = current.RefreshToken,
            ["client_id"] = _config.ClientId
        };
        AddSecret(form);

        var response = GoogleTokenResponse.Parse(await PostTokenAsync(form, ct).ConfigureAwait(false), _clock());
        var next = current with
        {
            AccessToken = response.AccessToken,
            ExpiresOn = response.ExpiresOn,
            // 刷新响应通常不含 refresh_token：沿用旧的；若 Google 轮换了新的则采用新的。
            RefreshToken = string.IsNullOrEmpty(response.RefreshToken) ? current.RefreshToken : response.RefreshToken,
            Scope = response.Scope ?? current.Scope,
            Email = GoogleIdToken.TryReadEmail(response.IdToken) ?? current.Email
        };
        await _tokens.SaveAsync(next, ct).ConfigureAwait(false);
        _cached = next;
        return next;
    }

    private async Task<GmailTokenSet> InteractiveLoginAsync(CancellationToken ct)
    {
        using var listener = LoopbackAuthorizationListener.Start();
        var verifier = Pkce.CreateVerifier();
        var state = Pkce.CreateState();
        var url = GoogleAuthorizationUrl.Build(
            _config.ClientId,
            listener.RedirectUri,
            _config.EffectiveScopes,
            state,
            Pkce.CreateChallenge(verifier),
            loginHint: string.IsNullOrWhiteSpace(_config.EmailAddress) ? null : _config.EmailAddress);

        var wait = listener.WaitForResponseAsync(state, TimeSpan.FromSeconds(_config.LoginTimeoutSeconds), ct);
        await _browser.LaunchAsync(url, ct).ConfigureAwait(false);
        var callback = await wait.ConfigureAwait(false);

        if (!string.IsNullOrEmpty(callback.Error))
        {
            throw new GoogleOAuthException(callback.Error, callback.ErrorDescription);
        }

        var form = new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = callback.Code!,
            ["code_verifier"] = verifier,
            ["redirect_uri"] = listener.RedirectUri,
            ["client_id"] = _config.ClientId
        };
        AddSecret(form);

        var response = GoogleTokenResponse.Parse(await PostTokenAsync(form, ct).ConfigureAwait(false), _clock());
        if (string.IsNullOrEmpty(response.RefreshToken))
        {
            throw new GoogleOAuthException("missing_refresh_token", "Google 未返回 refresh token（请到 Google 账号“第三方访问权限”里移除本应用后重试）。");
        }

        var email = GoogleIdToken.TryReadEmail(response.IdToken)
            ?? await TryFetchEmailAsync(response.AccessToken, ct).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(_config.EmailAddress) && email is not null &&
            !string.Equals(email, _config.EmailAddress, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"登录的 Google 账号（{email}）与配置的邮箱（{_config.EmailAddress}）不一致。");
        }

        var set = new GmailTokenSet(response.AccessToken, response.RefreshToken, response.ExpiresOn, email ?? _config.EmailAddress, response.Scope);
        await _tokens.SaveAsync(set, ct).ConfigureAwait(false);
        _cached = set;
        return set;
    }

    private async Task<string?> TryFetchEmailAsync(string accessToken, CancellationToken ct)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, GmailEndpoints.UserInfoEndpoint);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
            return doc.RootElement.TryGetProperty("email", out var e) ? e.GetString() : null;
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException)
        {
            return null;
        }
    }

    private void AddSecret(Dictionary<string, string> form)
    {
        if (!string.IsNullOrEmpty(_config.ClientSecret))
        {
            form["client_secret"] = _config.ClientSecret!;
        }
    }

    /// <summary>POST token 端点；4xx 的 JSON 错误体也原样返回交给 Parse 抛 <see cref="GoogleOAuthException"/>。</summary>
    private async Task<string> PostTokenAsync(Dictionary<string, string> form, CancellationToken ct)
    {
        using var content = new FormUrlEncodedContent(form);
        using var response = await _http.PostAsync(GmailEndpoints.TokenEndpoint, content, ct).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode && !body.Contains("\"error\"", StringComparison.Ordinal))
        {
            throw new HttpRequestException($"Google token 端点返回 {(int)response.StatusCode}。", null, response.StatusCode);
        }

        return body;
    }

    public void Dispose()
    {
        _gate.Dispose();
        if (_ownsHttp)
        {
            _http.Dispose();
        }
    }
}
