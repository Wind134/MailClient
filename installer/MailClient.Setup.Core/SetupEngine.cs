using System.Diagnostics;
using System.Text;

namespace MailClient.Setup.Core;

public sealed record SetupProgress(double Fraction, string Message, string? Detail = null);

public sealed class SetupException : Exception
{
    public SetupException(string message, Exception? inner = null)
        : base(message, inner)
    {
    }
}

public sealed class InstallRequest
{
    /// <summary>要安装的文件所在目录（安装程序自己的目录：应用本体 + 安装 / 卸载程序）。</summary>
    public required string PayloadDir { get; init; }

    public required string InstallDir { get; init; }
    public required string Version { get; init; }
    public bool DesktopShortcut { get; init; } = true;
}

public sealed class UninstallRequest
{
    public required string InstallDir { get; init; }
    public bool DeleteUserData { get; init; }
}

public sealed record UninstallResult(bool UserDataDeleted, string? UserDataError, int PendingFiles);

public static class SetupEngine
{
    /// <summary>安装 / 升级。失败抛 <see cref="SetupException"/>（消息是给用户看的中文）；全新安装失败时会撤回已复制的文件。</summary>
    public static void Install(InstallRequest request, IProgress<SetupProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        var problem = SetupPaths.ValidateInstallDir(request.InstallDir);
        if (problem is not null)
        {
            throw new SetupException(problem);
        }

        var installDir = Path.GetFullPath(request.InstallDir).TrimEnd('\\', '/');
        var payloadDir = Path.GetFullPath(request.PayloadDir).TrimEnd('\\', '/');
        if (SetupPaths.IsSameOrInside(installDir, payloadDir) || SetupPaths.IsSameOrInside(payloadDir, installDir))
        {
            throw new SetupException("安装位置不能和安装程序所在的文件夹重叠，请换一个位置。");
        }

        var files = EnumeratePayload(payloadDir);
        if (!files.Any(f => string.Equals(f.Relative, SetupConstants.AppExeName, StringComparison.OrdinalIgnoreCase)))
        {
            throw new SetupException("安装包不完整（找不到 " + SetupConstants.AppExeName + "），请重新下载。");
        }

        var totalBytes = files.Sum(f => f.Length);
        CheckFreeSpace(installDir, totalBytes);

        var previous = InstallManifest.Read(installDir);
        var isFresh = previous.Count == 0 && !File.Exists(Path.Combine(installDir, SetupConstants.AppExeName));
        var copied = new List<string>();

        try
        {
            progress?.Report(new SetupProgress(0.01, "正在准备…", isFresh ? null : "检测到旧版本，将在原位置升级"));
            if (AppProcess.StopRunning(installDir, TimeSpan.FromSeconds(8)))
            {
                progress?.Report(new SetupProgress(0.04, "已关闭正在运行的邮件"));
            }

            try
            {
                Directory.CreateDirectory(installDir);
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
            {
                throw new SetupException("无法在这个位置创建文件夹（可能没有写入权限），请换一个位置。", ex);
            }

            long doneBytes = 0;
            var index = 0;
            foreach (var file in files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                index++;
                var dest = Path.Combine(installDir, file.Relative);
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                CopyWithRetry(file.FullPath, dest);
                copied.Add(dest);
                doneBytes += file.Length;
                var fraction = 0.05 + (0.85 * (totalBytes == 0 ? 1 : (double)doneBytes / totalBytes));
                progress?.Report(new SetupProgress(fraction, $"正在复制文件（{index}/{files.Count}）", file.Relative));
            }

            progress?.Report(new SetupProgress(0.91, "正在清理旧版本文件…"));
            var keep = new HashSet<string>(files.Select(f => f.Relative), StringComparer.OrdinalIgnoreCase);
            foreach (var stale in previous.Where(p => !keep.Contains(p)))
            {
                TryDeleteFile(Path.Combine(installDir, stale));
            }

            InstallManifest.Write(installDir, files.Select(f => f.Relative));

            progress?.Report(new SetupProgress(0.94, "正在创建快捷方式…"));
            CreateShortcuts(installDir, request.DesktopShortcut);

            progress?.Report(new SetupProgress(0.97, "正在完成注册…"));
            AppRegistration.WriteUninstallEntry(installDir, request.Version, totalBytes);
            AppRegistration.WriteAumid(installDir);

            progress?.Report(new SetupProgress(1.0, "安装完成"));
        }
        catch (Exception)
        {
            if (isFresh)
            {
                Rollback(installDir, copied);
            }

            throw;
        }
    }

    /// <summary>卸载。默认保留 %LocalAppData%\MailClient 里的邮件数据和设置。</summary>
    public static UninstallResult Uninstall(UninstallRequest request, IProgress<SetupProgress>? progress = null)
    {
        var installDir = Path.GetFullPath(request.InstallDir).TrimEnd('\\', '/');

        progress?.Report(new SetupProgress(0.03, "正在关闭正在运行的邮件…"));
        AppProcess.StopRunning(installDir, TimeSpan.FromSeconds(8));

        progress?.Report(new SetupProgress(0.15, "正在移除快捷方式…"));
        TryDeleteFile(SetupPaths.StartMenuShortcut);
        TryDeleteFile(SetupPaths.DesktopShortcut);

        progress?.Report(new SetupProgress(0.25, "正在移除注册信息…"));
        AppRegistration.RemoveAll();

        var relatives = InstallManifest.Read(installDir).ToList();
        if (relatives.Count == 0 && Directory.Exists(installDir))
        {
            // 没有清单（旧安装损坏）：只在默认目录里整个清理，其它位置只删我们自己的两个 exe，避免误删用户文件
            relatives = string.Equals(installDir, SetupPaths.DefaultInstallDir.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)
                ? Directory.EnumerateFiles(installDir, "*", SearchOption.AllDirectories).Select(f => Path.GetRelativePath(installDir, f)).ToList()
                : new List<string> { SetupConstants.AppExeName, SetupConstants.SetupExeName };
        }

        // 卸载程序若就是从安装目录里跑起来的，它自己的 exe / dll / 界面资源（.pri .xbf）现在不能删：
        // 删了界面会立刻崩溃。这些文件留给最后的收尾批处理，等本进程退出后再删。
        var selfFiles = ReadRunningSetupFiles(installDir);
        relatives.Add(SetupConstants.ManifestFileName);
        var pending = new List<string>();
        for (var i = 0; i < relatives.Count; i++)
        {
            var path = Path.Combine(installDir, relatives[i]);
            if (selfFiles.Contains(relatives[i]))
            {
                if (File.Exists(path))
                {
                    pending.Add(path);
                }
            }
            else if (!TryDeleteFile(path) && File.Exists(path))
            {
                pending.Add(path);
            }

            if (i % 20 == 0 || i == relatives.Count - 1)
            {
                progress?.Report(new SetupProgress(0.3 + (0.55 * (i + 1) / relatives.Count), "正在删除程序文件…", relatives[i]));
            }
        }

        // 空文件夹由深到浅删除
        var dirs = relatives
            .Select(r => Path.GetDirectoryName(Path.Combine(installDir, r)))
            .Where(d => !string.IsNullOrEmpty(d))
            .SelectMany(d => ParentChain(d!, installDir))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(d => d.Length)
            .ToList();
        dirs.Add(installDir);
        var pendingDirs = new List<string>();
        foreach (var dir in dirs.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (Directory.Exists(dir) && !TryDeleteEmptyDir(dir))
            {
                pendingDirs.Add(dir);
            }
        }

        var userDataDeleted = false;
        string? userDataError = null;
        if (request.DeleteUserData)
        {
            progress?.Report(new SetupProgress(0.9, "正在删除邮件数据和设置…"));
            try
            {
                if (Directory.Exists(SetupPaths.UserDataDir))
                {
                    Directory.Delete(SetupPaths.UserDataDir, recursive: true);
                }

                userDataDeleted = true;
            }
            catch (Exception ex)
            {
                userDataError = ex.Message;
            }
        }

        if (pending.Count > 0 || pendingDirs.Count > 0)
        {
            ScheduleLeftoverCleanup(pending, pendingDirs);
        }

        progress?.Report(new SetupProgress(1.0, "卸载完成"));
        return new UninstallResult(userDataDeleted, userDataError, pending.Count);
    }

    private static HashSet<string> ReadRunningSetupFiles(string installDir)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var self = Environment.ProcessPath;
            if (string.IsNullOrEmpty(self) || !SetupPaths.IsSameOrInside(self, installDir))
            {
                return set;
            }

            var list = Path.Combine(installDir, SetupConstants.SetupFilesListName);
            if (File.Exists(list))
            {
                foreach (var line in File.ReadAllLines(list))
                {
                    var rel = line.Trim();
                    if (rel.Length > 0)
                    {
                        set.Add(rel.Replace('/', '\\'));
                    }
                }
            }
            else
            {
                // 没有清单：至少保住自己
                set.Add(SetupConstants.SetupExeName);
            }
        }
        catch (Exception)
        {
            // 读不到就按普通方式处理
        }

