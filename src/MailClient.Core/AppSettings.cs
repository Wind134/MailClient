using System.Text.Json;
using System.Text.Json.Serialization;

namespace MailClient.Core;

/// <summary>
/// 应用设置（%LocalAppData%\MailClient\settings.json）。
/// 注意：这里允许保存 OAuth ClientId / ClientSecret——Google 桌面应用的 client_secret 并不是真正的机密
/// （Google 文档如是说明），但仍请不要把真实值提交进仓库。账号密码 / 令牌绝不放在这里（见 ICredentialStore）。
/// </summary>
public sealed class AppSettings
{
    public string? GmailClientId { get; set; }

    public string? GmailClientSecret { get; set; }

    public string? MicrosoftClientId { get; set; }

    public string? MicrosoftTenantId { get; set; }

    /// <summary>用户已清除演示数据 → 以后库为空时也不再自动灌入演示邮件。</summary>
    public bool DemoDataDismissed { get; set; }

    /// <summary>首次启动的“添加账号”欢迎提示是否已处理过（点了“稍后再说”或已添加）。</summary>
    public bool FirstRunPromptDismissed { get; set; }

    /// <summary>账号颜色（AccountId:D → #RRGGBB）。</summary>
    public Dictionary<string, string> AccountColors { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// “发送成功后是否把副本 APPEND 到服务器的已发送文件夹”的按预设覆盖（键：Qq / NetEase / OtherImap；值 true / false）。
    /// 没有写的预设使用内置默认（QQ / 网易 / 其他 IMAP = true，Gmail / Outlook = false）。
    /// </summary>
    public Dictionary<string, bool> SentCopyOverrides { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>后台轮询间隔（分钟，默认 5，范围 1–120）。</summary>
    public int? SyncPollMinutes { get; set; }

    /// <summary>无 CONDSTORE 的服务器：对账窗口的最大邮件数（默认 2000）。</summary>
    public int? SyncWindowMessages { get; set; }

    /// <summary>无 CONDSTORE 的服务器：对账窗口的天数（默认 30）；窗口 = 最新 N 封 ∪ 最近 D 天。</summary>
    public int? SyncWindowDays { get; set; }

    /// <summary>是否对收件箱使用 IMAP IDLE 即时推送（默认 true；关掉则只轮询）。</summary>
    public bool? SyncIdleEnabled { get; set; }

    /// <summary>新邮件到达时弹 Windows 通知（默认开）。Windows 自己的“通知”设置仍然优先。</summary>
    public bool NotificationsEnabled { get; set; } = true;

    /// <summary>正文缓存：正文自上次打开 / 拉取起最长保留天数（默认 90；0 = 不按时间清理）。</summary>
    public int? BodyCacheMaxAgeDays { get; set; }

    /// <summary>正文缓存：总大小上限（MB，默认 200；0 = 不限）。</summary>
    public int? BodyCacheMaxMb { get; set; }

    /// <summary>点邮件里的链接怎么打开："System"（系统默认浏览器，默认）或 "Internal"（软件内置浏览器）。</summary>
    public string? LinkOpenMode { get; set; }

    /// <summary>关闭主窗口时保留在后台（托盘），默认开。关则点关闭真正退出。</summary>
    public bool KeepRunningInBackground { get; set; } = true;

    /// <summary>登录 Windows 时静默启动（不显示主窗口），默认关。只登记当前用户。</summary>
    public bool LaunchAtStartup { get; set; }

    /// <summary>自动检查更新（启动约 30 秒后，之后每 6 小时），默认开。</summary>
    public bool AutoCheckUpdates { get; set; } = true;

    /// <summary>用户点了「跳过此版本」的发布标签（如 v1.0.16）；自动检查不再为它提示，更新的版本出现后又会提示。</summary>
    public string? SkippedUpdateVersion { get; set; }

    /// <summary>是否已经提示过「已在托盘后台运行」。</summary>
    public bool TrayHintShown { get; set; }

    /// <summary>最近的搜索（最新在前，最多 10 条，去重）。</summary>
    public List<string> RecentSearches { get; set; } = new();
}

/// <summary>读写 <see cref="AppSettings"/>；线程安全。环境变量优先于文件，便于调试而不落盘。</summary>
public sealed class AppSettingsStore
{
    public const string EnvGmailClientId = "MAILCLIENT_GMAIL_CLIENT_ID";
    public const string EnvGmailClientSecret = "MAILCLIENT_GMAIL_CLIENT_SECRET";
    public const string EnvGraphClientId = "MAILCLIENT_GRAPH_CLIENT_ID";
    public const string EnvGraphTenantId = "MAILCLIENT_GRAPH_TENANT_ID";

    private readonly object _lock = new();
    private readonly string? _path;
    private AppSettings _current;

    /// <param name="path">null = 仅内存（测试）。</param>
    public AppSettingsStore(string? path)
    {
        _path = path;
        _current = Read(path);
    }

    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "MailClient",
        "settings.json");

    public static AppSettingsStore OpenDefault() => new(DefaultPath);

    public string? FilePath => _path;

    /// <summary>当前设置的快照副本（修改它不会影响存储；用 <see cref="Update"/> 写入）。</summary>
    public AppSettings Snapshot()
    {
        lock (_lock)
        {
            return Clone(_current);
        }
    }

    public void Update(Action<AppSettings> mutate)
    {
        ArgumentNullException.ThrowIfNull(mutate);
        lock (_lock)
        {
            mutate(_current);
            Write();
        }
    }

    public string? GmailClientId => FirstNonBlank(Environment.GetEnvironmentVariable(EnvGmailClientId), Snapshot().GmailClientId);

    public string? GmailClientSecret => FirstNonBlank(Environment.GetEnvironmentVariable(EnvGmailClientSecret), Snapshot().GmailClientSecret);

    public string? MicrosoftClientId => FirstNonBlank(Environment.GetEnvironmentVariable(EnvGraphClientId), Snapshot().MicrosoftClientId);

    public string MicrosoftTenantId => FirstNonBlank(Environment.GetEnvironmentVariable(EnvGraphTenantId), Snapshot().MicrosoftTenantId) ?? "common";

    /// <summary>双向同步参数（设置文件里没写的用默认值，并夹在合理范围内）。</summary>
    public Sync.SyncOptions GetSyncOptions()
    {
        var s = Snapshot();
        return Sync.SyncOptions.FromSettings(s.SyncPollMinutes, s.SyncWindowMessages, s.SyncWindowDays, s.SyncIdleEnabled);
    }

    /// <summary>正文缓存上限（设置里没写的用默认值，并夹在合理范围内）。</summary>
    public Retention.StorageOptions GetStorageOptions()
    {
        var s = Snapshot();
        return Retention.StorageOptions.FromSettings(s.BodyCacheMaxAgeDays, s.BodyCacheMaxMb);
    }

    /// <summary>链接打开方式（默认系统浏览器）。</summary>
    public Links.LinkOpenMode GetLinkOpenMode() => Links.LinkRouter.ParseMode(Snapshot().LinkOpenMode);

    /// <summary>关闭主窗口时是否保留在后台（默认开）。</summary>
    public bool GetKeepRunningInBackground() => Snapshot().KeepRunningInBackground;

    public bool GetAutoCheckUpdates() => Snapshot().AutoCheckUpdates;

    public string? GetSkippedUpdateVersion() => Snapshot().SkippedUpdateVersion;

    public string? GetAccountColor(Guid accountId)
    {
        lock (_lock)
        {
            return _current.AccountColors.TryGetValue(accountId.ToString("D"), out var c) ? c : null;
        }
    }

    public bool? GetSentCopyOverride(string presetKey)
    {
        lock (_lock)
        {
            return _current.SentCopyOverrides.TryGetValue(presetKey, out var v) ? v : null;
        }
    }

    public void SetAccountColor(Guid accountId, string? color)
        => Update(s =>
        {
            var key = accountId.ToString("D");
            if (string.IsNullOrWhiteSpace(color))
            {
                s.AccountColors.Remove(key);
            }
            else
            {
                s.AccountColors[key] = color!;
            }
        });

    private void Write()
    {
        if (_path is null)
        {
            return;
        }

        var dir = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        var tmp = _path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(_current, AppSettingsJsonContext.Default.AppSettings));
        File.Move(tmp, _path, overwrite: true);
    }

