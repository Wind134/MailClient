using MailClient.Auth;
using MailClient.Core;
using MailClient.Providers;

namespace MailClient.Tests;

/// <summary>
/// 真实 Gmail 联调（手动）。需要环境变量：
/// MAILCLIENT_GMAIL_CLIENT_ID、MAILCLIENT_GMAIL_EMAIL；可选 MAILCLIENT_GMAIL_CLIENT_SECRET；
/// 首次运行会拉起系统浏览器让你登录授权（5 分钟内完成），之后令牌在内存凭据库里复用。
/// 若已有 refresh token，可设置 MAILCLIENT_GMAIL_REFRESH_TOKEN 跳过浏览器（自动化 / CI）。
/// 未设置 MAILCLIENT_GMAIL_CLIENT_ID 时直接跳过。
/// </summary>
public class LiveGmailTests
{
    private static string? Env(string name)
    {
        var v = Environment.GetEnvironmentVariable(name);
        return string.IsNullOrWhiteSpace(v) ? null : v;
    }

    [Fact]
    public async Task Connect_list_folders_and_sync_inbox_when_env_set()
    {
        var clientId = Env("MAILCLIENT_GMAIL_CLIENT_ID");
        var email = Env("MAILCLIENT_GMAIL_EMAIL");
        if (clientId is null || email is null)
        {
            return; // 没有真实凭据时安静跳过
        }

        var config = new GmailAccountConfig(Guid.NewGuid(), email, "Live", clientId, Env("MAILCLIENT_GMAIL_CLIENT_SECRET"));
        var store = new InMemoryCredentialStore();
        if (Env("MAILCLIENT_GMAIL_REFRESH_TOKEN") is { } refresh)
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
