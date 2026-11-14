using MailClient.Auth;
using MailClient.Core;
using MailClient.Core.Tray;
using MailClient.Core.Notifications;
using MailClient.Core.Services;
using MailClient.Providers.Outbox;
using MailClient.Providers.Setup;
using MailClient.Providers.Sync;
using MailClient.Providers.WriteBack;
using MailClient.Storage;
using MailClient.Storage.Services;
using MailClient.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;
using Microsoft.Windows.AppNotifications;
using System.Net.NetworkInformation;
using System.Threading;
using System.Threading.Tasks;

namespace MailClient_App;

public partial class App : Application
{
    private MainWindow? _window;
    private MailDatabase? _database;
    private IntPtr _hwnd;
    private OutboxWorker? _outboxWorker;
    private MailClient.Storage.Services.StorageMaintenanceService? _storageMaintenance;
    private PendingOpsWorker? _pendingOpsWorker;
    private SyncCoordinator? _syncCoordinator;
    private NotificationService? _notifications;
    private ShellViewModel? _shell;
    private AppSettingsStore? _settingsStore;
    private TrayIconService? _tray;

    // 系统通知的点击：外壳（数据库 / 视图模型）就绪之前先排队
    private readonly object _toastGate = new();
    private readonly Queue<ToastAction> _toastQueue = new();
    private bool _shellReady;
    private bool _backgroundLaunch;
    private bool _silentLaunch;
    private bool _exiting;
    private (string Key, DateTime At)? _lastToast;
    private UpdateService? _updates;

    /// <summary>命令行带 <c>--silent</c>（开机启动）时由 <see cref="Program"/> 在创建窗口前设上。</summary>
    internal static bool LaunchSilently { get; set; }

    public App()
    {
        WriteStartupBreadcrumb("app:ctor-before-InitializeComponent");
        // 仅开发 / 截图用：MAILCLIENT_THEME=dark|light 强制界面主题（默认跟随系统）
        var themeOverride = Environment.GetEnvironmentVariable("MAILCLIENT_THEME");
        if (string.Equals(themeOverride, "dark", StringComparison.OrdinalIgnoreCase))
        {
            RequestedTheme = ApplicationTheme.Dark;
        }
        else if (string.Equals(themeOverride, "light", StringComparison.OrdinalIgnoreCase))
        {
            RequestedTheme = ApplicationTheme.Light;
        }

        InitializeComponent();
        WriteStartupBreadcrumb("app:ctor-after-InitializeComponent");
    }

    protected override async void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
    {
        // 本地库：%LocalAppData%\MailClient\mail.db（可用环境变量 MAILCLIENT_DB 覆盖，便于演示 / 调试）
        var path = Environment.GetEnvironmentVariable("MAILCLIENT_DB");
        string settingsPath;
        string credentialsPath;
        if (string.IsNullOrWhiteSpace(path))
        {
            path = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "MailClient",
                "mail.db");
            settingsPath = AppSettingsStore.DefaultPath;
            credentialsPath = DpapiCredentialStore.DefaultPath;
        }
        else
        {
            // 指定了自定义库时，设置和凭据也放在它旁边，避免演示 / 测试污染真实数据
            var dir = Path.GetDirectoryName(Path.GetFullPath(path)) ?? ".";
            settingsPath = Path.Combine(dir, "settings.json");
            credentialsPath = Path.Combine(dir, "credentials.dat");
        }

