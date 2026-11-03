using MailClient.Core;
using MailKit.Security;

namespace MailClient.Providers.Imap;

internal static class MailKitSocketOptions
{
    public static SecureSocketOptions ToMailKit(MailSecureSocketOptions options) => options switch
    {
        MailSecureSocketOptions.None => SecureSocketOptions.None,
        MailSecureSocketOptions.SslOnConnect => SecureSocketOptions.SslOnConnect,
        MailSecureSocketOptions.StartTls => SecureSocketOptions.StartTls,
        MailSecureSocketOptions.StartTlsWhenAvailable => SecureSocketOptions.StartTlsWhenAvailable,
        _ => SecureSocketOptions.Auto
    };
}
