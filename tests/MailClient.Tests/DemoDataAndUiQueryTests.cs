using MailClient.Core;
using MailClient.Storage;
using MailClient.Storage.Demo;

namespace MailClient.Tests;

public class DemoDataAndUiQueryTests
{
    [Fact]
    public async Task Seeder_Creates_Accounts_Folders_And_Messages_Once()
    {
        await using var tmp = await TempDb.CreateSeededAsync();
        var db = tmp.Database;

        var accounts = await db.ListAccountsAsync();
        Assert.Equal(2, accounts.Count);

        var folders = await db.ListAllFoldersAsync();
        Assert.Equal(12, folders.Count);

        var all = await db.QueryMessagesAsync(new MessageListFilter(null, null, false, false, null, 1000));
        Assert.InRange(all.Count, 30, 60);

        // 幂等：再次 Seed 不增加
        await DemoDataSeeder.SeedIfEmptyAsync(db);
        var again = await db.QueryMessagesAsync(new MessageListFilter(null, null, false, false, null, 1000));
        Assert.Equal(all.Count, again.Count);
    }

    [Fact]
    public async Task Inbox_Messages_Cover_All_Four_Categories()
    {
        await using var tmp = await TempDb.CreateSeededAsync();
        var db = tmp.Database;
        var inboxIds = (await db.ListAllFoldersAsync()).Where(f => f.Special == SpecialFolder.Inbox).Select(f => f.Id).ToList();

        foreach (var category in new[] { MailCategory.Primary, MailCategory.Transactions, MailCategory.Updates, MailCategory.Promotions })
        {
            var rows = await db.QueryMessagesAsync(new MessageListFilter(inboxIds, category, false, false, null, 500));
            Assert.NotEmpty(rows);
            Assert.All(rows, r => Assert.Equal(category, r.Category));
        }
    }

    [Fact]
    public async Task Query_Filters_Unread_Flagged_And_Search()
    {
        await using var tmp = await TempDb.CreateSeededAsync();
        var db = tmp.Database;

        var unread = await db.QueryMessagesAsync(new MessageListFilter(null, null, true, false, null, 500));
        Assert.NotEmpty(unread);
        Assert.All(unread, m => Assert.False(m.IsRead));

        var flagged = await db.QueryMessagesAsync(new MessageListFilter(null, null, false, true, null, 500));
        Assert.NotEmpty(flagged);
        Assert.All(flagged, m => Assert.True(m.IsFlagged));

        var all = await db.QueryMessagesAsync(new MessageListFilter(null, null, false, false, null, 1000));
        var hits = await db.QueryMessagesAsync(new MessageListFilter(null, null, false, false, "项目", 500));
        Assert.NotEmpty(hits);
        Assert.All(hits, m => Assert.True(
            m.Subject.Contains("项目") || (m.Preview ?? "").Contains("项目") || m.FromAddress.Contains("项目") || (m.FromName ?? "").Contains("项目")));

        // LIKE 通配符必须被转义：搜 "%" 只能命中字面含 % 的邮件，而不是所有邮件
        var percent = await db.QueryMessagesAsync(new MessageListFilter(null, null, false, false, "%", 500));
        Assert.True(percent.Count < all.Count);
        Assert.All(percent, m => Assert.Contains("%", m.Subject + m.Preview + m.FromAddress + m.FromName));    }

    [Fact]
    public async Task Read_Flag_Move_Delete_Update_Counts()
    {
        await using var tmp = await TempDb.CreateSeededAsync();
        var db = tmp.Database;
        var folders = await db.ListAllFoldersAsync();
        var inbox = folders.First(f => f.Special == SpecialFolder.Inbox);
        var trash = folders.First(f => f.AccountId == inbox.AccountId && f.Special == SpecialFolder.Trash);
        var unread = (await db.QueryMessagesAsync(new MessageListFilter(new[] { inbox.Id }, null, true, false, null, 500))).First();

        await db.SetMessageReadAsync(unread.Id, true);
        await db.SetMessageFlaggedAsync(unread.Id, true);
        var reloaded = await db.GetMessageAsync(unread.Id);
        Assert.True(reloaded!.IsRead);
        Assert.True(reloaded.IsFlagged);

        await db.MoveMessageAsync(unread.Id, trash.Id);
        Assert.Equal(trash.Id, (await db.GetMessageAsync(unread.Id))!.FolderId);

        await db.DeleteMessageAsync(unread.Id);
        Assert.Null(await db.GetMessageAsync(unread.Id));

        var after = (await db.ListAllFoldersAsync()).First(f => f.Id == inbox.Id);
        var actualUnread = (await db.QueryMessagesAsync(new MessageListFilter(new[] { inbox.Id }, null, true, false, null, 500))).Count;
        Assert.Equal(actualUnread, after.UnreadCount);
    }

    [Fact]
    public async Task Concurrent_Reads_And_Writes_Do_Not_Throw()
    {
        await using var tmp = await TempDb.CreateSeededAsync();
        var db = tmp.Database;
        var id = (await db.QueryMessagesAsync(new MessageListFilter(null, null, false, false, null, 5))).First().Id;
        var tasks = Enumerable.Range(0, 40).Select(i => i % 2 == 0
            ? db.QueryMessagesAsync(new MessageListFilter(null, null, false, false, null, 50))
            : db.SetMessageReadAsync(id, i % 4 == 1));
        await Task.WhenAll(tasks);
    }

    [Fact]
    public async Task Null_Category_Messages_Show_Up_In_Primary_Tab_Only()
    {
        await using var tmp = new TempDb();
        var db = tmp.Database;
        await db.InitializeAsync();
        var accountId = Guid.NewGuid();
        await db.UpsertAccountAsync(new AccountEntity(accountId, "A", "a@b.com", "imap.generic", AuthKind.AppPassword, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, null, true));
        await db.UpsertFolderAsync(new FolderEntity($"{accountId:D}:INBOX", accountId, "INBOX", SpecialFolder.Inbox, "INBOX", 0, 0, 0, null, null));
        await db.UpsertMessagesAsync([
            new MessageEntity("m1", accountId, $"{accountId:D}:INBOX", 1, null, null, "无分类", "x@y.com", null, "", null, null,
                DateTimeOffset.UtcNow, null, false, false, false, false, null, 0)
        ]);

        var primary = await db.QueryMessagesAsync(new MessageListFilter(null, MailCategory.Primary));
        var promo = await db.QueryMessagesAsync(new MessageListFilter(null, MailCategory.Promotions));
        Assert.Single(primary);
        Assert.Empty(promo);
    }
}
