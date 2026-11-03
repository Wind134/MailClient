using MailClient.Core;
using MailClient.Core.Notifications;
using MailClient.Core.Sync;
using MailClient.Storage;

namespace MailClient.Tests;

/// <summary>同步前快照 + 同步后查询：只把“同步期间新到的未读收件箱邮件”交给通知策略。</summary>
public sealed class NewMailDetectionTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 31, 12, 0, 0, TimeSpan.FromHours(8));
    private static readonly Guid AccId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly string InboxId = $"{AccId:D}:INBOX";

    private static AccountEntity Account() => new(AccId, "测试账号", "me@example.com", "imap", AuthKind.Password, Now, Now, null, true);

    private static async Task<TempDb> CreateAsync(string? inboxCursor)
    {
        var db = new TempDb();
        await db.Database.InitializeAsync();
        await db.Database.UpsertAccountAsync(Account());
        await db.Database.UpsertFolderAsync(new FolderEntity(InboxId, AccId, "INBOX", SpecialFolder.Inbox, "INBOX", 0, 0, 0, null, null));
        if (inboxCursor is not null)
        {
            await db.Database.SaveFolderSyncStateAsync(new FolderSyncState(InboxId, inboxCursor, null, null, null, null));
        }

        return db;
    }

    private static MessageEntity Msg(string id, bool read = false, string from = "alice@example.org", DateTimeOffset? at = null)
        => new(id, AccId, InboxId, null, null, null, $"主题 {id}", from, "Alice", "内容预览", null, null, at ?? Now.AddMinutes(-2), null, read, false, false, false, null, 100);

    [Fact]
    public async Task Initial_sync_produces_no_notifications()
    {
        await using var db = await CreateAsync(inboxCursor: null);
        var snap = await db.Database.SnapshotInboxAsync(AccId);
        Assert.True(snap.IsInitialSync);
        await db.Database.UpsertMessageAsync(Msg("m1"));
        Assert.Empty(await db.Database.FindNewMailAsync(Account(), snap, true, Now));
    }

    [Fact]
    public async Task New_unread_message_after_a_completed_sync_is_reported()
    {
        await using var db = await CreateAsync(inboxCursor: "5");
        await db.Database.UpsertMessageAsync(Msg("old"));
        var snap = await db.Database.SnapshotInboxAsync(AccId);
        Assert.False(snap.IsInitialSync);
        Assert.Contains("old", snap.KnownIds);

        await db.Database.UpsertMessageAsync(Msg("new"));
        var found = await db.Database.FindNewMailAsync(Account(), snap, true, Now);
        var only = Assert.Single(found);
        Assert.Equal("new", only.MessageId);
        Assert.Equal("Alice", only.SenderDisplay);
        Assert.Equal(AccId, only.AccountId);
    }

    [Fact]
    public async Task Known_message_that_merely_became_unread_is_not_reported()
    {
        await using var db = await CreateAsync(inboxCursor: "5");
        await db.Database.UpsertMessageAsync(Msg("m1", read: true));
        var snap = await db.Database.SnapshotInboxAsync(AccId);
        await db.Database.UpsertMessageAsync(Msg("m1", read: false)); // 服务器端被标回未读
        Assert.Empty(await db.Database.FindNewMailAsync(Account(), snap, true, Now));
    }

    [Fact]
    public async Task Already_read_and_own_messages_and_disabled_setting_are_not_reported()
    {
        await using var db = await CreateAsync(inboxCursor: "5");
        var snap = await db.Database.SnapshotInboxAsync(AccId);
        await db.Database.UpsertMessageAsync(Msg("read", read: true));
        await db.Database.UpsertMessageAsync(Msg("mine", from: "ME@example.com"));
        Assert.Empty(await db.Database.FindNewMailAsync(Account(), snap, true, Now));

        await db.Database.UpsertMessageAsync(Msg("real"));
        Assert.Empty(await db.Database.FindNewMailAsync(Account(), snap, notificationsEnabled: false, Now));
        Assert.Single(await db.Database.FindNewMailAsync(Account(), snap, true, Now));
    }

    [Fact]
    public async Task Missing_inbox_folder_counts_as_initial_sync()
    {
        await using var db = new TempDb();
        await db.Database.InitializeAsync();
        await db.Database.UpsertAccountAsync(Account());
        Assert.True((await db.Database.SnapshotInboxAsync(AccId)).IsInitialSync);
    }
}