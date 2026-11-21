using MailClient.Auth;
using MailClient.Core;
using MailClient.Providers;

namespace MailClient.Tests;

/// <summary>
/// 真实 Gmail 联调（手动执行）。需要环境变量 MAILCLIENT_GMAIL_CLIENT_ID、MAILCLIENT_GMAIL_EMAIL，
/// 可选 MAILCLIENT_GMAIL_CLIENT_SECRET。首次运行会拉起浏览器授权（5 分钟内完成）；
/// 若设置 MAILCLIENT_GMAIL_REFRESH_TOKEN 则跳过浏览器。未配置时用例标记为跳过。
/// </summary>
[Trait("Category", "Live")]
public class LiveGmailTests
{
    [SkippableFact]
    public async Task Connect_list_folders_and_sync_inbox_when_env_set()
    {
        var clientId = LiveEnvironment.Require("MAILCLIENT_GMAIL_CLIENT_ID");
        var email = LiveEnvironment.Require("MAILCLIENT_GMAIL_EMAIL");
        var config = new GmailAccountConfig(Guid.NewGuid(), email, "Live", clientId, LiveEnvironment.Optional("MAILCLIENT_GMAIL_CLIENT_SECRET"));

        var store = new InMemoryCredentialStore();
        if (LiveEnvironment.Optional("MAILCLIENT_GMAIL_REFRESH_TOKEN") is { } refresh)
        {
            await new GmailTokenStore(store, config.EffectiveVaultKey)
                .SaveAsync(new GmailTokenSet("", refresh, DateTimeOffset.UtcNow.AddMinutes(-1), email, null));
        }

        using var acquirer = new GmailTokenAcquirer(config, store);
        await acquirer.AcquireAsync(allowInteractive: true);

        await using var provider = new GmailProvider(config, acquirer);
        await provider.ConnectAsync();
        var folders = await provider.GetFoldersAsync();
        Assert.Contains(folders, f => f.Special == SpecialFolder.Inbox);

        var inbox = await provider.Imap.SyncFolderDetailedAsync("INBOX", maxCount: 5);
        Assert.All(inbox, i => Assert.False(string.IsNullOrEmpty(i.Summary.Id)));
        await provider.DisconnectAsync();
    }
}
