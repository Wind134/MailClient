using MailClient.Core;

namespace MailClient.Providers.Setup;

public enum SetupStage
{
    Login,
    Imap,
    Smtp,
    Graph
}

public enum SetupStageStatus
{
    Running,
    Succeeded,
    Failed
}

public sealed record SetupProgress(SetupStage Stage, SetupStageStatus Status, string? Message = null);

public enum SetupErrorKind
{
    Unknown,
    Cancelled,
    AuthFailed,
    NeedAuthCode,
    ServerUnreachable,
    HostNotFound,
    TlsError,
    Timeout,
    Protocol,
    UnsafeLogin,
    MissingClientId,
    OAuthDenied,
    AccountMismatch,
    Network
}

public sealed record SetupError(SetupErrorKind Kind, string Message, string? Detail = null);

/// <summary>向导正在添加的账号草稿：从填写、验证到保存都用它。</summary>
/// <param name="UserName">IMAP/SMTP 登录名；null = 邮箱地址。</param>
/// <param name="Password">密码 / 授权码（仅 IMAP 类账号；用完即丢，只进 ICredentialStore）。</param>
public sealed record AccountDraft(
    Guid Id,
    SetupProviderKind Kind,
    string EmailAddress,
    string DisplayName,
    string? UserName = null,
    string? Password = null,
    ImapServerSettings? Imap = null,
    SmtpServerSettings? Smtp = null,
    string? ClientId = null,
    string? ClientSecret = null,
    string? TenantId = null)
{
    public string EffectiveUserName => string.IsNullOrWhiteSpace(UserName) ? EmailAddress : UserName!;

    public bool IsOAuth => Kind is SetupProviderKind.Gmail or SetupProviderKind.Outlook;
}

public sealed record SetupResult(
    bool Success,
    string? EmailAddress = null,
    string? DisplayName = null,
    SetupError? Error = null,
    SetupStage? FailedStage = null)
{
    public static SetupResult Ok(string? email = null, string? displayName = null) => new(true, email, displayName);

    public static SetupResult Fail(SetupError error, SetupStage? stage = null) => new(false, Error: error, FailedStage: stage);
}