        // 系统通知：理想顺序是先 Register 再读激活参数。但 Register()/GetActivatedEventArgs()
        // 在本机会话里会同步卡住（等 notification 平台 / svchost），且不可用 UI 线程 Wait 后台任务
        //（STA 死锁）。因此：先尽量非阻塞地读激活参数 → 建窗 Activate → 再登记通知。
        _notifications = new NotificationService();
        _notifications.ActionInvoked += OnToastAction;
        WriteStartupBreadcrumb("onlaunched:begin");
        ToastAction? launchToast = null;
        ExtendedActivationKind? activatedKind = null;
        string? launchArguments = null;
        // GetActivatedEventArgs 也可能卡住：放到独立 STA 线程并限时，避免堵死 UI
        try
        {
            AppActivationArguments? activated = null;
            Exception? actEx = null;
            var thr = new Thread(() =>
            {
                try
                {
                    activated = AppInstance.GetCurrent().GetActivatedEventArgs();
                }
                catch (Exception ex)
                {
                    actEx = ex;
                }
            });
            thr.IsBackground = true;
            thr.SetApartmentState(ApartmentState.STA);
            thr.Start();
            if (!thr.Join(TimeSpan.FromSeconds(2)))
            {
                WriteStartupBreadcrumb("onlaunched:activated-timeout");
            }
            else if (actEx is not null)
            {
                WriteStartupBreadcrumb("onlaunched:activated-fail " + actEx.GetType().Name);
            }
            else if (activated is not null)
            {
                WriteStartupBreadcrumb("onlaunched:got-activated kind=" + activated.Kind);
                activatedKind = activated.Kind;
                if (activated.Kind == ExtendedActivationKind.Launch
                    && activated.Data is Windows.ApplicationModel.Activation.ILaunchActivatedEventArgs launchArgs)
                {
                    launchArguments = launchArgs.Arguments;
                }

                if (activated.Kind == ExtendedActivationKind.AppNotification
                    && activated.Data is AppNotificationActivatedEventArgs toastArgs)
                {
                    launchToast = NotificationService.ParseArguments(toastArgs.Arguments);
                }
            }
        }
        catch (Exception ex)
        {
            WriteStartupBreadcrumb("onlaunched:activated-outer-fail " + ex.GetType().Name);
        }

        // 点的是“标为已读 / 删除”按钮而冷启动：不弹主窗口，做完就退出
        _backgroundLaunch = launchToast?.RunsInBackground == true;
        // 开机启动：不弹主窗口，但留在托盘继续收信。通知按钮的后台路径优先（做完要退出）。
        _silentLaunch = !_backgroundLaunch && (
            LaunchSilently
            || activatedKind == ExtendedActivationKind.StartupTask
            || MailClient.Core.Tray.StartupLaunch.HasSilentArgument(launchArguments));
        if (launchToast is not null)
        {
            OnToastAction(launchToast);
        }

