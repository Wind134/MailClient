namespace MailClient.Core;

/// <summary>
/// 各邮箱服务的 ProviderId。<b>持久化在 accounts.ProviderId 列里</b>，
/// 改动会让已有账号在下次启动时失配，因此只增不改。
/// </summary>
public static class ProviderIds
{
    /// <summary>Microsoft Graph（Outlook / Microsoft 365）。</summary>
    public const string MicrosoftGraph = "microsoft.graph";

    /// <summary>Gmail（OAuth + IMAP/SMTP）。</summary>
    public const string Gmail = "gmail.oauth";
}
