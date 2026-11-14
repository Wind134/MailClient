using System.Diagnostics;
using Windows.System;

namespace MailClient_App;

/// <summary>
/// 打开 Windows 设置页。不要用 <c>HyperlinkButton.NavigateUri</c>：
/// WinUI 3 对 <c>ms-settings:</c> 会静默失败，点击没有任何反应
/// （microsoft-ui-xaml#5630）。<see cref="Launcher.LaunchUriAsync"/> 才打得开。
/// </summary>
internal static class WindowsSettings
{
    public static async Task<bool> OpenAsync(string page)
    {
        if (string.IsNullOrWhiteSpace(page))
        {
            return false;
        }

        var uri = page.StartsWith("ms-settings:", StringComparison.OrdinalIgnoreCase)
            ? new Uri(page)
            : new Uri("ms-settings:" + page.Trim());

        try
        {
            if (await Launcher.LaunchUriAsync(uri))
            {
                return true;
            }
        }
        catch
        {
            // 免打包进程里 Launcher 偶尔直接拒绝，下面再用 ShellExecute 试一次。
        }

        try
        {
            Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
            return true;
        }
        catch
        {
            return false;
        }
    }
}
