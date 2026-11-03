namespace MailClient.Setup.Core;

public static class SetupPaths
{
    public static string LocalAppData => Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

    /// <summary>默认安装位置：每用户，不需要管理员。</summary>
    public static string DefaultInstallDir => Path.Combine(LocalAppData, "Programs", "MailClient");

    /// <summary>应用的用户数据（邮件缓存、设置、凭据）。MSIX 版与 exe 版共用这个位置，所以升级 / 切换都不会丢数据。</summary>
    public static string UserDataDir => Path.Combine(LocalAppData, "MailClient");

    public static string StartMenuShortcut => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), SetupConstants.ProductName + ".lnk");

    public static string DesktopShortcut => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), SetupConstants.ProductName + ".lnk");

    /// <summary>校验用户选的安装目录；返回 null 表示可以用，否则是给用户看的中文原因。</summary>
    public static string? ValidateInstallDir(string? dir)
    {
        if (string.IsNullOrWhiteSpace(dir))
        {
            return "请选择安装位置。";
        }

        string full;
        try
        {
            if (!Path.IsPathRooted(dir) || dir.StartsWith(@"\\", StringComparison.Ordinal))
            {
                return "请输入本地磁盘上的完整路径，例如 D:\\Apps\\MailClient。";
            }

            full = Path.GetFullPath(dir).TrimEnd('\\', '/');
        }
        catch (Exception)
        {
            return "这个路径里有无法使用的字符。";
        }

        var root = Path.GetPathRoot(full)?.TrimEnd('\\', '/') ?? string.Empty;
        if (string.Equals(full, root, StringComparison.OrdinalIgnoreCase))
        {
            return "不能直接安装到磁盘根目录，请选一个文件夹。";
        }

        if (full.Length > 200)
        {
            return "路径太长了，请换一个短一点的位置。";
        }

        if (IsSameOrInside(UserDataDir, full) || IsSameOrInside(full, UserDataDir))
        {
            return "不能安装到邮件数据文件夹里或它的上层，请换一个位置。";
        }

        foreach (var special in new[] { Environment.SpecialFolder.Windows, Environment.SpecialFolder.System })
        {
            var s = Environment.GetFolderPath(special);
            if (!string.IsNullOrEmpty(s) && IsSameOrInside(full, s))
            {
                return "不能安装到系统文件夹里。";
            }
        }

        return null;
    }

    /// <summary>path 是 parent 本身或在它下面。</summary>
    public static bool IsSameOrInside(string path, string parent)
    {
        var p = Path.GetFullPath(path).TrimEnd('\\', '/');
        var q = Path.GetFullPath(parent).TrimEnd('\\', '/');
        return string.Equals(p, q, StringComparison.OrdinalIgnoreCase)
            || p.StartsWith(q + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }
}
