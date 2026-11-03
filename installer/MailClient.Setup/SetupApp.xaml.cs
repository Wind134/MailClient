using MailClient.Setup.Core;
using Microsoft.UI.Xaml;

namespace MailClient.Setup;

public partial class SetupApp : Application
{
    private SetupWindow? _window;

    public SetupApp()
    {
        Arguments = SetupArguments.Parse(Environment.GetCommandLineArgs().Skip(1));
        if (string.Equals(Arguments.Theme, "dark", StringComparison.OrdinalIgnoreCase))
        {
            RequestedTheme = ApplicationTheme.Dark;
        }
        else if (string.Equals(Arguments.Theme, "light", StringComparison.OrdinalIgnoreCase))
        {
            RequestedTheme = ApplicationTheme.Light;
        }

        InitializeComponent();
    }

    public static SetupArguments Arguments { get; private set; } = new();

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        if (Arguments.Silent && Arguments.Preview is null)
        {
            RunSilent();
            return;
        }

        _window = new SetupWindow(Arguments);
        _window.Activate();
    }

    /// <summary>/S：不显示界面，直接安装或卸载，退出码 0 成功，1 失败。</summary>
    private static void RunSilent()
    {
        var code = 0;
        try
        {
            if (Arguments.Mode == SetupMode.Uninstall)
            {
                var dir = AppRegistration.ReadInstalled()?.InstallDir ?? AppContext.BaseDirectory;
                SetupEngine.Uninstall(new UninstallRequest { InstallDir = dir, DeleteUserData = Arguments.DeleteUserData });
            }
            else
            {
                var payload = AppContext.BaseDirectory;
                var dir = Arguments.InstallDir ?? AppRegistration.ReadInstalled()?.InstallDir ?? SetupPaths.DefaultInstallDir;
                SetupEngine.Install(new InstallRequest
                {
                    PayloadDir = payload,
                    InstallDir = dir,
                    Version = SetupVersion.Current,
                    DesktopShortcut = Arguments.DesktopShortcut,
                });
                if (Arguments.Run)
                {
                    SetupLauncher.LaunchApp(dir);
                }
            }
        }
        catch (Exception)
        {
            code = 1;
        }

        Environment.Exit(code);
    }
}
