using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;
using System.Runtime.InteropServices;
using WinRT;

namespace MailClient_App;

/// <summary>
/// 自定义入口：在创建窗口之前做单实例登记。第二次启动时把激活重定向到已在跑的进程（含托盘后台）。
/// 命令行 <c>--quit</c>：让正在运行的实例正常退出（安装 / 卸载程序升级前使用），没有实例在跑就什么也不做。
/// </summary>
public static class Program
{
    public const string InstanceKey = "MailClient.Main";
    public const string QuitArgument = "--quit";

    [STAThread]
    public static void Main(string[] args)
    {
        ComWrappersSupport.InitializeComWrappers();
        if (!AppIdentity.IsPackaged)
        {
            // 免打包：先设进程 AUMID（通知 / 任务栏分组），再做单实例登记
            AppIdentity.PrepareUnpackaged();
        }

        var quitOnly = args.Any(a => string.Equals(a, QuitArgument, StringComparison.OrdinalIgnoreCase));
        if (DecideRedirection())
        {
            return;
        }

        if (quitOnly)
        {
            return; // 没有已在运行的实例，不需要退出任何东西，也不要自己开窗
        }

        Application.Start(p =>
        {
            var context = new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread());
            SynchronizationContext.SetSynchronizationContext(context);
            new App();
        });
    }

    /// <summary>若当前进程不是主实例，重定向激活并返回 true（调用方应退出）。</summary>
    private static bool DecideRedirection()
    {
        var main = AppInstance.FindOrRegisterForKey(InstanceKey);
        if (main.IsCurrent)
        {
            main.Activated += OnActivated;
            return false;
        }

        try
        {
            var args = AppInstance.GetCurrent().GetActivatedEventArgs();
            RedirectActivationTo(main, args);
        }
        catch
        {
            // 重定向失败时仍退出本进程，避免双开
        }

        return true;
    }

    private static void OnActivated(object? sender, AppActivationArguments e)
    {
        if (Application.Current is App app)
        {
            app.OnRedirectedActivation(e);
        }
    }

    /// <summary>在后台线程做 RedirectActivationToAsync，避免 STA 上同步 Wait 死锁。</summary>
    private static void RedirectActivationTo(AppInstance keyInstance, AppActivationArguments args)
    {
        var done = new ManualResetEventSlim(false);
        _ = Task.Run(async () =>
        {
            try
            {
                await keyInstance.RedirectActivationToAsync(args).AsTask().ConfigureAwait(false);
            }
            catch
            {
                // ignore
            }
            finally
            {
                done.Set();
            }
        });
        done.Wait(TimeSpan.FromSeconds(10));
    }
}
