using MailClient.Core;

namespace MailClient.Providers.Imap;

/// <summary>常见邮箱的 IMAP/SMTP 预设（授权码登录）。</summary>
public static class ImapProviderPresets
{
    public static (ImapServerSettings Imap, SmtpServerSettings Smtp) QqMail()
        => (
            new ImapServerSettings("imap.qq.com", 993, MailSecureSocketOptions.SslOnConnect),
            new SmtpServerSettings("smtp.qq.com", 465, MailSecureSocketOptions.SslOnConnect));

    public static (ImapServerSettings Imap, SmtpServerSettings Smtp) NetEase163()
        => (
            new ImapServerSettings("imap.163.com", 993, MailSecureSocketOptions.SslOnConnect),
            new SmtpServerSettings("smtp.163.com", 465, MailSecureSocketOptions.SslOnConnect));

    public static (ImapServerSettings Imap, SmtpServerSettings Smtp) NetEase126()
        => (
            new ImapServerSettings("imap.126.com", 993, MailSecureSocketOptions.SslOnConnect),
            new SmtpServerSettings("smtp.126.com", 465, MailSecureSocketOptions.SslOnConnect));

    public static (ImapServerSettings Imap, SmtpServerSettings Smtp) GmailImap()
        => (
            new ImapServerSettings("imap.gmail.com", 993, MailSecureSocketOptions.SslOnConnect),
            new SmtpServerSettings("smtp.gmail.com", 465, MailSecureSocketOptions.SslOnConnect));

    public static (ImapServerSettings Imap, SmtpServerSettings Smtp) OutlookImap()
        => (
            new ImapServerSettings("outlook.office365.com", 993, MailSecureSocketOptions.SslOnConnect),
            new SmtpServerSettings("smtp.office365.com", 587, MailSecureSocketOptions.StartTls));
}
