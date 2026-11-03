namespace MailClient.Core;

/// <summary>
/// Microsoft Graph / Outlook 账号配置（公共客户端，无 client secret）。
/// ClientId 需在 Entra ID 应用注册中创建后填入；留空则无法交互登录。
/// </summary>
public sealed record MicrosoftGraphAccountConfig(
    Guid AccountId,
    string EmailAddress,
    string DisplayName,
    string ClientId,
    string TenantId = "common",
    string? RedirectUri = null,
    string? CredentialVaultKey = null,
    int SyncBatchSize = 50,
    bool PreferDeltaSync = true,
    /// <summary>
    /// 是否使用 Windows WAM（系统账号）Broker。默认 false：走系统浏览器，更稳。
    /// 开启后若 WAM 报 ApiContractViolation / 假网络错误，会自动回退浏览器一次。
    /// </summary>
    bool UseWamBroker = false)
{
    /// <summary>
    /// 占位 ClientId。请替换为你在 Entra ID 注册的「公共客户端 / 移动和桌面」应用 ID。
    /// 切勿在仓库中提交真实生产密钥；公共客户端本来也不应有 client secret。
    /// </summary>
    public const string PlaceholderClientId = "";

    /// <summary>MSAL 默认本机回环重定向（系统浏览器）。</summary>
    public const string DefaultRedirectUri = "http://localhost";

    public string EffectiveRedirectUri =>
        string.IsNullOrWhiteSpace(RedirectUri) ? DefaultRedirectUri : RedirectUri;

    public string EffectiveTenantId =>
        string.IsNullOrWhiteSpace(TenantId) ? "common" : TenantId;
}

/// <summary>Graph 委托权限范围（MVP）。</summary>
public static class MicrosoftGraphScopes
{
    public static readonly string[] Default =
    [
        "User.Read",
        "Mail.Read",
        "Mail.ReadWrite",
        "Mail.Send",
        "offline_access"
    ];
}
