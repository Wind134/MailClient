using MailClient.Core;
using MailClient.Providers.Imap;

namespace MailClient.Providers.Setup;

/// <summary>添加账号向导里的“邮箱类型”。</summary>
public enum SetupProviderKind
{
    Gmail,
    Outlook,
    Qq,
    NetEase,
    OtherImap
}

/// <summary>一个邮箱域名对应的 IMAP/SMTP 预设。</summary>
/// <param name="UsesAuthCode">是否必须使用“授权码 / 应用专用密码”而不是网页登录密码。</param>
public sealed record ProviderPreset(
    SetupProviderKind Kind,
    string DisplayName,
    ImapServerSettings Imap,
    SmtpServerSettings Smtp,
    bool UsesAuthCode);

/// <summary>按邮箱域名识别服务商 / 服务器预设（纯函数，可单测）。</summary>
public static class ProviderDetector
{
    private static readonly Dictionary<string, Func<ProviderPreset>> ByDomain = Build();

    /// <summary>粗略校验邮箱地址：恰好一个 @，本地部分与域名非空，域名含点且无空白。</summary>
    public static bool IsValidEmail(string? email)
    {
        if (string.IsNullOrWhiteSpace(email))
        {
            return false;
        }

        var text = email.Trim();
        if (text.Any(char.IsWhiteSpace) || text.Count(c => c == '@') != 1)
        {
            return false;
        }

        var at = text.IndexOf('@');
        var local = text[..at];
        var domain = text[(at + 1)..];
        return local.Length > 0 &&
               domain.Length >= 3 &&
               domain.Contains('.') &&
               !domain.StartsWith('.') &&
               !domain.EndsWith('.') &&
               !domain.Contains("..");
    }

    /// <summary>取域名（小写）；非法地址返回 null。</summary>
    public static string? ExtractDomain(string? email)
        => IsValidEmail(email) ? email!.Trim()[(email!.Trim().IndexOf('@') + 1)..].ToLowerInvariant() : null;

    public static string? ExtractLocalPart(string? email)
        => IsValidEmail(email) ? email!.Trim()[..email!.Trim().IndexOf('@')] : null;

    /// <summary>按邮箱域名查预设；未知域名返回 null（调用方再走 ISPDB 自动配置）。</summary>
    public static ProviderPreset? Detect(string? email)
    {
        var domain = ExtractDomain(email);
        return domain is not null && ByDomain.TryGetValue(domain, out var factory) ? factory() : null;
    }

    /// <summary>向导里选中某类型 + 已输入邮箱时应使用的预设（163 与 126 按域名区分）。</summary>
    public static ProviderPreset? PresetFor(SetupProviderKind kind, string? email)
    {
        switch (kind)
        {
            case SetupProviderKind.Qq:
                return Qq();
            case SetupProviderKind.NetEase:
                var detected = Detect(email);
                return detected is { Kind: SetupProviderKind.NetEase } ? detected : NetEase163();
            case SetupProviderKind.Gmail:
                return Gmail();
            case SetupProviderKind.Outlook:
                return Outlook();
            default:
                return Detect(email);
        }
    }

    public static string ProviderTitle(SetupProviderKind kind) => kind switch
    {
        SetupProviderKind.Gmail => "Gmail",
        SetupProviderKind.Outlook => "Outlook",
        SetupProviderKind.Qq => "QQ 邮箱",
        SetupProviderKind.NetEase => "网易邮箱",
        _ => "IMAP 邮箱"
    };

