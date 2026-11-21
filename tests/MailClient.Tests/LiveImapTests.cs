using MailClient.Core;
using MailClient.Providers;
using MailClient.Providers.Imap;

namespace MailClient.Tests;

/// <summary>
/// 真实 IMAP 联调（手动执行）。需要环境变量 MAILCLIENT_IMAP_HOST / USER / PASSWORD，
/// 可选 MAILCLIENT_IMAP_PORT(993)、MAILCLIENT_IMAP_SSL(ssl|starttls|none)、
/// MAILCLIENT_SMTP_HOST、MAILCLIENT_SMTP_PORT(465)。未配置时用例标记为跳过。
/// </summary>
[Trait("Category", "Live")]
public class LiveImapTests
{
    [SkippableFact]
    public async Task Connect_ListFolders_WhenEnvConfigured()
    {
        var host = LiveEnvironment.Require("MAILCLIENT_IMAP_HOST");
        var user = LiveEnvironment.Require("MAILCLIENT_IMAP_USER");
        var pass = LiveEnvironment.Require("MAILCLIENT_IMAP_PASSWORD");
        var port = LiveEnvironment.OptionalPort("MAILCLIENT_IMAP_PORT", 993);
        var ssl = (LiveEnvironment.Optional("MAILCLIENT_IMAP_SSL") ?? "ssl").ToLowerInvariant();
        var socket = ssl switch
        {
            "starttls" => MailSecureSocketOptions.StartTls,
            "none" => MailSecureSocketOptions.None,
            _ => MailSecureSocketOptions.SslOnConnect
        };

        var smtpHost = LiveEnvironment.Optional("MAILCLIENT_SMTP_HOST") ?? host.Replace("imap", "smtp");
        var smtpPort = LiveEnvironment.OptionalPort("MAILCLIENT_SMTP_PORT", 465);

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
