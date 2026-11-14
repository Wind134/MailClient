namespace MailClient.Core.Tray;

/// <summary>
/// 开机启动的纯逻辑：静默参数、注册表命令行、任务管理器「启动应用」禁用标记。
/// 真正写注册表 / StartupTask 的代码在 App 与安装程序里，这里不碰系统。
/// </summary>
public static class StartupLaunch
{
    /// <summary>登录后启动时附带的参数：不显示主窗口，只在托盘后台跑。</summary>
    public const string SilentArgument = "--silent";

    /// <summary>
    /// HKCU Run 与任务管理器里的值名。必须和安装程序
    /// <c>SetupConstants.StartupRunValueName</c> 一致，卸载时才能清掉。
    /// </summary>
    public const string RunValueName = "MailClient";

    /// <summary>任务管理器把启动项标成禁用时，StartupApproved 二进制值的首字节。</summary>
    public const byte ApprovedDisabled = 3;

    /// <summary>启动项启用时的首字节。后面 11 个字节保持 0。</summary>
    public const byte ApprovedEnabled = 2;

    public static bool HasSilentArgument(IEnumerable<string>? args)
    {
        if (args is null)
        {
            return false;
        }

        foreach (var arg in args)
        {
            if (IsSilentToken(arg))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Windows 激活参数或 Run 项里的整段命令行。</summary>
    public static bool HasSilentArgument(string? commandLine)
    {
        if (string.IsNullOrWhiteSpace(commandLine))
        {
            return false;
        }

        return commandLine
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .Any(IsSilentToken);
    }

    public static string BuildCommand(string exePath)
    {
        if (string.IsNullOrWhiteSpace(exePath))
        {
            throw new ArgumentException("需要可执行文件路径。", nameof(exePath));
        }

        if (exePath.Contains('"'))
        {
            throw new ArgumentException("路径里不能有引号。", nameof(exePath));
        }

        return $"\"{exePath.Trim()}\" {SilentArgument}";
    }

    public static bool CommandMatches(string? registered, string exePath)
    {
        if (string.IsNullOrWhiteSpace(registered) || string.IsNullOrWhiteSpace(exePath))
        {
            return false;
        }

        try
        {
            return string.Equals(registered.Trim(), BuildCommand(exePath), StringComparison.OrdinalIgnoreCase);
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    /// <summary>没有这项，或首字节不是 3，都算「没有被任务管理器关掉」。</summary>
    public static bool IsApprovedDisabled(byte[]? blob)
        => blob is { Length: > 0 } && blob[0] == ApprovedDisabled;

    public static byte[] ApprovedEnabledBlob()
        => new byte[] { ApprovedEnabled, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 };

    private static bool IsSilentToken(string? arg)
        => string.Equals(arg?.Trim(), SilentArgument, StringComparison.OrdinalIgnoreCase);
}