    private static Dictionary<string, Func<ProviderPreset>> Build()
    {
        var map = new Dictionary<string, Func<ProviderPreset>>(StringComparer.OrdinalIgnoreCase);
        foreach (var d in new[] { "gmail.com", "googlemail.com" })
        {
            map[d] = Gmail;
        }

        foreach (var d in new[] { "outlook.com", "hotmail.com", "live.com", "msn.com", "outlook.cn", "hotmail.co.uk", "live.cn" })
        {
            map[d] = Outlook;
        }

        foreach (var d in new[] { "qq.com", "foxmail.com", "vip.qq.com" })
        {
            map[d] = Qq;
        }

        map["163.com"] = NetEase163;
        map["126.com"] = NetEase126;
        map["yeah.net"] = () => NetEase("yeah.net", "imap.yeah.net", "smtp.yeah.net", "网易 yeah.net 邮箱");
        map["188.com"] = () => NetEase("188.com", "imap.188.com", "smtp.188.com", "网易 188 邮箱");
        map["vip.163.com"] = () => NetEase("vip.163.com", "imap.vip.163.com", "smtp.vip.163.com", "网易 VIP 邮箱");

        map["icloud.com"] = () => Other("iCloud 邮箱", "imap.mail.me.com", 993, MailSecureSocketOptions.SslOnConnect, "smtp.mail.me.com", 587, MailSecureSocketOptions.StartTls, true);
        map["me.com"] = map["icloud.com"];
        map["yahoo.com"] = () => Other("Yahoo 邮箱", "imap.mail.yahoo.com", 993, MailSecureSocketOptions.SslOnConnect, "smtp.mail.yahoo.com", 465, MailSecureSocketOptions.SslOnConnect, true);
        map["sina.com"] = () => Other("新浪邮箱", "imap.sina.com", 993, MailSecureSocketOptions.SslOnConnect, "smtp.sina.com", 465, MailSecureSocketOptions.SslOnConnect, true);
        map["sina.cn"] = () => Other("新浪邮箱", "imap.sina.cn", 993, MailSecureSocketOptions.SslOnConnect, "smtp.sina.cn", 465, MailSecureSocketOptions.SslOnConnect, true);
        map["sohu.com"] = () => Other("搜狐邮箱", "imap.sohu.com", 993, MailSecureSocketOptions.SslOnConnect, "smtp.sohu.com", 465, MailSecureSocketOptions.SslOnConnect, true);
        map["aliyun.com"] = () => Other("阿里邮箱", "imap.aliyun.com", 993, MailSecureSocketOptions.SslOnConnect, "smtp.aliyun.com", 465, MailSecureSocketOptions.SslOnConnect, false);
        map["139.com"] = () => Other("139 邮箱", "imap.139.com", 993, MailSecureSocketOptions.SslOnConnect, "smtp.139.com", 465, MailSecureSocketOptions.SslOnConnect, true);
        map["exmail.qq.com"] = () => Other("腾讯企业邮箱", "imap.exmail.qq.com", 993, MailSecureSocketOptions.SslOnConnect, "smtp.exmail.qq.com", 465, MailSecureSocketOptions.SslOnConnect, true);
        return map;
    }

    private static ProviderPreset Gmail()
    {
        var (imap, smtp) = ImapProviderPresets.GmailImap();
        return new ProviderPreset(SetupProviderKind.Gmail, "Gmail", imap, smtp, true);
    }

    private static ProviderPreset Outlook()
    {
        var (imap, smtp) = ImapProviderPresets.OutlookImap();
        return new ProviderPreset(SetupProviderKind.Outlook, "Outlook / Microsoft 365", imap, smtp, false);
    }

    private static ProviderPreset Qq()
    {
        var (imap, smtp) = ImapProviderPresets.QqMail();
        return new ProviderPreset(SetupProviderKind.Qq, "QQ 邮箱", imap, smtp, true);
    }

    private static ProviderPreset NetEase163()
    {
        var (imap, smtp) = ImapProviderPresets.NetEase163();
        return new ProviderPreset(SetupProviderKind.NetEase, "163 邮箱", imap, smtp, true);
    }

    private static ProviderPreset NetEase126()
    {
        var (imap, smtp) = ImapProviderPresets.NetEase126();
        return new ProviderPreset(SetupProviderKind.NetEase, "126 邮箱", imap, smtp, true);
    }

    private static ProviderPreset NetEase(string _, string imapHost, string smtpHost, string name)
        => new(
            SetupProviderKind.NetEase,
            name,
            new ImapServerSettings(imapHost, 993, MailSecureSocketOptions.SslOnConnect),
            new SmtpServerSettings(smtpHost, 465, MailSecureSocketOptions.SslOnConnect),
            true);

    private static ProviderPreset Other(
        string name, string imapHost, int imapPort, MailSecureSocketOptions imapSec,
        string smtpHost, int smtpPort, MailSecureSocketOptions smtpSec, bool authCode)
        => new(
            SetupProviderKind.OtherImap,
            name,
            new ImapServerSettings(imapHost, imapPort, imapSec),
            new SmtpServerSettings(smtpHost, smtpPort, smtpSec),
            authCode);
}
