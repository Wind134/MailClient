namespace MailClient.Core.Sending;

/// <summary>各服务商可接受的附件总大小（原始字节，已扣除 base64 约 33% 的膨胀）。超过只做警告，不拦截发送（由服务器最终裁决）。</summary>
public static class AttachmentLimits
{
    public const long GraphInlineLimit = 3L * 1024 * 1024;

    /// <param name="providerId">账号的 ProviderId（gmail.oauth / microsoft.graph / imap.generic）。</param>
    /// <param name="emailAddress">用于识别 QQ / 网易这类按域名区分的预设。</param>
    public static long For(string providerId, string? emailAddress = null)
    {
        if (string.Equals(providerId, "microsoft.graph", StringComparison.OrdinalIgnoreCase))
        {
            return GraphInlineLimit;
        }

        if (string.Equals(providerId, "gmail.oauth", StringComparison.OrdinalIgnoreCase))
        {
            return 18L * 1024 * 1024;
        }

        var domain = emailAddress is { Length: > 0 } && emailAddress.Contains('@')
            ? emailAddress[(emailAddress.IndexOf('@') + 1)..].ToLowerInvariant()
            : string.Empty;
        return domain is "qq.com" or "foxmail.com" or "163.com" or "126.com" or "yeah.net" or "vip.163.com"
            ? 30L * 1024 * 1024
            : 18L * 1024 * 1024;
    }
}
