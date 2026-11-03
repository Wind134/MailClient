using System.Diagnostics;

namespace MailClient.Setup.Core;

/// <summary>找到并结束安装目录里正在运行的「邮件」（含托盘后台）。先礼貌地让它自己退出，不行再强制结束。</summary>
public static class AppProcess
{
    public static List<Process> FindRunning(string installDir)
    {
        var result = new List<Process>();
        var exeName = Path.GetFileNameWithoutExtension(SetupConstants.AppExeName);
        foreach (var p in Process.GetProcessesByName(exeName))
        {
            try
            {
                var file = p.MainModule?.FileName;
                if (file is not null && SetupPaths.IsSameOrInside(file, installDir))
                {
                    result.Add(p);
                    continue;
                }
            }
            catch (Exception)
            {
                // 无权读取（例如别的用户的进程）：不是我们要管的
            }

            p.Dispose();
        }

        return result;
    }

    /// <summary>返回是否有进程被强制结束。</summary>
    public static bool StopRunning(string installDir, TimeSpan gracePeriod)
    {
        var running = FindRunning(installDir);
        if (running.Count == 0)
        {
            return false;
        }

        var forced = false;
        try
        {
            // 让应用走自己的正常退出流程（保存状态、释放托盘图标）
            var exe = Path.Combine(installDir, SetupConstants.AppExeName);
            if (File.Exists(exe))
            {
                using var quit = Process.Start(new ProcessStartInfo(exe, SetupConstants.QuitArgument)
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                });
                quit?.WaitForExit(5000);
            }
        }
        catch (Exception)
        {
            // 退回到强制结束
        }

        var deadline = DateTime.UtcNow + gracePeriod;
        foreach (var p in running)
        {
            try
            {
                var remaining = deadline - DateTime.UtcNow;
                if (remaining > TimeSpan.Zero && p.WaitForExit(remaining))
                {
                    continue;
                }

                p.Kill(entireProcessTree: true);
                p.WaitForExit(5000);
                forced = true;
            }
            catch (Exception)
            {
                // 已经退出了
            }
            finally
            {
                p.Dispose();
            }
        }

        return forced;
    }
}
