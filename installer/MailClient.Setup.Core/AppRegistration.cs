using Microsoft.Win32;

namespace MailClient.Setup.Core;

/// <summary>HKCU 下的注册：Apps &amp; features 的卸载项，以及 Windows 通知用的 AppUserModelId。全部是每用户，不需要管理员。</summary>
public static class AppRegistration
{
    public const string UninstallPath = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\" + SetupConstants.UninstallKeyName;
    private const string AumidPath = @"Software\Classes\AppUserModelId\" + SetupConstants.Aumid;

    public sealed record InstalledInfo(string Version, string InstallDir);

    public static InstalledInfo? ReadInstalled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(UninstallPath);
            var dir = key?.GetValue("InstallLocation") as string;
            if (key is null || string.IsNullOrWhiteSpace(dir))
            {
                return null;
            }

            return new InstalledInfo(key.GetValue("DisplayVersion") as string ?? string.Empty, dir);
        }
        catch (Exception)
        {
            return null;
        }
    }

    public static void WriteUninstallEntry(string installDir, string version, long sizeBytes)
    {
        var setupExe = Path.Combine(installDir, SetupConstants.SetupExeName);
        using var key = Registry.CurrentUser.CreateSubKey(UninstallPath)
            ?? throw new InvalidOperationException("无法写入卸载信息。");
        key.SetValue("DisplayName", SetupConstants.ProductName);
        key.SetValue("DisplayVersion", version);
        key.SetValue("Publisher", SetupConstants.Publisher);
        key.SetValue("InstallLocation", installDir);
        key.SetValue("DisplayIcon", Path.Combine(installDir, SetupConstants.AppExeName) + ",0");
        key.SetValue("UninstallString", $"\"{setupExe}\" /uninstall");
        key.SetValue("QuietUninstallString", $"\"{setupExe}\" /uninstall /S");
        key.SetValue("InstallDate", DateTime.Now.ToString("yyyyMMdd"));
        key.SetValue("EstimatedSize", (int)Math.Min(int.MaxValue, sizeBytes / 1024), RegistryValueKind.DWord);
        key.SetValue("NoModify", 1, RegistryValueKind.DWord);
        key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
    }

    public static void WriteAumid(string installDir)
    {
        using var key = Registry.CurrentUser.CreateSubKey(AumidPath)
            ?? throw new InvalidOperationException("无法写入通知注册信息。");
        key.SetValue("DisplayName", SetupConstants.ProductName);
        var icon = Path.Combine(installDir, "Assets", "Square44x44Logo.scale-200.png");
        if (File.Exists(icon))
        {
            key.SetValue("IconUri", icon);
        }

        key.SetValue("IconBackgroundColor", "0");
    }

    /// <summary>卸载：删掉卸载项、AUMID 以及 Windows 应用 SDK 为通知登记的 COM 激活项。</summary>
    public static void RemoveAll()
    {
        // AUMID 下的 CustomActivator 指向通知激活用的 CLSID，一并清掉，否则注册表里会残留指向已删除 exe 的 LocalServer32
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(AumidPath);
            var clsid = key?.GetValue("CustomActivator") as string;
            if (!string.IsNullOrWhiteSpace(clsid) && Guid.TryParse(clsid.Trim('{', '}'), out var g))
            {
                Registry.CurrentUser.DeleteSubKeyTree(@"Software\Classes\CLSID\{" + g.ToString().ToUpperInvariant() + "}", false);
                Registry.CurrentUser.DeleteSubKeyTree(@"Software\Classes\CLSID\{" + g.ToString() + "}", false);
            }
        }
        catch (Exception)
        {
            // 清理尽力而为
        }

        TryDeleteTree(AumidPath);
        TryDeleteTree(UninstallPath);
    }

    private static void TryDeleteTree(string path)
    {
        try
        {
            Registry.CurrentUser.DeleteSubKeyTree(path, false);
        }
        catch (Exception)
        {
            // 清理尽力而为
        }
    }

    /// <summary>检测是否装着 MSIX 版（侧载）。返回版本号或 null。</summary>
    public static string? FindMsixVersion()
    {
        try
        {
            using var repo = Registry.CurrentUser.OpenSubKey(@"Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\Repository\Packages");
            if (repo is null)
            {
                return null;
            }

            foreach (var name in repo.GetSubKeyNames())
            {
                if (name.StartsWith(SetupConstants.MsixPackageNamePrefix, StringComparison.OrdinalIgnoreCase))
                {
                    var parts = name.Split('_');
                    return parts.Length > 1 ? parts[1] : string.Empty;
                }
            }
        }
        catch (Exception)
        {
            // ignore
        }

        return null;
    }
}