        return set;
    }

    public static void CreateShortcuts(string installDir, bool desktop)
    {
        var exe = Path.Combine(installDir, SetupConstants.AppExeName);
        ShellLink.Create(SetupPaths.StartMenuShortcut, exe, installDir, SetupConstants.ProductName, exe, SetupConstants.Aumid);
        if (desktop)
        {
            ShellLink.Create(SetupPaths.DesktopShortcut, exe, installDir, SetupConstants.ProductName, exe, SetupConstants.Aumid);
        }
        else
        {
            TryDeleteFile(SetupPaths.DesktopShortcut);
        }
    }

    public sealed record PayloadFile(string FullPath, string Relative, long Length);

    public static List<PayloadFile> EnumeratePayload(string payloadDir)
    {
        var root = Path.GetFullPath(payloadDir).TrimEnd('\\', '/');
        return Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Select(f => new PayloadFile(f, Path.GetRelativePath(root, f), new FileInfo(f).Length))
            .Where(f => !string.Equals(f.Relative, SetupConstants.ManifestFileName, StringComparison.OrdinalIgnoreCase))
            .OrderBy(f => f.Relative, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static IEnumerable<string> ParentChain(string dir, string stopAt)
    {
        var current = dir;
        while (!string.IsNullOrEmpty(current) && SetupPaths.IsSameOrInside(current, stopAt)
            && !string.Equals(current.TrimEnd('\\'), stopAt, StringComparison.OrdinalIgnoreCase))
        {
            yield return current;
            current = Path.GetDirectoryName(current);
        }
    }

    private static void CheckFreeSpace(string installDir, long needBytes)
    {
        try
        {
            var root = Path.GetPathRoot(installDir);
            if (string.IsNullOrEmpty(root))
            {
                return;
            }

            var free = new DriveInfo(root).AvailableFreeSpace;
            if (free < (needBytes * 11 / 10) + (50L * 1024 * 1024))
            {
                throw new SetupException($"磁盘剩余空间不足：需要约 {(needBytes / 1024 / 1024) + 50} MB，当前只剩 {free / 1024 / 1024} MB。请清理空间或换一个磁盘。");
            }
        }
        catch (SetupException)
        {
            throw;
        }
        catch (Exception)
        {
            // 取不到空间信息就不拦
        }
    }

    private static void CopyWithRetry(string source, string dest)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                if (File.Exists(dest))
                {
                    File.SetAttributes(dest, FileAttributes.Normal);
                }

                File.Copy(source, dest, overwrite: true);
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (attempt >= 8)
                {
                    throw new SetupException($"无法写入文件 {Path.GetFileName(dest)}，它可能正被其它程序占用。请先退出「邮件」后重试。", ex);
                }

                Thread.Sleep(400);
            }
        }
    }

    private static void Rollback(string installDir, List<string> copied)
    {
        foreach (var file in copied)
        {
            TryDeleteFile(file);
        }

        TryDeleteFile(Path.Combine(installDir, SetupConstants.ManifestFileName));
        foreach (var dir in copied.Select(Path.GetDirectoryName).Where(d => d is not null).Distinct().OrderByDescending(d => d!.Length))
        {
            TryDeleteEmptyDir(dir!);
        }

        TryDeleteEmptyDir(installDir);
    }

    internal static bool TryDeleteFile(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return true;
            }

            File.SetAttributes(path, FileAttributes.Normal);
            File.Delete(path);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    internal static bool TryDeleteEmptyDir(string path)
    {
        try
        {
            if (Directory.Exists(path) && !Directory.EnumerateFileSystemEntries(path).Any())
            {
                Directory.Delete(path);
            }

            return !Directory.Exists(path);
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>卸载程序自己（以及它加载着的 dll）在运行时删不掉：交给一个临时批处理，等本进程退出后再删，最后连批处理自己一起删。</summary>
    private static void ScheduleLeftoverCleanup(List<string> files, List<string> dirs)
    {
        try
        {
            var sb = new StringBuilder();
            sb.AppendLine("@echo off");
            sb.AppendLine("chcp 65001 >nul");
            sb.AppendLine("set n=0");
            var probe = files.FirstOrDefault(f => f.EndsWith(SetupConstants.SetupExeName, StringComparison.OrdinalIgnoreCase)) ?? files.FirstOrDefault();
            if (probe is not null)
            {
                sb.AppendLine(":wait");
                sb.AppendLine("set /a n+=1");
                sb.AppendLine($"del /f /q \"{probe}\" >nul 2>&1");
                sb.AppendLine($"if exist \"{probe}\" if %n% LSS 60 (ping -n 2 127.0.0.1 >nul & goto wait)");
            }

            foreach (var f in files)
            {
                sb.AppendLine($"del /f /q \"{f}\" >nul 2>&1");
            }

            foreach (var d in dirs.OrderByDescending(d => d.Length))
            {
                sb.AppendLine($"rmdir \"{d}\" >nul 2>&1");
            }

            sb.AppendLine("(goto) 2>nul & del \"%~f0\"");

            var script = Path.Combine(Path.GetTempPath(), "mailclient-uninstall-" + Guid.NewGuid().ToString("N") + ".cmd");
            File.WriteAllText(script, sb.ToString(), new UTF8Encoding(false));
            Process.Start(new ProcessStartInfo("cmd.exe", $"/c \"\"{script}\"\"")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
            });
        }
        catch (Exception)
        {
            // 残留几个文件而已
        }
    }
}