        WriteStartupBreadcrumb("onlaunched:before-db");
        _database = new MailDatabase(path);
        WriteStartupBreadcrumb("onlaunched:after-db");
        WriteStartupBreadcrumb("onlaunched:before-settings path=" + settingsPath);
        AppSettingsStore settings;
        try
        {
            settings = new AppSettingsStore(settingsPath);
            WriteStartupBreadcrumb("onlaunched:after-settings");
        }
        catch (Exception ex)
        {
            WriteStartupBreadcrumb("onlaunched:settings-fail " + ex.GetType().Name + ": " + ex.Message);
            settings = new AppSettingsStore(null);
        }
        _settingsStore = settings;
        try
        {
            StartupRegistration.ReconcileUnpackaged(settings);
        }
        catch (Exception ex)
        {
            WriteStartupBreadcrumb("startup-reconcile-fail " + ex.GetType().Name + ": " + ex.Message);
        }
        WriteStartupBreadcrumb("onlaunched:before-credentials");
        ICredentialStore credentials = new DpapiCredentialStore(credentialsPath);
        WriteStartupBreadcrumb("onlaunched:after-credentials");
        // WAM 登录需要窗口句柄；窗口创建后才会赋值，只在 UI 线程上读取这个字段。
        Func<IntPtr> hwnd = () => _hwnd;
        WriteStartupBreadcrumb("onlaunched:before-setup");
        IAccountSetupService setup = new AccountSetupService(_database, credentials, settings, hwnd);
        WriteStartupBreadcrumb("onlaunched:after-setup");
        // 同步日志（UIDVALIDITY 重置、丢弃的待写回操作、IDLE 重连等）放在库旁边：%LocalAppData%\MailClient\sync.log
        WriteStartupBreadcrumb("onlaunched:before-synclog");
        var syncLog = new FileSyncLog(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(path)) ?? ".", "sync.log"));
        WriteStartupBreadcrumb("onlaunched:after-synclog");
        WriteStartupBreadcrumb("onlaunched:before-account-sync");
        var accountSync = new AccountSyncService(_database, credentials, settings, hwnd, syncLog);
        WriteStartupBreadcrumb("onlaunched:after-account-sync");
        ISyncService sync = accountSync;
        accountSync.NewMailDetected += OnNewMailDetected;
        // 发件箱附件副本放在库旁边，发送（含重试）完成后才删除
        var attachmentRoot = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(path)) ?? ".", "outbox");
        ISendService sendService = new OutboxSendService(_database, attachmentRoot);
        // 草稿附件副本放在库旁边（%LocalAppData%\MailClient\drafts），原文件被移走 / 删除后草稿仍能带着附件重新打开
        var draftAttachmentRoot = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(path)) ?? ".", "drafts");
        var drafts = new DatabaseDraftStore(_database, draftAttachmentRoot);
        // 后台服务与正文懒加载共用同一套提供商工厂
        var providers = new AccountProviderFactory(credentials, settings, hwnd);
        var bodyLoader = new MailClient.Providers.Services.MessageBodyService(_database, providers);
        WriteStartupBreadcrumb("onlaunched:before-shell");
        var shell = new ShellViewModel(_database, sendService, null, sync, setup, settings, drafts, bodyLoader: bodyLoader);
        WriteStartupBreadcrumb("onlaunched:after-shell");

        // 发件箱投递 + 已读/标记/移动/删除 写回服务器（均不会弹登录窗口，需要重新登录时会在界面上提示）
        var outboxProcessor = new OutboxProcessor(_database, new OutgoingTransportFactory(providers), null, attachmentRoot);
        var pendingOpsProcessor = new PendingOpsProcessor(_database, new WriteGatewayFactory(providers));
        _outboxWorker = new OutboxWorker(outboxProcessor, _database);
        _pendingOpsWorker = new PendingOpsWorker(pendingOpsProcessor, _database);
        shell.AttachBackgroundServices(outboxProcessor, pendingOpsProcessor);

        _shell = shell;
        WriteStartupBreadcrumb("onlaunched:before-mainwindow");
        // 存储维护：按设置清理正文缓存、整理数据库、轮换日志；放在库旁边，耗时操作都在后台线程
        var dataDirectory = Path.GetDirectoryName(Path.GetFullPath(path)) ?? ".";
        _storageMaintenance = new MailClient.Storage.Services.StorageMaintenanceService(
            _database, dataDirectory, () => settings.GetStorageOptions(), message => syncLog.Info(message));
        _storageMaintenance.RotateLogs();
        _window = new MainWindow(shell) { AppSettings = settings, Notifications = _notifications, Storage = _storageMaintenance };
        WriteStartupBreadcrumb("onlaunched:after-mainwindow");
        WriteStartupBreadcrumb("onlaunched:window-created");
        if (_silentLaunch)
        {
            try
            {
                _window.AppWindow.Hide();
            }
            catch (Exception ex)
            {
                WriteStartupBreadcrumb("onlaunched:silent-hide-fail " + ex.GetType().Name);
            }

            _window.PromptFirstRunWhenShown = true;
            WriteStartupBreadcrumb("onlaunched:silent");
        }
        else if (!_backgroundLaunch)
        {
            _window.Activate();
            WriteStartupBreadcrumb("onlaunched:activated");
        }

        _hwnd = WinRT.Interop.WindowNative.GetWindowHandle(_window);
        // 冷启动只处理后台通知按钮时不进托盘，做完就会 Exit
        if (!_backgroundLaunch)
        {
            InstallTray(_window, settings, forceVisible: _silentLaunch);
        }

        // 窗口起来后再登记通知，避免 Register 卡死启动
        try
        {
            _notifications.Register();
            WriteStartupBreadcrumb("onlaunched:notify-registered ok=" + _notifications.IsRegistered + (_notifications.RegistrationError is { } regErr ? " error=" + regErr : string.Empty));
        }
        catch (Exception ex)
        {
            WriteStartupBreadcrumb("onlaunched:notify-register-fail " + ex.Message);
        }

        try
        {
            await shell.InitializeAsync(seedDemoData: false);
            WriteStartupBreadcrumb("onlaunched:init-done");
            // 清掉没有对应草稿的附件副本（例如上次崩溃时残留的）
            await drafts.CleanOrphanAttachmentsAsync();
        }
        catch (Exception ex)
        {
            shell.StatusMessage = $"初始化失败：{ex.Message}";
            shell.IsStatusError = true;
            shell.IsStatusOpen = true;
            return;
        }

        // 数据库就绪后再启动后台服务：启动时恢复卡在“发送中”的条目，并处理待发 / 待写回的内容
        _outboxWorker.Start();
        _pendingOpsWorker.Start();
        // 启动稍后在后台跑一次存储维护（清理超限正文 / 整理数据库），之后每 6 小时一次
        _storageMaintenance?.Start();

        // 双向同步：定时轮询 + Inbox 的 IDLE 推送；离线 / 系统休眠时暂停，恢复后立刻补一轮
        _syncCoordinator = new SyncCoordinator(accountSync, _database, new ProviderIdleRunner(providers), () => settings.GetSyncOptions(), syncLog);
        _syncCoordinator.SetOnline(NetworkInterface.GetIsNetworkAvailable());
        _syncCoordinator.Start();
        NetworkChange.NetworkAvailabilityChanged += (_, e) =>
        {
            _syncCoordinator?.SetOnline(e.IsAvailable);
            if (e.IsAvailable)
            {
                _outboxWorker?.KickNetworkAvailable();
                _pendingOpsWorker?.KickNetworkAvailable();
            }
        };
        try
        {
            Microsoft.Windows.System.Power.PowerManager.SystemSuspendStatusChanged += (_, _) =>
            {
                var status = Microsoft.Windows.System.Power.PowerManager.SystemSuspendStatus;
                _syncCoordinator?.SetSuspended(status == Microsoft.Windows.System.Power.SystemSuspendStatus.Entering);
            };
        }
        catch (Exception)
        {
            // 电源事件不可用时只靠网络状态暂停 / 恢复
        }

        // 检查更新：启动约 30 秒后第一次检查，之后每 6 小时一次（失败只写 update.log）
        try
        {
            _updates = new UpdateService(settings, dataDirectory);
            _window.Updates = _updates;
            _window.StartUpdatePrompts();
            if (!_backgroundLaunch)
            {
                _updates.Start();
            }
        }
        catch (Exception ex)
        {
            WriteStartupBreadcrumb("update:init-fail " + ex.GetType().Name + ": " + ex.Message);
        }

        // 外壳就绪：处理排队的通知点击
        lock (_toastGate)
        {
            _shellReady = true;
        }

        DrainToastQueue();

        _window.Closed += async (_, _) =>
        {
            await ShutdownServicesAsync();
        };

        if (_backgroundLaunch || _silentLaunch)
        {
            return; // 后台通知按钮做完即退出；开机静默启动则等用户打开窗口再提示
        }

        try
        {
            await _window.ShowFirstRunPromptIfNeededAsync();
        }
        catch (Exception ex)
        {
            // 欢迎提示不是关键路径，只在状态条里提示
            shell.StatusMessage = $"无法显示欢迎提示：{ex.Message}";
            shell.IsStatusError = true;
            shell.IsStatusOpen = true;
        }
    }

    // ───────── Windows 通知 ─────────

    /// <summary>同步发现新邮件（同步线程上）：套用方案并弹通知。永远不能让同步失败。</summary>
    private void OnNewMailDetected(object? sender, NewMailEventArgs e)
    {
        try
        {
            if (_settingsStore?.Snapshot().NotificationsEnabled == false)
            {
                return;
            }

            _notifications?.Show(NewMailPolicy.Plan(e.Messages));
        }
        catch (Exception)
        {
            // 通知失败不影响收信
        }
    }

    /// <summary>通知被点击：可能来自通知线程，也可能是冷启动的激活参数；外壳没好之前先排队。</summary>

    private static void WriteStartupBreadcrumb(string step)
    {
        try
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "MailClient");
            Directory.CreateDirectory(dir);
            var line = DateTimeOffset.Now.ToString("O") + " " + step + Environment.NewLine;
            MailClient.Core.Logging.RotatingLog.Append(Path.Combine(dir, "startup.log"), line);
        }
        catch
        {
            // 诊断用，失败忽略
        }
    }

    private void OnToastAction(ToastAction action)
    {
        lock (_toastGate)
        {
            // 冷启动时 Windows 可能既给激活参数又触发 NotificationInvoked：几秒内的相同动作只处理一次
            var key = $"{action.Kind}|{action.MessageId}|{action.AccountId}";
            if (_lastToast is { } last && last.Key == key && DateTime.UtcNow - last.At < TimeSpan.FromSeconds(5))
            {
                return;
            }

            _lastToast = (key, DateTime.UtcNow);
            _toastQueue.Enqueue(action);
            if (!_shellReady)
            {
                return;
            }
        }

        DrainToastQueue();
    }

    private void DrainToastQueue()
    {
        var window = _window;
        if (window is null)
        {
            return;
        }

        window.DispatcherQueue.TryEnqueue(async () =>
        {
            while (true)
            {
                ToastAction? next;
                lock (_toastGate)
                {
                    next = _toastQueue.Count > 0 ? _toastQueue.Dequeue() : null;
                }

                if (next is null)
                {
                    return;
                }

                try
                {
                    await HandleToastAsync(next);
                }
                catch (Exception ex)
                {
                    if (_shell is { } shell)
                    {
                        shell.StatusMessage = $"处理通知失败：{ex.Message}";
                        shell.IsStatusError = true;
                        shell.IsStatusOpen = true;
                    }
                }
            }
        });
    }

    private async Task HandleToastAsync(ToastAction action)
    {
        var shell = _shell;
        var window = _window;
        if (shell is null || window is null)
        {
            return;
        }

        switch (action.Kind)
        {
            case ToastActionKind.Open when action.MessageId is { } id:
                window.BringToFront();
                await shell.OpenMessageAsync(id);
                break;
            case ToastActionKind.OpenInbox:
                window.BringToFront();
                var inbox = action.AccountId is { } account
                    ? shell.Tree.FindNode(n => n.Kind == MailboxNodeKind.Folder && n.Special == SpecialFolder.Inbox && n.AccountId == account)
                    : null;
                inbox ??= shell.Tree.Favorites.FirstOrDefault(n => n.Kind == MailboxNodeKind.AllInboxes);
                if (inbox is not null)
                {
                    shell.Tree.SelectedNode = inbox;
                }

                break;
            case ToastActionKind.Update:
                await window.ShowUpdateFromToastAsync();
                break;
            case ToastActionKind.MarkRead when action.MessageId is { } readId:
                await shell.MarkReadByIdAsync(readId);
                await FinishBackgroundActionAsync(readId);
                break;
            case ToastActionKind.Delete when action.MessageId is { } deleteId:
                await shell.DeleteByIdAsync(deleteId);
                await FinishBackgroundActionAsync(deleteId);
                break;
        }
    }

    /// <summary>按钮动作做完后：清掉这条通知；若是冷启动（没有窗口）则等写回队列发完（最多 20 秒）再退出。</summary>
    private async Task FinishBackgroundActionAsync(string messageId)
    {
        if (_notifications is { } notifications)
        {
            await notifications.RemoveForMessageAsync(messageId);
        }

        if (!_backgroundLaunch || _database is null)
        {
            return;
        }

        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (DateTime.UtcNow < deadline)
        {
            _pendingOpsWorker?.Kick();
            try
            {
                if (await _database.CountPendingOpsAsync() == 0)
                {
                    break;
                }
            }
            catch (Exception)
            {
                break;
            }

            await Task.Delay(500);
        }

        _notifications?.Unregister();
        Exit();
    }

    // ───────── 托盘 / 单实例 ─────────

    /// <summary>开始菜单再次启动、或重定向激活：把已有窗口唤到前台。</summary>
    internal void OnRedirectedActivation(AppActivationArguments args)
    {
        WriteStartupBreadcrumb("redirected-activation kind=" + args.Kind);
        var window = _window;
        if (window is null)
        {
            return;
        }

        // 安装 / 卸载程序升级前发来的“请退出”：直接正常退出（会走 ShutdownServicesAsync）
        if (IsQuitRequest(args))
        {
            window.DispatcherQueue.TryEnqueue(window.RequestQuit);
            return;
        }

        // 又一次开机静默启动打到已在跑的进程：保持隐藏，不要把窗口拽出来
        if (IsSilentRequest(args))
        {
            WriteStartupBreadcrumb("redirected-silent");
            return;
        }

        // 若是点通知冷/热启动进来的，把动作也排进队列
        try
        {
            if (args.Kind == ExtendedActivationKind.AppNotification
                && args.Data is AppNotificationActivatedEventArgs toastArgs)
            {
                var action = NotificationService.ParseArguments(toastArgs.Arguments);
                if (action is not null)
                {
                    OnToastAction(action);
                }
            }
        }
        catch (Exception ex)
        {
            WriteStartupBreadcrumb("redirected-toast-fail " + ex.GetType().Name);
        }

        window.DispatcherQueue.TryEnqueue(() =>
        {
            try
            {
                window.BringToFront();
            }
            catch
            {
                // ignore
            }
        });
    }

    private static bool IsSilentRequest(AppActivationArguments args)
    {
        try
        {
            if (args.Kind == ExtendedActivationKind.StartupTask)
            {
                return true;
            }

            return args.Kind == ExtendedActivationKind.Launch
                && args.Data is Windows.ApplicationModel.Activation.ILaunchActivatedEventArgs launch
                && MailClient.Core.Tray.StartupLaunch.HasSilentArgument(launch.Arguments);
        }
        catch
        {
            return false;
        }
    }

    private static bool IsQuitRequest(AppActivationArguments args)
    {
        try
        {
            return args.Kind == ExtendedActivationKind.Launch
                && args.Data is Windows.ApplicationModel.Activation.ILaunchActivatedEventArgs launch
                && launch.Arguments?.Contains(Program.QuitArgument, StringComparison.OrdinalIgnoreCase) == true;
        }
        catch
        {
            return false;
        }
    }

    private void InstallTray(MainWindow window, AppSettingsStore settings, bool forceVisible = false)
    {
        try
        {
            _tray = new TrayIconService();
            _tray.OpenRequested += () => window.DispatcherQueue.TryEnqueue(window.BringToFront);
            _tray.SyncAllRequested += () => window.DispatcherQueue.TryEnqueue(() =>
            {
                if (_shell is { CanSync: true } shell)
                {
                    _ = shell.SyncAsync(null, userInitiated: true);
                }
            });
            _tray.ExitRequested += () => window.DispatcherQueue.TryEnqueue(window.RequestQuit);
            window.HiddenToTray += () => OnHiddenToTray(window, settings);
            window.ShownFromHidden += () => SyncTrayVisibility(settings, settings.GetKeepRunningInBackground());
            window.BackgroundSettingChanged += enabled => SyncTrayVisibility(settings, enabled);
            if (forceVisible)
            {
                try
                {
                    _tray.Show(ResolveAppIconPath(), "邮件");
                }
                catch (Exception ex)
                {
                    WriteStartupBreadcrumb("tray:show-fail " + ex.Message);
                }
            }
            else
            {
                SyncTrayVisibility(settings, settings.GetKeepRunningInBackground());
            }
            WriteStartupBreadcrumb("tray:installed");
        }
        catch (Exception ex)
        {
            WriteStartupBreadcrumb("tray:install-fail " + ex.GetType().Name + ": " + ex.Message);
            try { _tray?.Dispose(); } catch { }
            _tray = null;
        }
    }

    private void SyncTrayVisibility(AppSettingsStore settings, bool enabled)
    {
        if (_tray is null)
        {
            return;
        }

        if (!enabled)
        {
            _tray.Hide();
            return;
        }

        try
        {
            _tray.Show(ResolveAppIconPath(), "邮件");
        }
        catch (Exception ex)
        {
            WriteStartupBreadcrumb("tray:show-fail " + ex.Message);
        }
    }

    private void OnHiddenToTray(MainWindow window, AppSettingsStore settings)
    {
        SyncTrayVisibility(settings, settings.GetKeepRunningInBackground());
        if (settings.Snapshot().TrayHintShown)
        {
            return;
        }

        settings.Update(s => s.TrayHintShown = true);
        try
        {
            _notifications?.ShowTrayHint();
        }
        catch
        {
            // 可选提示失败无所谓
        }

        try
        {
            if (_shell is { } shell)
            {
                // 窗口已隐藏，状态条用户看不到；仍写一下便于日志
                shell.StatusMessage = "邮件仍在后台运行，点托盘图标可打开。";
            }
        }
        catch { }
    }

    private static string? ResolveAppIconPath()
    {
        try
        {
            var packaged = Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico");
            if (File.Exists(packaged))
            {
                return packaged;
            }
        }
        catch { }

        try
        {
            var loc = Windows.ApplicationModel.Package.Current.InstalledLocation.Path;
            var p = Path.Combine(loc, "Assets", "AppIcon.ico");
            if (File.Exists(p))
            {
                return p;
            }
        }
        catch { }

        return null;
    }

    private async Task ShutdownServicesAsync()
    {
        if (_exiting)
        {
            return;
        }

        _exiting = true;
        try { _tray?.Dispose(); } catch { }
        _tray = null;
        try { _storageMaintenance?.Dispose(); } catch { }
        try { _updates?.Dispose(); } catch { }
        if (_outboxWorker is { } outbox)
        {
            _outboxWorker = null;
            try { await outbox.DisposeAsync(); } catch { }
        }
        if (_pendingOpsWorker is { } pending)
        {
            _pendingOpsWorker = null;
            try { await pending.DisposeAsync(); } catch { }
        }
        _notifications?.Unregister();
        if (_syncCoordinator is { } coordinator)
        {
            _syncCoordinator = null;
            try { await coordinator.DisposeAsync(); } catch { }
        }
    }
}