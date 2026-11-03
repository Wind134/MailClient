using MailClient.Core;
using Microsoft.Identity.Client;
using Microsoft.Identity.Client.Broker;

namespace MailClient.Auth;

/// <summary>
/// MSAL 公共客户端：优先静默续票；交互登录默认走系统浏览器（不用 WAM）。
/// UseWamBroker=true 时先试 WAM，若遇 ApiContractViolation / 假网络错误等则自动回退系统浏览器一次。
/// </summary>
public sealed class MicrosoftGraphTokenAcquirer
{
    /// <summary>WAM ApiContractViolation（常见缺 broker 重定向 URI）。</summary>
    public const int WamApiContractViolation = unchecked((int)0x80860003); // 2156265475

    /// <summary>WAM / WinRT 「需要 Internet」类错误（本机有网时也常误报）。</summary>
    public const int WamNetworkRequired = unchecked((int)0x800704CF);

    private readonly MicrosoftGraphAccountConfig _config;
    private readonly string[] _scopes;
    private readonly MsalTokenCacheHelper _cacheHelper;
    private IPublicClientApplication? _app;
    private readonly Func<IntPtr>? _windowHandleProvider;
    private bool _brokerEnabled;

    /// <param name="windowHandleProvider">
    /// 返回父窗口句柄（HWND）。启用 WAM 时交互登录必须有它；
    /// 默认系统浏览器模式可传 null，有句柄时仍会挂上以改善嵌入式体验体验。
    /// </param>
    public MicrosoftGraphTokenAcquirer(
        MicrosoftGraphAccountConfig config,
        ICredentialStore store,
        IEnumerable<string>? scopes = null,
        Func<IntPtr>? windowHandleProvider = null)
    {
        _windowHandleProvider = windowHandleProvider;
        _config = config ?? throw new ArgumentNullException(nameof(config));
        ArgumentNullException.ThrowIfNull(store);
        _scopes = scopes?.ToArray() ?? MicrosoftGraphScopes.Default;
        var vaultKey = string.IsNullOrWhiteSpace(config.CredentialVaultKey)
            ? MsalTokenCacheHelper.SuggestVaultKey(config.AccountId)
            : config.CredentialVaultKey!;
        _cacheHelper = new MsalTokenCacheHelper(store, vaultKey);
    }

    public MsalTokenCacheHelper CacheHelper => _cacheHelper;

    public bool IsBrokerEnabled => _brokerEnabled;

    /// <summary>最近一次交互登录是否走了 WAM；未做过交互则为 null。</summary>
    public bool? LastInteractiveUsedBroker { get; private set; }

    /// <summary>本次 Acquire 是否因 WAM 失败而回退过系统浏览器。</summary>
    public bool BrokerFallbackOccurred { get; private set; }

    /// <summary>WAM 所需的 broker 重定向 URI（需在 Entra「身份验证」里登记）。</summary>
    public static string BrokerRedirectUri(string clientId) =>
        $"ms-appx-web://microsoft.aad.brokerplugin/{clientId.Trim()}";

    /// <summary>
    /// 解析交互登录所需的父 HWND。
    /// AcquireTokenInteractiveParameterBuilder 只有 WithParentActivityOrWindow(IntPtr|object)，
    /// 没有 Func&lt;IntPtr&gt; 重载——若把 Func 直接传入会命中 object 重载，把委托本身当成父窗口。
    /// </summary>
    public static IntPtr ResolveParentHwnd(Func<IntPtr>? windowHandleProvider, bool requireForBroker)
    {
        var hwnd = windowHandleProvider?.Invoke() ?? IntPtr.Zero;
        if (hwnd == IntPtr.Zero && requireForBroker)
        {
            throw new InvalidOperationException(
                "Microsoft 登录需要主窗口句柄（window_handle_required）。请确保主窗口已显示后再点「用 Microsoft 登录」。");
        }

        return hwnd;
    }

