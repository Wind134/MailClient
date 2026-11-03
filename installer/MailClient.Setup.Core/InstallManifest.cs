namespace MailClient.Setup.Core;

/// <summary>
/// 安装时写在安装目录里的文件清单（相对路径，每行一个）。升级时用它清理旧版本多出来的文件，卸载时只删清单里的文件，
/// 所以即使用户把程序装进一个本来就有别的东西的文件夹，卸载也不会误删。
/// </summary>
public static class InstallManifest
{
    public static IReadOnlyList<string> Read(string installDir)
    {
        var path = Path.Combine(installDir, SetupConstants.ManifestFileName);
        if (!File.Exists(path))
        {
            return Array.Empty<string>();
        }

        try
        {
            return File.ReadAllLines(path)
                .Select(l => l.Trim())
                .Where(IsSafeRelative)
                .ToList();
        }
        catch (Exception)
        {
            return Array.Empty<string>();
        }
    }

    public static void Write(string installDir, IEnumerable<string> relativePaths)
    {
        var path = Path.Combine(installDir, SetupConstants.ManifestFileName);
        File.WriteAllLines(path, relativePaths.OrderBy(p => p, StringComparer.OrdinalIgnoreCase));
    }

    /// <summary>清单里只允许安装目录内的相对路径（防止被篡改后删到目录外）。</summary>
    public static bool IsSafeRelative(string line)
    {
        if (string.IsNullOrWhiteSpace(line) || Path.IsPathRooted(line))
        {
            return false;
        }

        return !line.Replace('/', '\\').Split('\\').Any(part => part == "..");
    }
}
