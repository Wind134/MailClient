using MailClient.Core;
using MailClient.Providers;
using MailClient.Providers.Imap;

namespace MailClient.Tests;

/// <summary>
/// 可选联调：设置环境变量后才会真正连接。
/// MAILCLIENT_IMAP_HOST, MAILCLIENT_IMAP_PORT, MAILCLIENT_IMAP_USER, MAILCLIENT_IMAP_PASSWORD
/// 可选：MAILCLIENT_SMTP_HOST, MAILCLIENT_SMTP_PORT, MAILCLIENT_IMAP_SSL=ssl|starttls
/// </summary>
public class LiveImapTests
{
    private static bool IsConfigured()
        => !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("MAILCLIENT_IMAP_HOST"))
           && !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("MAILCLIENT_IMAP_USER"))
           && !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("MAILCLIENT_IMAP_PASSWORD"));

    [Fact]
    public async Task Connect_ListFolders_WhenEnvConfigured()
    {
        if (!IsConfigured())
        {
            return;
        }

        var host = Environment.GetEnvironmentVariable("MAILCLIENT_IMAP_HOST")!;
        var port = int.Parse(Environment.GetEnvironmentVariable("MAILCLIENT_IMAP_PORT") ?? "993");
        var user = Environment.GetEnvironmentVariable("MAILCLIENT_IMAP_USER")!;
        var pass = Environment.GetEnvironmentVariable("MAILCLIENT_IMAP_PASSWORD")!;
        var ssl = (Environment.GetEnvironmentVariable("MAILCLIENT_IMAP_SSL") ?? "ssl").ToLowerInvariant();
        var socket = ssl switch
        {
            "starttls" => MailSecureSocketOptions.StartTls,
            "none" => MailSecureSocketOptions.None,
            _ => MailSecureSocketOptions.SslOnConnect
        };

        var smtpHost = Environment.GetEnvironmentVariable("MAILCLIENT_SMTP_HOST") ?? host.Replace("imap", "smtp");
        var smtpPort = int.Parse(Environment.GetEnvironmentVariable("MAILCLIENT_SMTP_PORT") ?? "465");

        var config = new GenericImapAccountConfig(
            AccountId: Guid.NewGuid(),
            EmailAddress: user,
            DisplayName: user,
            Imap: new ImapServerSettings(host, port, socket),
            Smtp: new SmtpServerSettings(smtpHost, smtpPort, socket),
            CredentialVaultKey: null);

        await using var provider = new GenericImapProvider(config, new ImapAccountSecrets(user, pass));
        await provider.ConnectAsync();
        var folders = await provider.GetFoldersAsync();
        Assert.NotEmpty(folders);
        Assert.Contains(folders, f => f.Special == SpecialFolder.Inbox);

        var inbox = folders.First(f => f.Special == SpecialFolder.Inbox);
        var messages = await provider.SyncFolderDetailedAsync(inbox.RemotePath ?? inbox.Id, maxCount: 5);
        Assert.NotNull(messages);

        await provider.DisconnectAsync();
    }
}