    private static AppSettings Read(string? path)
    {
        if (path is null || !File.Exists(path))
        {
            return new AppSettings();
        }

        try
        {
            var loaded = JsonSerializer.Deserialize(File.ReadAllText(path), AppSettingsJsonContext.Default.AppSettings) ?? new AppSettings();
            loaded.AccountColors = new Dictionary<string, string>(
                loaded.AccountColors ?? new Dictionary<string, string>(), StringComparer.OrdinalIgnoreCase);
            loaded.RecentSearches ??= new List<string>();
            return loaded;
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            return new AppSettings();
        }
    }

    private static AppSettings Clone(AppSettings s) => new()
    {
        GmailClientId = s.GmailClientId,
        GmailClientSecret = s.GmailClientSecret,
        MicrosoftClientId = s.MicrosoftClientId,
        MicrosoftTenantId = s.MicrosoftTenantId,
        DemoDataDismissed = s.DemoDataDismissed,
        FirstRunPromptDismissed = s.FirstRunPromptDismissed,
        AccountColors = new Dictionary<string, string>(s.AccountColors, StringComparer.OrdinalIgnoreCase),
        SentCopyOverrides = new Dictionary<string, bool>(s.SentCopyOverrides, StringComparer.OrdinalIgnoreCase),
        RecentSearches = new List<string>(s.RecentSearches ?? new List<string>()),
        SyncPollMinutes = s.SyncPollMinutes,
        SyncWindowMessages = s.SyncWindowMessages,
        SyncWindowDays = s.SyncWindowDays,
        SyncIdleEnabled = s.SyncIdleEnabled,
        NotificationsEnabled = s.NotificationsEnabled,
        BodyCacheMaxAgeDays = s.BodyCacheMaxAgeDays,
        BodyCacheMaxMb = s.BodyCacheMaxMb,
        LinkOpenMode = s.LinkOpenMode,
        KeepRunningInBackground = s.KeepRunningInBackground,
        LaunchAtStartup = s.LaunchAtStartup,
        AutoCheckUpdates = s.AutoCheckUpdates,
        SkippedUpdateVersion = s.SkippedUpdateVersion,
        TrayHintShown = s.TrayHintShown
    };

    private static string? FirstNonBlank(params string?[] values)
        => values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v))?.Trim();
}

/// <summary>裁剪发布下的设置 JSON 源生成上下文（避免反射 Deserialize 抛 InvalidOperationException）。</summary>
[JsonSerializable(typeof(AppSettings))]
[JsonSerializable(typeof(Dictionary<string, string>))]
[JsonSerializable(typeof(Dictionary<string, bool>))]
[JsonSerializable(typeof(List<string>))]
internal partial class AppSettingsJsonContext : JsonSerializerContext
{
}
