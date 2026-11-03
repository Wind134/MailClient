using System.Diagnostics;
using MailClient.Setup.Core;

namespace MailClient.Setup;

internal static class SetupVersion
{
    /// <summary>取应用本体 exe 的文件版本（安装包里和已安装目录里都有 MailClient.App.exe）；取不到再用安装程序自己的版本。</summary>
    public static string Current { get; } = Resolve();

    private static string Resolve()
    {
        foreach (var name in new[] { SetupConstants.AppExeName, SetupConstants.SetupExeName })
        {
            try
            {
                var path = Path.Combine(AppContext.BaseDirectory, name);
                if (File.Exists(path))
                {
                    var v = FileVersionInfo.GetVersionInfo(path).FileVersion;
                    if (!string.IsNullOrWhiteSpace(v))
                    {
                        return v;
                    }
                }
            }
            catch (Exception)
            {
                // 试下一个
            }
        }

        return "1.0.0.0";
    }
}

internal static class SetupLauncher
{
    public static void LaunchApp(string installDir)
    {
        var exe = Path.Combine(installDir, SetupConstants.AppExeName);
        if (File.Exists(exe))
        {
            Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true, WorkingDirectory = installDir });
        }
    }
}
