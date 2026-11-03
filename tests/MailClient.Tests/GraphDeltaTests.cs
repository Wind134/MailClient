using MailClient.Core;
using MailClient.Core.Services;
using MailClient.Core.Sync;
using MailClient.Providers.Graph;
using MailClient.Storage;

namespace MailClient.Tests;

public class GraphDeltaTests
{
    private const string Delta1 = "https://graph.microsoft.com/v1.0/me/mailFolders/inbox/messages/delta?$deltatoken=ONE";
    private const string Delta2 = "https://graph.microsoft.com/v1.0/me/mailFolders/inbox/messages/delta?$deltatoken=TWO";

    private static GraphSyncPipeline Pipeline(SendFixture fx, MemorySyncLog log) => new(fx.Db, SyncOptions.Default, log, () => SendFixture.T0);

    private static async Task<(GraphSyncPipeline P, FakeGraphSource S, SendFixture Fx, MemorySyncLog Log)> StartAsync()
    {
        var fx = await SendFixture.CreateAsync("microsoft.graph");
        var log = new MemorySyncLog();
        var s = new FakeGraphSource();
        return (Pipeline(fx, log), s, fx, log);
    }

    private static string Id(SendFixture fx, string graphId) => $"{fx.AccountId:D}:{graphId}";

    [Fact]
    public async Task First_Sync_Stores_Messages_And_The_DeltaLink()
    {
        var (p, s, fx, _) = await StartAsync();
        await using var _fx = fx;
        s.Responses.Enqueue(new GraphDeltaResult([FakeGraphSource.Full(fx.AccountId, "a"), FakeGraphSource.Full(fx.AccountId, "b")], Delta1));

        var report = await p.SyncFolderAsync(s, fx.AccountId, "INBOX", 100);

        Assert.Equal(2, report.NewMessages);
        Assert.True(report.HasDeltaLink);
        Assert.Equal(Delta1, (await fx.Db.GetFolderAsync(fx.FolderId("INBOX")))!.SyncCursor);
        Assert.Null(s.Calls[0].DeltaLink);
        Assert.NotNull(s.Calls[0].Since);
    }

    [Fact]
    public async Task Next_Sync_Uses_The_Stored_DeltaLink_And_Rotates_It()
    {
        var (p, s, fx, _) = await StartAsync();
        await using var _fx = fx;
        s.Responses.Enqueue(new GraphDeltaResult([FakeGraphSource.Full(fx.AccountId, "a")], Delta1));
        s.Responses.Enqueue(new GraphDeltaResult([], Delta2));
        await p.SyncFolderAsync(s, fx.AccountId, "INBOX", 100);

        var report = await p.SyncFolderAsync(s, fx.AccountId, "INBOX", 100);

        Assert.False(report.Changed);
        Assert.Equal(Delta1, s.Calls[1].DeltaLink);
        Assert.Null(s.Calls[1].Since);
        Assert.Equal(Delta2, (await fx.Db.GetFolderAsync(fx.FolderId("INBOX")))!.SyncCursor);
    }

    [Fact]
    public async Task Removed_Entries_Delete_Local_Messages_Deleted_Or_Moved_Out()
    {
        var (p, s, fx, _) = await StartAsync();
        await using var _fx = fx;
        s.Responses.Enqueue(new GraphDeltaResult([FakeGraphSource.Full(fx.AccountId, "a"), FakeGraphSource.Full(fx.AccountId, "b"), FakeGraphSource.Full(fx.AccountId, "c")], Delta1));
        s.Responses.Enqueue(new GraphDeltaResult([FakeGraphSource.Removed("b"), FakeGraphSource.Removed("never-seen")], Delta2));
        await p.SyncFolderAsync(s, fx.AccountId, "INBOX", 100);

        var report = await p.SyncFolderAsync(s, fx.AccountId, "INBOX", 100);

        Assert.Equal(1, report.Removed);
        Assert.Null(await fx.Db.GetMessageAsync(Id(fx, "b")));
        Assert.NotNull(await fx.Db.GetMessageAsync(Id(fx, "a")));
        Assert.Equal(2, (await fx.Db.GetFolderAsync(fx.FolderId("INBOX")))!.TotalCount);
    }

    [Fact]
    public async Task Partial_Updates_Change_Read_And_Flag_Without_Touching_Other_Fields()
    {
        var (p, s, fx, _) = await StartAsync();
        await using var _fx = fx;
        s.Responses.Enqueue(new GraphDeltaResult([FakeGraphSource.Full(fx.AccountId, "a")], Delta1));
        s.Responses.Enqueue(new GraphDeltaResult([FakeGraphSource.Partial("a", read: true)], Delta2));
        await p.SyncFolderAsync(s, fx.AccountId, "INBOX", 100);

        var report = await p.SyncFolderAsync(s, fx.AccountId, "INBOX", 100);

        Assert.Equal(1, report.FlagUpdates);
        var m = (await fx.Db.GetMessageAsync(Id(fx, "a")))!;
        Assert.True(m.IsRead);
        Assert.False(m.IsFlagged);
        Assert.Equal("主题 a", m.Subject);
    }

