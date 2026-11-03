namespace MailClient.Core.Updates;

/// <summary>
/// 「检查更新」的配置。**要换成别的仓库，只改下面的 <see cref="Repository"/> 这一处**（格式 owner/repo）。
/// </summary>
public static class UpdateConfig
{
    /// <summary>GitHub 仓库（owner/repo）。检查更新读它的 Releases；唯一的配置点。</summary>
    public const string Repository = "Wind134/MailClient";

    /// <summary>发布资源里安装程序的文件名模式（* 通配）。</summary>
    public const string InstallerAssetPattern = "MailClient-Setup-*.exe";

    /// <summary>发布资源里的校验和文件（每行「sha256  文件名」）。</summary>
    public const string ChecksumAssetName = "SHA256SUMS.txt";

    /// <summary>
    /// 隐藏的调试 / 测试开关：环境变量 MAILCLIENT_UPDATE_FEED 指向一个本地 JSON 文件路径，
    /// 或一个 http(s) 地址，内容与 GitHub 的 releases/latest 响应相同。设置后不再访问 GitHub API。
    /// </summary>
    public const string FeedEnvironmentVariable = "MAILCLIENT_UPDATE_FEED";

    /// <summary>启动后多久做第一次检查。</summary>
    public static readonly TimeSpan InitialDelay = TimeSpan.FromSeconds(30);

    /// <summary>之后每隔多久检查一次（应用常驻托盘）。</summary>
    public static readonly TimeSpan CheckInterval = TimeSpan.FromHours(6);

    /// <summary>单次网络请求的超时。</summary>
    public static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(20);

    public static string LatestReleaseApiUrl(string repository = Repository)
        => $"https://api.github.com/repos/{repository}/releases/latest";

    public static string ReleasesPageUrl(string repository = Repository)
        => $"https://github.com/{repository}/releases";

    public static string UserAgent(Version current)
        => $"MailClient/{current} (+https://github.com/{Repository})";

    /// <summary>当前生效的调试源（环境变量），没设置返回 null。</summary>
    public static string? FeedOverride
    {
        get
        {
            var v = Environment.GetEnvironmentVariable(FeedEnvironmentVariable);
            return string.IsNullOrWhiteSpace(v) ? null : v.Trim();
        }
    }

    /// <summary>
    /// 安装包下载地址是否可信：必须是 https，且在 github.com 或 *.githubusercontent.com 下；
    /// 仅当启用了调试源（<paramref name="allowLoopbackHttp"/>）时，额外允许本机回环地址的 http。
    /// </summary>
    public static bool IsTrustedDownloadUrl(string? url, bool allowLoopbackHttp = false)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return false;
        }

        if (uri.Scheme == Uri.UriSchemeHttps)
        {
            var host = uri.Host.ToLowerInvariant();
            return host == "github.com" || host.EndsWith(".github.com", StringComparison.Ordinal)
                || host == "githubusercontent.com" || host.EndsWith(".githubusercontent.com", StringComparison.Ordinal);
        }

        return allowLoopbackHttp && uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback;
    }
}