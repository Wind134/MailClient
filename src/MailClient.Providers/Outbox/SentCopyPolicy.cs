using MailClient.Providers.Setup;

namespace MailClient.Providers.Outbox;

/// <summary>
/// 发送成功后是否要把副本 APPEND 到服务器的“已发送”文件夹：
/// Gmail 与 Graph（saveToSentItems=true）服务器自己会保存，不能再追加（会重复）；
/// QQ / 163 / 126 / 其他 IMAP 通常不会保存，默认追加，可按预设在 settings.json 的 SentCopyOverrides 里覆盖。纯函数，可单测。
/// </summary>
public static class SentCopyPolicy
{
    public const string GraphProviderId = "microsoft.graph";
    public const string GmailProviderId = "gmail.oauth";

    public static string PresetKey(SetupProviderKind kind) => kind.ToString();

    public static bool DefaultFor(string providerId, SetupProviderKind kind)
    {
        if (string.Equals(providerId, GraphProviderId, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(providerId, GmailProviderId, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return kind switch
        {
            SetupProviderKind.Gmail or SetupProviderKind.Outlook => false,
            _ => true // Qq / NetEase / OtherImap
        };
    }

    /// <param name="userOverride">settings.json 中对应预设的覆盖值（null = 用默认）。Gmail / Graph 不可被覆盖为 true（会产生重复邮件）。</param>
    public static bool ShouldAppend(string providerId, SetupProviderKind kind, bool? userOverride)
    {
        if (string.Equals(providerId, GraphProviderId, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(providerId, GmailProviderId, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return userOverride ?? DefaultFor(providerId, kind);
    }
}
