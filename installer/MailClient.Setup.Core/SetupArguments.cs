namespace MailClient.Setup.Core;

public enum SetupMode
{
    Install,
    Uninstall,
}

/// <summary>
/// 命令行：
///   MailClient-Setup.exe                      正常（带界面）安装
///   /S 或 /silent                            静默，不显示界面
///   /D=路径 或 /dir=路径                      安装目录（默认 %LocalAppData%\Programs\MailClient）
///   /nodesktop                               不创建桌面快捷方式（默认创建）
///   /run                                     安装完成后启动（静默模式默认不启动）
///   /uninstall                               卸载（Apps &amp; features 里的卸载命令用它）
///   /deletedata                              卸载时同时删除 %LocalAppData%\MailClient（默认保留）
///   /preview=welcome|progress|done|error|uninstall|uninstall-progress|uninstall-done   只预览界面，不做任何改动（开发用）
/// </summary>
public sealed class SetupArguments
{
    public SetupMode Mode { get; init; } = SetupMode.Install;
    public bool Silent { get; init; }
    public string? InstallDir { get; init; }
    public bool DesktopShortcut { get; init; } = true;
    public bool Run { get; init; }
    public bool DeleteUserData { get; init; }
    public string? Preview { get; init; }
    /// <summary>仅开发/截图用：/theme=dark|light，强制界面主题（默认跟随系统）。</summary>
    public string? Theme { get; init; }

    public static SetupArguments Parse(IEnumerable<string> args)
    {
        var mode = SetupMode.Install;
        var silent = false;
        string? dir = null;
        var desktop = true;
        var run = false;
        var runSpecified = false;
        var deleteData = false;
        string? preview = null;
        string? theme = null;

        var list = args.ToList();
        for (var i = 0; i < list.Count; i++)
        {
            var raw = list[i];
            if (string.IsNullOrWhiteSpace(raw))
            {
                continue;
            }

            var arg = raw.Trim();
            var name = arg.TrimStart('/', '-');
            string? value = null;
            var eq = name.IndexOf('=');
            if (eq >= 0)
            {
                value = name[(eq + 1)..].Trim().Trim('"');
                name = name[..eq];
            }

            switch (name.ToLowerInvariant())
            {
                case "uninstall":
                case "u":
                    mode = SetupMode.Uninstall;
                    break;
                case "s":
                case "silent":
                case "quiet":
                    silent = true;
                    break;
                case "d":
                case "dir":
                    // 同时支持 /D=路径 和 --dir 路径
                    if (value is null && i + 1 < list.Count && !list[i + 1].StartsWith('/'))
                    {
                        value = list[++i].Trim().Trim('"');
                    }

                    dir = string.IsNullOrWhiteSpace(value) ? null : value;
                    break;
                case "nodesktop":
                    desktop = false;
                    break;
                case "desktop":
                    desktop = true;
                    break;
                case "run":
                    run = true;
                    runSpecified = true;
                    break;
                case "norun":
                    run = false;
                    runSpecified = true;
                    break;
                case "deletedata":
                    deleteData = true;
                    break;
                case "preview":
                    preview = value;
                    break;
                case "theme":
                    theme = value;
                    break;
            }
        }

        _ = runSpecified;
        return new SetupArguments
        {
            Mode = mode,
            Silent = silent,
            InstallDir = dir,
            DesktopShortcut = desktop,
            Run = run,
            DeleteUserData = deleteData,
            Preview = preview,
            Theme = theme,
        };
    }
}
