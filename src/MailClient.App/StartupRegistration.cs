using MailClient.Core;
using MailClient.Core.Tray;
using Microsoft.Win32;
using Windows.ApplicationModel;

namespace MailClient_App;

/// <summary>
/// 当前用户的开机启动。免打包（exe 安装版 / 绿色版）写 HKCU Run，并带 <c>--silent</c>；
/// MSIX 用清单里的 StartupTask。都不需要管理员。
/// </summary>
internal static class StartupRegistration
{
    public const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    public const string ApprovedKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";
    public const string PackagedTaskId = "MailClientStartup";

    public readonly record struct Result(bool Enabled, bool CanChange, string? Message, bool IsError);

    /// <summary>启动时对齐免打包的 Run 项和 settings.json。不改任务管理器的禁用标记。</summary>
    public static void ReconcileUnpackaged(AppSettingsStore settings)
    {
        if (AppIdentity.IsPackaged)
        {
            return;
        }

        if (!settings.Snapshot().LaunchAtStartup)
        {
            RemoveUnpackaged();
            return;
        }

        var exe = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(exe) || IsApprovedDisabled())
        {
            return;
        }

        if (StartupLaunch.CommandMatches(ReadCommand(), exe))
        {
            return;
        }

        WriteCommand(exe);
    }

    public static Result ReadUnpackaged()
    {
        try
        {
            var command = ReadCommand();
            if (string.IsNullOrWhiteSpace(command))
            {
                return new Result(false, true, null, false);
            }

            if (IsApprovedDisabled())
            {
                return new Result(false, true, "已在任务管理器的「启动应用」里关闭。打开下面的开关会重新启用。", false);
            }

            return new Result(true, true, null, false);
        }
        catch (Exception ex)
        {
            return new Result(false, true, "无法读取开机启动：" + ex.Message, true);
        }
    }

    public static async Task<Result> ReadAsync()
    {
        if (!AppIdentity.IsPackaged)
        {
            return ReadUnpackaged();
        }

        try
        {
            var task = await StartupTask.GetAsync(PackagedTaskId);
            return FromState(task.State, null);
        }
        catch (Exception ex)
        {
            return new Result(false, false, "无法读取开机启动：" + ex.Message, true);
        }
    }

    public static async Task<Result> SetAsync(bool enabled)
    {
        if (!AppIdentity.IsPackaged)
        {
            return SetUnpackaged(enabled);
        }

        try
        {
            var task = await StartupTask.GetAsync(PackagedTaskId);
            if (enabled)
            {
                if (task.State is StartupTaskState.Enabled or StartupTaskState.EnabledByPolicy)
                {
                    return FromState(task.State, null);
                }

                var state = await task.RequestEnableAsync();
                var note = state == StartupTaskState.DisabledByUser
                    ? "Windows 不允许这里直接打开。请到「设置 → 应用 → 启动」里启用「邮件」。"
                    : null;
                return FromState(state, note);
            }

            if (task.State is StartupTaskState.Disabled or StartupTaskState.DisabledByUser or StartupTaskState.DisabledByPolicy)
            {
                return FromState(task.State, null);
            }

            task.Disable();
            return FromState(StartupTaskState.Disabled, null);
        }
        catch (Exception ex)
        {
            return new Result(enabled, true, "设置开机启动失败：" + ex.Message, true);
        }
    }

    private static Result SetUnpackaged(bool enabled)
    {
        try
        {
            if (!enabled)
            {
                RemoveUnpackaged();
                return new Result(false, true, null, false);
            }

            var exe = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(exe))
            {
                return new Result(false, true, "找不到程序路径，无法设置开机启动。", true);
            }

            WriteCommand(exe);
            using var approved = Registry.CurrentUser.CreateSubKey(ApprovedKeyPath, writable: true);
            approved?.SetValue(StartupLaunch.RunValueName, StartupLaunch.ApprovedEnabledBlob(), RegistryValueKind.Binary);
            return new Result(true, true, null, false);
        }
        catch (Exception ex)
        {
            return new Result(ReadUnpackaged().Enabled, true, "设置开机启动失败：" + ex.Message, true);
        }
    }

    private static Result FromState(StartupTaskState state, string? note) => state switch
    {
        StartupTaskState.Enabled => new Result(true, true, note, false),
        StartupTaskState.EnabledByPolicy => new Result(true, false, note ?? "系统策略要求开机启动，这里不能关掉。", false),
        StartupTaskState.DisabledByUser => new Result(false, false, note ?? "已在 Windows 的「启动应用」里关闭。请到那里重新打开「邮件」。", false),
        StartupTaskState.DisabledByPolicy => new Result(false, false, note ?? "系统策略禁止开机启动。", false),
        _ => new Result(false, true, note, false)
    };

    private static string? ReadCommand()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath);
        return key?.GetValue(StartupLaunch.RunValueName) as string;
    }

    private static void WriteCommand(string exePath)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true)
            ?? throw new InvalidOperationException("无法写入开机启动项。");
        key.SetValue(StartupLaunch.RunValueName, StartupLaunch.BuildCommand(exePath), RegistryValueKind.String);
    }

    private static void RemoveUnpackaged()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
        key?.DeleteValue(StartupLaunch.RunValueName, throwOnMissingValue: false);
    }

    private static bool IsApprovedDisabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(ApprovedKeyPath);
        return StartupLaunch.IsApprovedDisabled(key?.GetValue(StartupLaunch.RunValueName) as byte[]);
    }
}
