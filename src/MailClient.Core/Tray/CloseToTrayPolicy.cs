namespace MailClient.Core.Tray;

/// <summary>
/// 关闭主窗口时：是藏到托盘继续跑，还是真正退出。纯逻辑，方便单测。
/// </summary>
public static class CloseToTrayPolicy
{
    /// <summary>
    /// 默认：关闭主窗口时保留在后台（托盘）。
    /// </summary>
    public const bool DefaultKeepRunningInBackground = true;

    /// <summary>
    /// 是否应该取消关闭、把窗口藏起来（进程继续跑）。
    /// </summary>
    /// <param name="keepRunningInBackground">设置里的「关闭主窗口时保留在后台」。</param>
    /// <param name="forceQuit">托盘菜单「退出」或其它强制退出路径。</param>
    public static bool ShouldHideInsteadOfClose(bool keepRunningInBackground, bool forceQuit)
        => keepRunningInBackground && !forceQuit;

    /// <summary>
    /// 设置里没写或反序列化得到的可空值 → 落到默认。
    /// </summary>
    public static bool ResolveKeepRunning(bool? setting)
        => setting ?? DefaultKeepRunningInBackground;
}
