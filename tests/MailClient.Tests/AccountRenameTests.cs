using MailClient.Core;
using MailClient.Storage.Demo;
using MailClient.Storage.Services;
using MailClient.ViewModels;

namespace MailClient.Tests;

public class AccountRenameTests
{
    [Fact]
    public async Task RenameAccount_UpdatesDisplayName_AndTreeTitle()
    {
        await using var tmp = await TempDb.CreateAsync();
        var now = DateTimeOffset.UtcNow;
        var id = Guid.NewGuid();
        await tmp.Database.UpsertAccountAsync(new AccountEntity(
            id, "旧名字", "user@gmail.com", "gmail", AuthKind.OAuth, now, now, null, true));

        using var dir = new TempDir();
        var settings = new AppSettingsStore(Path.Combine(dir.Path, "settings.json"));
        settings.Update(s => s.DemoDataDismissed = true);
        var shell = new ShellViewModel(tmp.Database, new OutboxSendService(tmp.Database),
            null, null, null, settings);
        await shell.InitializeAsync(seedDemoData: false);

        await shell.RenameAccountAsync(id, "我的 Gmail");

        var account = await tmp.Database.GetAccountAsync(id);
        Assert.NotNull(account);
        Assert.Equal("我的 Gmail", account!.DisplayName);

        var node = shell.Tree.Accounts.Single(a => a.AccountId == id);
        Assert.Equal("我的 Gmail", node.Title);
        Assert.Equal("我", node.AvatarLetter);
    }

    [Fact]
    public async Task RenameAccount_Empty_FallsBackToLocalPart()
    {
        await using var tmp = await TempDb.CreateAsync();
        var now = DateTimeOffset.UtcNow;
        var id = Guid.NewGuid();
        await tmp.Database.UpsertAccountAsync(new AccountEntity(
            id, "临时", "alice@contoso.com", "imap.generic", AuthKind.AppPassword, now, now, null, true));

        using var dir = new TempDir();
        var settings = new AppSettingsStore(Path.Combine(dir.Path, "settings.json"));
        settings.Update(s => s.DemoDataDismissed = true);
        var shell = new ShellViewModel(tmp.Database, new OutboxSendService(tmp.Database),
            null, null, null, settings);
        await shell.InitializeAsync(seedDemoData: false);

        await shell.RenameAccountAsync(id, "   ");

        var account = await tmp.Database.GetAccountAsync(id);
        Assert.Equal("alice", account!.DisplayName);
        Assert.Equal("alice", shell.Tree.Accounts.Single().Title);
        Assert.Equal("A", shell.Tree.Accounts.Single().AvatarLetter);
    }
}

public class PreviewRepairTests
{
    [Fact]
    public async Task RepairBadPreviews_RecomputesFromBodyHtml()
    {
        await using var tmp = await TempDb.CreateAsync();
        var now = DateTimeOffset.UtcNow;
        var accountId = Guid.NewGuid();
        await tmp.Database.UpsertAccountAsync(new AccountEntity(
            accountId, "Gmail", "u@gmail.com", "gmail", AuthKind.OAuth, now, now, null, true));
        var folderId = $"{accountId:D}:inbox";
        await tmp.Database.UpsertFolderAsync(new FolderEntity(
            folderId, accountId, "收件箱", SpecialFolder.Inbox, "INBOX", 0, 0, 0, null, null));

        var html = """
            <html><head><style>#outlook a{padding:0;} body{margin:0;-webkit-text-size-adjust:100%;}</style></head>
            <body><p>安全事件进展说明：排查已完成，请查收。</p></body></html>
            """;
        var badPreview = "#outlook a { padding:0; } body { margin:0;padding:0;-webkit-text-size-adjust:100%";
        var messageId = "msg-css-1";
        await tmp.Database.UpsertMessageAsync(new MessageEntity(
            messageId, accountId, folderId, 1, "<1@x>", "1",
            "主题", "noreply@exchange.example", "Example Exchange", badPreview,
            badPreview, html, now, now, true, false, false, false,
            MailCategory.Updates, 1000, BodyFetched: true));

        var n = await tmp.Database.RepairBadPreviewsAsync();
        Assert.True(n >= 1);

        var msg = await tmp.Database.GetMessageAsync(messageId);
        Assert.NotNull(msg);
        Assert.Contains("安全事件", msg!.Preview);
        Assert.DoesNotContain("#outlook", msg.Preview);
        Assert.Equal(html.Trim(), msg.BodyHtml!.Trim());
    }

    [Fact]
    public async Task Initialize_WithSeedDemoFalse_ClearsDemoAccounts()
    {
        await using var tmp = await TempDb.CreateSeededAsync();
        Assert.Contains(await tmp.Database.ListAccountsAsync(), a => DemoDataSeeder.IsDemoAccount(a.Id));

        using var dir = new TempDir();
        var settings = new AppSettingsStore(Path.Combine(dir.Path, "settings.json"));
        var shell = new ShellViewModel(tmp.Database, new OutboxSendService(tmp.Database),
            null, null, null, settings);
        await shell.InitializeAsync(seedDemoData: false);

        Assert.False(shell.HasDemoData);
        Assert.DoesNotContain(await tmp.Database.ListAccountsAsync(), a => DemoDataSeeder.IsDemoAccount(a.Id));
        Assert.True(settings.Snapshot().DemoDataDismissed);
    }
}
