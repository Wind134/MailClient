using System.Runtime.InteropServices;
using MailClient.Setup.Core;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;
using WinRT.Interop;

namespace MailClient.Setup;

/// <summary>安装 / 卸载向导的唯一窗口：左侧步骤面板 + 右侧按步骤切换的页面。</summary>
public sealed partial class SetupWindow : Window
{
    private const int WindowWidth = 780;
    private const int WindowHeight = 520;

    private readonly SetupArguments _args;
    private readonly IntPtr _hwnd;
    private readonly string _version = SetupVersion.Current;
    private string _installDir = SetupPaths.DefaultInstallDir;
    private bool _busy;

    public SetupWindow(SetupArguments args)
    {
        _args = args;
        InitializeComponent();

        _hwnd = WindowNative.GetWindowHandle(this);
        Title = args.Mode == SetupMode.Uninstall ? "邮件 卸载程序" : "邮件 安装程序";
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(DragRegion);
        try
        {
            SystemBackdrop = new MicaBackdrop { Kind = MicaKind.Base };
        }
        catch (Exception)
        {
            // 不支持 Mica 的系统退回纯色
        }

        try
        {
            AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico"));
        }
        catch (Exception)
        {
            // 图标只是装饰
        }

        ConfigureWindowFrame();
        AppWindow.Closing += (_, e) =>
        {
            if (_busy)
            {
                e.Cancel = true; // 正在复制 / 删除文件时不允许中途关闭
            }
        };

        if (args.Preview is not null)
        {
            ShowPreview(args.Preview);
        }
        else if (args.Mode == SetupMode.Uninstall)
        {
            ShowUninstallConfirm();
        }
        else
        {
            ShowWelcome();
        }
    }

    private void ConfigureWindowFrame()
    {
        var scale = GetDpiForWindow(_hwnd) / 96.0;
        AppWindow.Resize(new SizeInt32((int)(WindowWidth * scale), (int)(WindowHeight * scale)));

        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.IsResizable = false;
            presenter.IsMaximizable = false;
        }

