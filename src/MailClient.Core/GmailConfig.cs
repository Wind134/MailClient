namespace MailClient.Core;

/// <summary>
/// Gmail（Google OAuth 2.0 · 已安装应用 / Desktop app 客户端类型）账号配置。
/// ClientId 需在 Google Cloud Console 创建「桌面应用」OAuth 客户端后填入（见 docs/gmail-oauth.md）。
/// Desktop 客户端会同时发放 client_secret，但 Google 明确说明它“并不是真正的机密”，
/// 因此这里作为可选配置：留空则按纯 PKCE 公共客户端流程（Google 对 Desktop 客户端仍可能要求带上）。
/// 切勿把真实 ClientId / ClientSecret 提交进仓库，请从环境变量或用户设置读取。
/// </summary>
public sealed record GmailAccountConfig(
    Guid AccountId,
    string EmailAddress,
    string DisplayName,
    string ClientId,
    string? ClientSecret = null,
    string? CredentialVaultKey = null,
    IReadOnlyList<string>? Scopes = null,
    int SyncBatchSize = 50,
    bool UseGmailCategories = true,
    int LoginTimeoutSeconds = 300)
{
    /// <summary>实际使用的 scope；未指定则取 <see cref="GmailScopes.Default"/>。</summary>
    public IReadOnlyList<string> EffectiveScopes => Scopes is { Count: > 0 } ? Scopes : GmailScopes.Default;

    public string EffectiveVaultKey =>
        string.IsNullOrWhiteSpace(CredentialVaultKey) ? GmailEndpoints.SuggestVaultKey(AccountId) : CredentialVaultKey!;
}

public static class GmailScopes
{
    /// <summary>IMAP / SMTP 全权限（受限 scope：公开发布需通过 Google 验证 + CASA 安全评估）。</summary>
    public const string MailFullAccess = "https://mail.google.com/";

    public const string OpenId = "openid";
    public const string Email = "email";
    public const string Profile = "profile";

    public static readonly string[] Default = [MailFullAccess, OpenId, Email, Profile];
}

/// <summary>Google OAuth / Gmail 端点与服务器常量。</summary>
public static class GmailEndpoints
{
    public const string AuthorizationEndpoint = "https://accounts.google.com/o/oauth2/v2/auth";
    public const string TokenEndpoint = "https://oauth2.googleapis.com/token";
    public const string RevocationEndpoint = "https://oauth2.googleapis.com/revoke";
    public const string UserInfoEndpoint = "https://openidconnect.googleapis.com/v1/userinfo";

    public const string ProviderId = "gmail.oauth";
    public const string ImapHost = "imap.gmail.com";
    public const int ImapPort = 993;
    public const string SmtpHost = "smtp.gmail.com";
    public const int SmtpPort = 465;

    /// <summary>凭据库键：保存 JSON 序列化的令牌（含 refresh token）。</summary>
    public static string SuggestVaultKey(Guid accountId) => $"oauth:gmail:{accountId:D}";
}

/// <summary>可向 IMAP/SMTP（XOAUTH2）提供访问令牌的来源。</summary>
public interface IOAuthTokenSource
{
    /// <summary>取得有效（必要时自动刷新）的访问令牌；需要重新登录时抛出异常。</summary>
    Task<OAuthAccessToken> GetAccessTokenAsync(CancellationToken cancellationToken = default);
}

/// <param name="UserName">XOAUTH2 的用户名（邮箱地址）。</param>
public sealed record OAuthAccessToken(string UserName, string AccessToken, DateTimeOffset ExpiresOn);
