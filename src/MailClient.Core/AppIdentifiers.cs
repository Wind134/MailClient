namespace MailClient.Core;

/// <summary>
/// 应用与安装程序共同约定的标识。
/// </summary>
/// <remarks>
/// 这些值会写进注册表（AppUserModelId 注册、启动项），应用与安装程序必须完全一致，
/// 因此集中在这里由两端引用同一常量。此前两边各写一遍字面量，改一边不会报错，
/// 只会让通知标题、任务栏分组或开机启动静默失效。
/// </remarks>
public static class AppIdentifiers
{
    /// <summary>显示名（通知标题、快捷方式、卸载列表都用它）。</summary>
    public const string ProductName = "邮件";

    /// <summary>免打包运行时的 AppUserModelID。</summary>
    public const string Aumid = "MailClient.Desktop";

    /// <summary>HKCU\...\Run 下的值名（开机启动）。</summary>
    public const string StartupRunValueName = "MailClient";
}