    [Fact]
    public async Task Full_Item_Updates_Are_Counted_As_Changes_So_The_UI_Refreshes()
    {
        var (p, s, fx, _) = await StartAsync();
        await using var _fx = fx;
        s.Responses.Enqueue(new GraphDeltaResult([FakeGraphSource.Full(fx.AccountId, "a")], Delta1));
        s.Responses.Enqueue(new GraphDeltaResult([FakeGraphSource.Full(fx.AccountId, "a", read: true, flagged: true)], Delta2));
        await p.SyncFolderAsync(s, fx.AccountId, "INBOX", 100);

        var report = await p.SyncFolderAsync(s, fx.AccountId, "INBOX", 100);

        Assert.True(report.Changed);
        Assert.Equal(1, report.FlagUpdates);
        Assert.Equal(0, report.NewMessages);
        Assert.True((await fx.Db.GetMessageAsync(Id(fx, "a")))!.IsFlagged);
    }

    [Fact]
    public async Task Pending_Local_Change_Wins_Over_A_Delta_Update()
    {
        var (p, s, fx, _) = await StartAsync();
        await using var _fx = fx;
        s.Responses.Enqueue(new GraphDeltaResult([FakeGraphSource.Full(fx.AccountId, "a")], Delta1));
        s.Responses.Enqueue(new GraphDeltaResult([FakeGraphSource.Full(fx.AccountId, "a", read: false, flagged: true)], Delta2));
        await p.SyncFolderAsync(s, fx.AccountId, "INBOX", 100);
        await fx.Db.ApplyMessageChangeAsync(Id(fx, "a"), PendingOpKind.MarkRead);

        await p.SyncFolderAsync(s, fx.AccountId, "INBOX", 100);

        var m = (await fx.Db.GetMessageAsync(Id(fx, "a")))!;
        Assert.True(m.IsRead);          // 未写回的已读：本地胜出
        Assert.True(m.IsFlagged);       // 星标没有待写回：服务器胜出
    }

    [Fact]
    public async Task Gone_410_Triggers_A_Full_Resync_And_Prunes_What_The_Server_No_Longer_Has()
    {
        var (p, s, fx, log) = await StartAsync();
        await using var _fx = fx;
        s.Responses.Enqueue(new GraphDeltaResult([FakeGraphSource.Full(fx.AccountId, "a"), FakeGraphSource.Full(fx.AccountId, "b"), FakeGraphSource.Full(fx.AccountId, "c")], Delta1));
        s.Responses.Enqueue(new GraphSyncStateNotFoundException("syncStateNotFound"));
        s.Responses.Enqueue(new GraphDeltaResult([FakeGraphSource.Full(fx.AccountId, "a", read: true), FakeGraphSource.Full(fx.AccountId, "c")], Delta2));
        await p.SyncFolderAsync(s, fx.AccountId, "INBOX", 100);

        var report = await p.SyncFolderAsync(s, fx.AccountId, "INBOX", 100);

        Assert.True(report.FullResync);
        Assert.Equal(1, report.Removed);            // b 在服务器上已经没有了
        Assert.Null(await fx.Db.GetMessageAsync(Id(fx, "b")));
        Assert.True((await fx.Db.GetMessageAsync(Id(fx, "a")))!.IsRead);
        Assert.Equal(Delta1, s.Calls[1].DeltaLink);        // 先试旧 token
        Assert.Null(s.Calls[2].DeltaLink);                 // 失败后无 token 重来
        Assert.Equal(Delta2, (await fx.Db.GetFolderAsync(fx.FolderId("INBOX")))!.SyncCursor);
        Assert.Contains(log.Lines, l => l.Contains("410"));
    }

    [Fact]
    public async Task Full_Resync_Keeps_Local_Only_Rows_And_Messages_Older_Than_The_Window()
    {
        var (p, s, fx, _) = await StartAsync();
        await using var _fx = fx;
        await fx.AddMessageAsync("INBOX", 1);       // 旧窗口外的邮件（T0 之前很久）
        var old = fx.Message("INBOX", 2) with { ReceivedAt = SendFixture.T0.AddDays(-400) };
        await fx.Db.UpsertMessagesAsync([old]);
        s.Responses.Enqueue(new GraphDeltaResult([FakeGraphSource.Full(fx.AccountId, "a")], Delta1));
        s.Responses.Enqueue(new GraphSyncStateNotFoundException("gone"));
        s.Responses.Enqueue(new GraphDeltaResult([FakeGraphSource.Full(fx.AccountId, "a")], Delta2));
        await p.SyncFolderAsync(s, fx.AccountId, "INBOX", 100);

        await p.SyncFolderAsync(s, fx.AccountId, "INBOX", 100);

        Assert.NotNull(await fx.Db.GetMessageAsync(old.Id));          // 窗口之外：不能因为这次没拉到就删
    }

    [Fact]
    public async Task Resync_Interrupted_By_Failure_Leaves_Local_Mail_Untouched()
    {
        var (p, s, fx, _) = await StartAsync();
        await using var _fx = fx;
        s.Responses.Enqueue(new GraphDeltaResult([FakeGraphSource.Full(fx.AccountId, "a")], Delta1));
        s.Responses.Enqueue(new HttpRequestException("offline"));
        await p.SyncFolderAsync(s, fx.AccountId, "INBOX", 100);

        await Assert.ThrowsAsync<HttpRequestException>(() => p.SyncFolderAsync(s, fx.AccountId, "INBOX", 100));

        Assert.NotNull(await fx.Db.GetMessageAsync(Id(fx, "a")));
        Assert.Equal(Delta1, (await fx.Db.GetFolderAsync(fx.FolderId("INBOX")))!.SyncCursor);
    }
}
