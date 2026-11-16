namespace MailClient.Core.Auth;

/// <summary>“登录已失效 / 重新登录”相关的界面文字（集中放在这里，测试和界面共用）。</summary>
public static class ReloginTexts
{
    public const string SyncAuthFailed = "登录已失效，请右键账号选择“重新登录”。";

    public const string SidebarTooltip = "登录已失效，右键重新登录";

    public const string MenuRelogin = "重新登录";

    public const string Checking = "正在检查连接…";

    public const string SigningIn = "正在等待你在浏览器中完成登录…";

    public const string VerifyingPassword = "正在验证…";

    public const string ButtonRelogin = "重新登录";

    public const string ButtonLater = "稍后";

    public const string ButtonSyncNow = "立即同步";

    public const string ButtonReloginAnyway = "仍要重新登录";

    public const string ButtonClose = "关闭";

    public const string ButtonCancel = "取消";

    public static string ExpiredTitle(string name) => $"「{name}」登录已失效";

    public const string ExpiredBody = "邮件已停止同步，请重新登录以恢复。本地邮件不会被删除。";

    public static string HealthyTitle(string name) => $"「{name}」连接正常";

    public const string HealthyBody =
        "当前登录状态有效，无需重新登录。如果仍有同步问题，可以尝试：立即同步；或删除该账户后重新添加（会清除该账户的本地缓存）。";

    public static string NetworkTitle(string name) => $"暂时连不上「{name}」";

    public static string NetworkBody(string? detail) =>
        "看起来是网络问题，登录状态可能仍然有效。请检查网络连接后再试。" +
        (string.IsNullOrWhiteSpace(detail) ? string.Empty : $"\n\n详细信息：{detail}");

    public static string CheckFailedTitle(string name) => $"「{name}」连接检查未通过";

    public static string CheckFailedBody(string? detail) =>
        (string.IsNullOrWhiteSpace(detail) ? "检查连接时出错。" : detail) + "\n\n重新登录不一定能解决这个问题，你也可以仍要重新登录试试。";

    public static string PasswordTitle(string name) => $"重新登录「{name}」";

    public static string PasswordBody(string email) => $"请输入 {email} 的密码或授权码。验证通过后才会替换原来保存的密码，本地邮件不会被删除。";

    public static string Succeeded(string name) => $"「{name}」已重新登录，正在同步…";

    public static string Failed(string name, string? error) => $"「{name}」重新登录失败：{error ?? "未知错误"}";

    public static string MismatchTitle => "登录的不是这个账号";
}