        var area = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Nearest).WorkArea;
        AppWindow.Move(new PointInt32(
            area.X + ((area.Width - AppWindow.Size.Width) / 2),
            area.Y + ((area.Height - AppWindow.Size.Height) / 2)));
    }

    // ───────────── 安装 ─────────────

    private void ShowWelcome()
    {
        Side.Configure(_version, "准备", "安装", "完成");
        var view = new SetupWelcomeView { WindowHandleProvider = () => _hwnd };

        var existing = AppRegistration.ReadInstalled();
        if (existing is not null && Directory.Exists(existing.InstallDir))
        {
            _installDir = existing.InstallDir;
            view.LockLocation();
            var from = string.IsNullOrEmpty(existing.Version) ? "旧版本" : "版本 " + existing.Version;
            view.SetNotice($"检测到已安装 {from}，将升级到 {_version}。你的邮件和设置都会保留。");
            view.DesktopShortcut = File.Exists(SetupPaths.DesktopShortcut);
        }
        else if (AppRegistration.FindMsixVersion() is { } msix)
        {
            var label = string.IsNullOrEmpty(msix) ? string.Empty : $"（{msix}）";
            view.SetNotice($"这台电脑还装着「邮件」的 MSIX 版{label}。exe 版沿用同一份邮件数据，两个版本请不要同时运行；确认新版可用后，在“设置 → 应用”里卸载旧版即可。");
        }

        if (!string.IsNullOrEmpty(_args.InstallDir))
        {
            _installDir = _args.InstallDir;
        }

        view.InstallDir = _installDir;
        view.DesktopShortcut = _args.DesktopShortcut && (existing is null || view.DesktopShortcut);
        try
        {
            view.SetSpaceInfo(SetupEngine.EnumeratePayload(AppContext.BaseDirectory).Sum(f => f.Length));
        }
        catch (Exception)
        {
            // 取不到大小就不显示
        }

        view.CancelRequested += Close;
        view.InstallRequested += () =>
        {
            _installDir = view.InstallDir;
            _ = RunInstallAsync(view.InstallDir, view.DesktopShortcut);
        };
        Host.Content = view;
    }

    private async Task RunInstallAsync(string installDir, bool desktopShortcut)
    {
        _busy = true;
        Side.SetActive(1);
        var progressView = new SetupProgressView("正在安装「邮件」");
        Host.Content = progressView;
        var progress = new Progress<SetupProgress>(progressView.Report);
        var started = DateTime.UtcNow;
        var request = new InstallRequest
        {
            PayloadDir = AppContext.BaseDirectory,
            InstallDir = installDir,
            Version = _version,
            DesktopShortcut = desktopShortcut,
        };

        string? error = null;
        try
        {
            await Task.Run(() => SetupEngine.Install(request, progress));
            var elapsed = DateTime.UtcNow - started;
            if (elapsed < TimeSpan.FromSeconds(2))
            {
                await Task.Delay(TimeSpan.FromSeconds(2) - elapsed); // 让进度动画至少完整播一下
            }

            progressView.Report(new SetupProgress(1, "安装完成"));
            await Task.Delay(500);
        }
        catch (SetupException ex)
        {
            error = ex.Message;
        }
        catch (Exception ex)
        {
            error = "安装过程中出了点问题：" + ex.Message;
        }

        _busy = false;
        if (error is null)
        {
            ShowInstallDone(installDir);
        }
        else
        {
            ShowInstallFailed(error, desktopShortcut);
        }
    }

    private void ShowInstallDone(string installDir)
    {
        Side.SetActive(3);
        var view = new SetupResultView();
        view.ConfigureSuccess("安装完成", $"「邮件」已安装到：{installDir}\n可以从开始菜单找到它。", "完成", "立即运行「邮件」");
        view.PrimaryClicked += launch =>
        {
            if (launch)
            {
                try
                {
                    SetupLauncher.LaunchApp(installDir);
                }
                catch (Exception)
                {
                    // 启动失败不影响安装结果
                }
            }

            Close();
        };
        Host.Content = view;
    }

    private void ShowInstallFailed(string message, bool desktopShortcut)
    {
        Side.SetActive(1);
        var view = new SetupResultView();
        view.ConfigureFailure("安装没有完成", message, "重试", "关闭");
        view.PrimaryClicked += retryClicked => _ = RunInstallAsync(_installDir, desktopShortcut);
        view.SecondaryClicked += Close;
        Host.Content = view;
    }

    // ───────────── 卸载 ─────────────

    private void ShowUninstallConfirm()
    {
        Side.Configure(_version, "确认", "卸载", "完成");
        var installed = AppRegistration.ReadInstalled();
        _installDir = installed?.InstallDir ?? AppContext.BaseDirectory.TrimEnd('\\', '/');

        var view = new SetupUninstallView();
        view.SetLocation(_installDir);
        view.CancelRequested += Close;
        view.UninstallRequested += deleteData => _ = RunUninstallAsync(deleteData);
        Host.Content = view;
    }

    private async Task RunUninstallAsync(bool deleteData)
    {
        _busy = true;
        Side.SetActive(1);
        var progressView = new SetupProgressView("正在卸载「邮件」");
        Host.Content = progressView;
        var progress = new Progress<SetupProgress>(progressView.Report);
        var started = DateTime.UtcNow;

        UninstallResult? result = null;
        string? error = null;
        try
        {
            result = await Task.Run(() => SetupEngine.Uninstall(
                new UninstallRequest { InstallDir = _installDir, DeleteUserData = deleteData }, progress));
            var elapsed = DateTime.UtcNow - started;
            if (elapsed < TimeSpan.FromSeconds(1.5))
            {
                await Task.Delay(TimeSpan.FromSeconds(1.5) - elapsed);
            }

            progressView.Report(new SetupProgress(1, "卸载完成"));
            await Task.Delay(500);
        }
        catch (Exception ex)
        {
            error = "卸载过程中出了点问题：" + ex.Message;
        }

        _busy = false;
        Side.SetActive(3);
        var view = new SetupResultView();
        if (error is not null || result is null)
        {
            view.ConfigureFailure("卸载没有完成", error ?? "未知错误", "关闭");
            view.PrimaryClicked += _ => Close();
        }
        else
        {
            var message = deleteData && result.UserDataDeleted
                ? "「邮件」已从这台电脑移除，本机的邮件数据和设置也已删除。"
                : "「邮件」已从这台电脑移除。你的邮件数据和设置仍保留在 %LocalAppData%\\MailClient，以后重新安装可以直接接着用。";
            view.ConfigureSuccess("卸载完成", message, "完成");
            if (result.UserDataError is not null)
            {
                view.SetHint("邮件数据文件夹没能删除（可能还有程序在使用它）：" + result.UserDataError);
            }

            view.PrimaryClicked += _ => Close();
        }

        Host.Content = view;
    }

    // ───────────── 预览（/preview=页面名，只显示界面，不改动任何东西；用来调样式和截图） ─────────────

    private void ShowPreview(string page)
    {
        var uninstall = page.StartsWith("uninstall", StringComparison.OrdinalIgnoreCase);
        Side.Configure(_version, uninstall ? new[] { "确认", "卸载", "完成" } : new[] { "准备", "安装", "完成" });
        switch (page.ToLowerInvariant())
        {
            case "progress":
            case "uninstall-progress":
            {
                Side.SetActive(1);
                var v = new SetupProgressView(uninstall ? "正在卸载「邮件」" : "正在安装「邮件」");
                v.Report(new SetupProgress(0.62, uninstall ? "正在删除程序文件…" : "正在复制文件（352/570）", @"Microsoft.WindowsAppRuntime.dll"));
                v.Jump(62);
                Host.Content = v;
                break;
            }

            case "done":
            {
                Side.SetActive(3);
                var v = new SetupResultView();
                v.ConfigureSuccess("安装完成", $"「邮件」已安装到：{SetupPaths.DefaultInstallDir}\n可以从开始菜单找到它。", "完成", "立即运行「邮件」");
                Host.Content = v;
                break;
            }

            case "error":
            {
                Side.SetActive(1);
                var v = new SetupResultView();
                v.ConfigureFailure("安装没有完成", "无法写入文件 Microsoft.UI.Xaml.dll，它可能正被其它程序占用。请先退出「邮件」后重试。", "重试", "关闭");
                Host.Content = v;
                break;
            }

            case "uninstall-done":
            {
                Side.SetActive(3);
                var v = new SetupResultView();
                v.ConfigureSuccess("卸载完成", "「邮件」已从这台电脑移除。你的邮件数据和设置仍保留在 %LocalAppData%\\MailClient，以后重新安装可以直接接着用。", "完成");
                Host.Content = v;
                break;
            }

            case "uninstall":
            {
                Side.SetActive(0);
                var v = new SetupUninstallView();
                v.SetLocation(SetupPaths.DefaultInstallDir);
                Host.Content = v;
                break;
            }

            case "upgrade":
            {
                Side.SetActive(0);
                var v = new SetupWelcomeView { WindowHandleProvider = () => _hwnd };
                v.InstallDir = SetupPaths.DefaultInstallDir;
                v.LockLocation();
                v.SetNotice($"检测到已安装 版本 1.0.13.0，将升级到 {_version}。你的邮件和设置都会保留。");
                v.SetSpaceInfo(398L * 1024 * 1024);
                Host.Content = v;
                break;
            }

            default:
            {
                Side.SetActive(0);
                var v = new SetupWelcomeView { WindowHandleProvider = () => _hwnd };
                v.InstallDir = SetupPaths.DefaultInstallDir;
                v.SetSpaceInfo(398L * 1024 * 1024);
                Host.Content = v;
                break;
            }
        }
    }

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);
}
