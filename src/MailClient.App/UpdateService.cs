using System.Diagnostics;
using System.Reflection;
using MailClient.Core;
using MailClient.Core.Logging;
using MailClient.Core.Updates;

namespace MailClient_App;

/// <summary>
/// 「检查更新」：启动约 30 秒后检查一次，之后每 6 小时一次（应用常驻托盘）；也可以手动检查。
/// 所有失败只写 update.log，不打扰用户、不崩溃。
/// </summary>
public sealed class UpdateService : IDisposable
{
    private readonly AppSettingsStore _settings;
    private readonly string _logPath;
    private readonly HttpClient _http;
    private readonly UpdateChecker _checker;
    private readonly CancellationTokenSource _cts = new();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private int _started;

    public UpdateService(AppSettingsStore settings, string dataDirectory)
    {
        _settings = settings;
        _logPath = Path.Combine(dataDirectory, "update.log");
        CurrentVersion = UpdateVersion.Normalize(Assembly.GetEntryAssembly()?.GetName().Version ?? new Version(0, 0, 0, 0));
        Edition = EditionDetector.Detect(AppIdentity.IsPackaged, AppContext.BaseDirectory);
        _http = UpdateChecker.CreateHttpClient(CurrentVersion);
        _checker = new UpdateChecker(_http, log: Log);
    }

    public Version CurrentVersion { get; }

    public UpdateEdition Edition { get; }

    /// <summary>自动检查发现、但用户还没看到的新版本。</summary>
    public ReleaseInfo? Pending { get; set; }

    public UpdateCheckResult? LastResult { get; private set; }

    public DateTimeOffset? LastChecked { get; private set; }

    /// <summary>自动检查发现新版本（在线程池线程上触发）。</summary>
    public event Action<ReleaseInfo>? UpdateAnnounced;

    public static string DownloadDirectory => Path.Combine(Path.GetTempPath(), "MailClient-Update");

    public void Start()
    {
        if (Interlocked.Exchange(ref _started, 1) == 1)
        {
            return;
        }

        CleanOldDownloads();
        _ = Task.Run(LoopAsync);
    }

    private async Task LoopAsync()
    {
        try
        {
            await Task.Delay(UpdateConfig.InitialDelay, _cts.Token).ConfigureAwait(false);
            while (!_cts.IsCancellationRequested)
            {
                if (_settings.GetAutoCheckUpdates())
                {
                    await CheckAsync(manual: false, _cts.Token).ConfigureAwait(false);
                }

                await Task.Delay(UpdateConfig.CheckInterval, _cts.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // 退出
        }
        catch (Exception ex)
        {
            Log("update loop stopped: " + ex.GetType().Name + ": " + ex.Message);
        }
    }

    /// <summary>检查一次。manual = 用户点了「检查更新」：无视「跳过此版本」，也不弹出通知（由调用方显示结果）。</summary>
    public async Task<UpdateCheckResult> CheckAsync(bool manual, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var result = await _checker.CheckAsync(CurrentVersion, ct).ConfigureAwait(false);
            LastResult = result;
            LastChecked = DateTimeOffset.Now;
            if (!manual && result.Status == UpdateCheckStatus.UpdateAvailable && result.Release is { } release)
            {
                if (UpdateSkipPolicy.ShouldAnnounce(release, CurrentVersion, _settings.GetSkippedUpdateVersion(), manual: false))
                {
                    var changed = Pending is null || !UpdateVersion.Normalize(Pending.Version).Equals(UpdateVersion.Normalize(release.Version));
                    Pending = release;
                    Log($"update: announcing {release.TagName} (new={changed})");
                    UpdateAnnounced?.Invoke(release);
                }
                else
                {
                    Log($"update: {release.TagName} was skipped by the user");
                }
            }

            return result;
        }
        finally
        {
            _gate.Release();
        }
    }

    public void SkipVersion(ReleaseInfo release)
    {
        _settings.Update(s => s.SkippedUpdateVersion = release.TagName);
        Pending = null;
        Log("update: user skipped " + release.TagName);
    }

    public Task<DownloadedUpdate> DownloadAsync(ReleaseInfo release, IProgress<DownloadProgress> progress, CancellationToken ct)
    {
        var downloader = new UpdateDownloader(_http, allowLoopbackHttp: UpdateConfig.FeedOverride is not null);
        Log($"update: downloading {release.TagName}");
        return downloader.DownloadInstallerAsync(release, DownloadDirectory, progress, ct);
    }

    /// <summary>静默启动安装程序：升级到已有的安装目录，装完自动重新打开；桌面上没有快捷方式就不新建。</summary>
    public bool LaunchInstaller(string setupPath)
    {
        try
        {
            var args = "/S /run";
            var desktop = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "邮件.lnk");
            if (!File.Exists(desktop))
            {
                args += " /nodesktop";
            }

            Log($"update: launching installer {Path.GetFileName(setupPath)} {args}");
            Process.Start(new ProcessStartInfo(setupPath, args) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(setupPath) });
            return true;
        }
        catch (Exception ex)
        {
            Log("update: cannot launch installer: " + ex.Message);
            return false;
        }
    }

    public void OpenReleasePage(ReleaseInfo? release)
    {
        try
        {
            var url = release?.HtmlUrl;
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
            {
                url = UpdateConfig.ReleasesPageUrl();
            }

            Log("update: opening release page " + url);
            Process.Start(new ProcessStartInfo(url!) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log("update: cannot open release page: " + ex.Message);
        }
    }

    private static void CleanOldDownloads()
    {
        try
        {
            if (Directory.Exists(DownloadDirectory))
            {
                foreach (var file in Directory.EnumerateFiles(DownloadDirectory))
                {
                    try { File.Delete(file); } catch (Exception) { /* 还在被安装程序占用就留着 */ }
                }

                try { Directory.Delete(DownloadDirectory); } catch (Exception) { /* 非空 */ }
            }
        }
        catch (Exception)
        {
            // 清理失败无所谓
        }
    }

    private void Log(string message)
    {
        try
        {
            RotatingLog.Append(_logPath, DateTimeOffset.Now.ToString("O") + " " + message + Environment.NewLine);
        }
        catch (Exception)
        {
            // 日志失败不影响任何事
        }
    }

    public void Dispose()
    {
        try { _cts.Cancel(); } catch (Exception) { }
        _http.Dispose();
    }
}