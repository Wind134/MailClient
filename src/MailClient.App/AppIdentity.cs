using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;

namespace MailClient_App;

/// <summary>
/// 区分“MSIX 打包运行”和“免打包运行（exe 安装包 / 绿色版）”，并为免打包运行补上 Windows 通知需要的身份：
/// 进程级 AppUserModelID + HKCU 下的 AppUserModelId 注册（通知标题和图标从这里来）。
/// 安装程序也会写同样的注册；这里再做一次是为了让绿色版 zip 解压即用。
/// </summary>
internal static class AppIdentity
{
    /// <summary>免打包运行时的 AppUserModelID；开始菜单快捷方式、通知、任务栏分组都用它。必须和安装程序里的一致。</summary>
    public const string UnpackagedAumid = "MailClient.Desktop";

    private static bool? _packaged;

    public static bool IsPackaged => _packaged ??= DetectPackaged();

    /// <summary>应用目录下的资源文件（AppContext.BaseDirectory\Assets\…）。免打包时工作目录不可靠，所以一律用绝对路径。</summary>
    public static string AssetPath(string fileName) => Path.Combine(AppContext.BaseDirectory, "Assets", fileName);

    /// <summary>免打包启动的最早阶段调用：设置进程 AUMID，并确保通知身份已登记。</summary>
    public static void PrepareUnpackaged()
    {
        try
        {
            SetCurrentProcessExplicitAppUserModelID(UnpackagedAumid);
        }
        catch
        {
            // 失败只会影响任务栏分组 / 通知标题
        }

        try
        {
            EnsureAumidRegistration();
        }
        catch
        {
            // 同上
        }
    }

    /// <summary>登记通知身份。Windows 应用 SDK 的 AppNotificationManager.Register() 会把显示名改成进程名、图标改成它生成的占位图，所以 Register() 之后要再调用一次。</summary>
    public static void EnsureAumidRegistration()
    {
        using var key = Registry.CurrentUser.CreateSubKey(@"Software\Classes\AppUserModelId\" + UnpackagedAumid);
        if (key is null)
        {
            return;
        }

        if (!string.Equals(key.GetValue("DisplayName") as string, "邮件", StringComparison.Ordinal))
        {
            key.SetValue("DisplayName", "邮件", RegistryValueKind.String);
        }

        var icon = AssetPath("Square44x44Logo.scale-200.png");
        if (File.Exists(icon) && !string.Equals(key.GetValue("IconUri") as string, icon, StringComparison.OrdinalIgnoreCase))
        {
            key.SetValue("IconUri", icon, RegistryValueKind.String);
        }

        if (key.GetValue("IconBackgroundColor") is null)
        {
            key.SetValue("IconBackgroundColor", "0", RegistryValueKind.String);
        }
    }

    private static bool DetectPackaged()
    {
        try
        {
            // 没有包标识时返回 APPMODEL_ERROR_NO_PACKAGE (15700)
            var length = 0;
            return GetCurrentPackageFullName(ref length, null) != 15700;
        }
        catch
        {
            return false;
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetCurrentPackageFullName(ref int packageFullNameLength, StringBuilder? packageFullName);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SetCurrentProcessExplicitAppUserModelID([MarshalAs(UnmanagedType.LPWStr)] string appId);
}