    /// <summary>是否像 WAM / broker 失败（应回退系统浏览器或提示补重定向 URI）。</summary>
    public static bool IsWamBrokerFailure(Exception? ex)
    {
        for (var cur = ex; cur is not null; cur = cur.InnerException)
        {
            if (cur is MsalException msal)
            {
                var code = msal.ErrorCode ?? string.Empty;
                var msg = msal.Message ?? string.Empty;
                if (ContainsWamSignal(code) || ContainsWamSignal(msg))
                {
                    return true;
                }
            }

            var text = cur.Message ?? string.Empty;
            if (ContainsWamSignal(text) ||
                text.Contains(WamApiContractViolation.ToString(), StringComparison.Ordinal) ||
                text.Contains("0x80860003", StringComparison.OrdinalIgnoreCase) ||
                text.Contains("0x800704CF", StringComparison.OrdinalIgnoreCase) ||
                text.Contains("800704cf", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    public static bool ShouldRetryWithoutBroker(Exception ex, bool brokerWasEnabled) =>
        brokerWasEnabled && IsWamBrokerFailure(ex);

    /// <summary>
    /// 先按 preferBroker 调用 acquire；若判定为 WAM 失败则再以 useBroker=false 调一次。
    /// 供生产 AcquireAsync 与单元测试共用。
    /// </summary>
    public static async Task<T> AcquireWithBrokerFallbackAsync<T>(
        bool preferBroker,
        Func<bool, CancellationToken, Task<T>> acquire,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(acquire);
        try
        {
            return await acquire(preferBroker, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ShouldRetryWithoutBroker(ex, preferBroker))
        {
            return await acquire(false, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>面向用户的中文说明；WAM 失败时带上 broker 重定向 URI 与 ClientId。</summary>
    public static string FormatInteractiveFailure(Exception ex, string? clientId)
    {
        var first = FirstLine(ex.Message);
        if (!IsWamBrokerFailure(ex))
        {
            if (ex is MsalException msal && !string.IsNullOrEmpty(msal.ErrorCode))
            {
                return $"Microsoft 登录失败（{msal.ErrorCode}）：{first}";
            }

            return $"Microsoft 登录失败：{first}";
        }

        var id = string.IsNullOrWhiteSpace(clientId) ? "（你的 ClientId）" : clientId.Trim();
        var brokerUri = string.IsNullOrWhiteSpace(clientId)
            ? "ms-appx-web://microsoft.aad.brokerplugin/{ClientId}"
            : BrokerRedirectUri(clientId);

        return
            "Microsoft 系统账号组件（WAM）登录失败。" +
            "本客户端默认改用系统浏览器登录；若你仍启用了 WAM，请到 Microsoft Entra → 应用注册 → 身份验证，" +
            "平台选「移动和桌面应用程序」，添加重定向 URI：\n" +
            brokerUri +
            "\n并确认「受支持的账户类型」包含个人 Microsoft 账户（租户用 common）。" +
            $" 当前 ClientId：{id}。" +
            $" 原始错误：{first}";
    }

    public async Task<AccessTokenResult> AcquireAsync(
        bool allowInteractive = true,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(_config.ClientId))
        {
            throw new InvalidOperationException(
                "未配置 Microsoft Graph ClientId。请在 Entra ID 注册公共客户端应用后填入 MicrosoftGraphAccountConfig.ClientId。");
        }

        BrokerFallbackOccurred = false;
        LastInteractiveUsedBroker = null;

        // 静默续票：用当前配置构建（默认无 WAM）
        var preferBroker = _config.UseWamBroker && OperatingSystem.IsWindows();
        EnsureApp(useBroker: preferBroker);

        var accounts = await _app!.GetAccountsAsync().ConfigureAwait(false);
        var account = accounts.FirstOrDefault();

        if (account is not null)
        {
            try
            {
                var silent = await _app.AcquireTokenSilent(_scopes, account)
                    .ExecuteAsync(cancellationToken)
                    .ConfigureAwait(false);
                return ToResult(silent);
            }
            catch (MsalUiRequiredException) when (!allowInteractive)
            {
                throw;
            }
            catch (MsalUiRequiredException)
            {
                // fall through to interactive
            }
        }
        else if (!allowInteractive)
        {
            throw new MsalUiRequiredException(
                "no_account",
                "本地无 MSAL 账户缓存，且不允许交互登录。");
        }

        try
        {
            return await AcquireWithBrokerFallbackAsync(
                preferBroker,
                async (useBroker, ct) =>
                {
                    if (useBroker != _brokerEnabled || _app is null)
                    {
                        if (!useBroker && preferBroker)
                        {
                            BrokerFallbackOccurred = true;
                        }

                        EnsureApp(useBroker, forceRebuild: true);
                    }

                    LastInteractiveUsedBroker = _brokerEnabled;
                    return await ExecuteInteractiveAsync(ct).ConfigureAwait(false);
                },
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(FormatInteractiveFailure(ex, _config.ClientId), ex);
        }
    }

    public async Task SignOutAsync(CancellationToken cancellationToken = default)
    {
        EnsureApp(useBroker: false);
        var accounts = await _app!.GetAccountsAsync().ConfigureAwait(false);
        foreach (var account in accounts)
        {
            await _app.RemoveAsync(account).ConfigureAwait(false);
        }

        _cacheHelper.Save(null);
        cancellationToken.ThrowIfCancellationRequested();
    }

    private async Task<AccessTokenResult> ExecuteInteractiveAsync(CancellationToken cancellationToken)
    {
        var hwnd = ResolveParentHwnd(_windowHandleProvider, requireForBroker: _brokerEnabled);
        var request = _app!.AcquireTokenInteractive(_scopes)
            .WithPrompt(Prompt.SelectAccount);
        if (hwnd != IntPtr.Zero)
        {
            request = request.WithParentActivityOrWindow(hwnd);
        }

        var interactive = await request
            .ExecuteAsync(cancellationToken)
            .ConfigureAwait(false);
        return ToResult(interactive);
    }

    private void EnsureApp(bool useBroker, bool forceRebuild = false)
    {
        if (_app is not null && !forceRebuild && _brokerEnabled == useBroker)
        {
            return;
        }

        _app = BuildApp(useBroker);
    }

    private IPublicClientApplication BuildApp(bool useBroker)
    {
        var builder = PublicClientApplicationBuilder
            .Create(_config.ClientId)
            .WithAuthority(AzureCloudInstance.AzurePublic, _config.EffectiveTenantId)
            .WithRedirectUri(_config.EffectiveRedirectUri);

        _brokerEnabled = false;
        if (useBroker)
        {
            try
            {
                builder = builder.WithBroker(new BrokerOptions(BrokerOptions.OperatingSystems.Windows));
                _brokerEnabled = true;
            }
            catch
            {
                _brokerEnabled = false;
            }
        }

        // PCA 构建器有 Func<IntPtr> 重载；交互请求构建器没有——两边都挂上（有句柄时）。
        if (_windowHandleProvider is not null)
        {
            builder = builder.WithParentActivityOrWindow(_windowHandleProvider);
        }

        var app = builder.Build();
        RegisterCache(app.UserTokenCache);
        return app;
    }

    private void RegisterCache(ITokenCache cache)
    {
        cache.SetBeforeAccess(args =>
        {
            var data = _cacheHelper.Load();
            if (data is { Length: > 0 })
            {
                args.TokenCache.DeserializeMsalV3(data);
            }
        });

        cache.SetAfterAccess(args =>
        {
            if (!args.HasStateChanged)
            {
                return;
            }

            var data = args.TokenCache.SerializeMsalV3();
            _cacheHelper.Save(data);
        });
    }

    private static bool ContainsWamSignal(string text) =>
        text.Contains("WAM", StringComparison.OrdinalIgnoreCase) ||
        text.Contains("broker", StringComparison.OrdinalIgnoreCase) ||
        text.Contains("ApiContractViolation", StringComparison.OrdinalIgnoreCase) ||
        text.Contains("2156265475", StringComparison.Ordinal) ||
        text.Contains("window_handle_required", StringComparison.OrdinalIgnoreCase) ||
        text.Contains("需要 Internet", StringComparison.OrdinalIgnoreCase) ||
        text.Contains("Internet connection", StringComparison.OrdinalIgnoreCase);

    private static string FirstLine(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return "(无详细信息)";
        }

        var line = message.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n')[0].Trim();
        return line.Length <= 240 ? line : line[..240] + "…";
    }

    private static AccessTokenResult ToResult(AuthenticationResult result) =>
        new(
            result.AccessToken,
            result.ExpiresOn,
            result.Account?.Username,
            result.Account?.HomeAccountId?.Identifier);
}
