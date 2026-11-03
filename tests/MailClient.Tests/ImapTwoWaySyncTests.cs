using MailClient.Core;
using MailClient.Core.Sync;
using MailClient.Providers.Imap;
using MailClient.Storage;

namespace MailClient.Tests;

public class ImapTwoWaySyncTests
{
    private static readonly ImapServerCaps CondStoreOnly = new(CondStore: true, QResync: false, Idle: true, GmailExt: false);
    private static readonly ImapServerCaps WithQResync = new(CondStore: true, QResync: true, Idle: true, GmailExt: false);
    private static readonly ImapServerCaps GmailCaps = new(CondStore: true, QResync: false, Idle: true, GmailExt: true);

    private static FakeImapSource SeedThree(ImapEngineFixture fx, ImapServerCaps? caps)
    {
        var src = fx.Source(caps: caps);
        src.Add(1);
        src.Add(2);
        src.Add(3);
        return src;
    }

    // ───────── 首次同步 / 状态保存 ─────────

    [Fact]
    public async Task Initial_Sync_Stores_Cursor_UidValidity_UidNext_And_ModSeq()
    {
        await using var fx = await ImapEngineFixture.CreateAsync();
        var src = SeedThree(fx, CondStoreOnly);

        var report = await fx.SyncAsync(src);

        Assert.Equal(ImapSyncStrategy.Initial, report.Strategy);
        Assert.Equal(3, report.NewMessages);
        var folder = (await fx.Db.GetFolderAsync(fx.InboxId))!;
        Assert.Equal("3", folder.SyncCursor);
        Assert.Equal(777, folder.UidValidity);
        Assert.Equal(4, folder.UidNext);
        Assert.Equal(3, folder.HighestModSeq);
    }

    [Fact]
    public async Task Server_Without_ModSeq_Leaves_ModSeq_Null()
    {
        await using var fx = await ImapEngineFixture.CreateAsync();
        var src = SeedThree(fx, ImapServerCaps.None);

        await fx.SyncAsync(src);

        Assert.Null((await fx.Db.GetFolderAsync(fx.InboxId))!.HighestModSeq);
    }

    [Fact]
    public async Task Nothing_Changed_Costs_Only_A_Select()
    {
        await using var fx = await ImapEngineFixture.CreateAsync();
        var src = SeedThree(fx, CondStoreOnly);
        await fx.SyncAsync(src);
        src.Calls.Clear();

        var report = await fx.SyncAsync(src);

        Assert.False(report.Changed);
        Assert.Equal(ImapSyncStrategy.CondStore, report.Strategy);
        Assert.Equal(["select"], src.Calls);
    }

    [Fact]
    public async Task New_Mail_Is_Fetched_When_UidNext_Moves()
    {
        await using var fx = await ImapEngineFixture.CreateAsync();
        var src = SeedThree(fx, CondStoreOnly);
        await fx.SyncAsync(src);
        src.Add(4);

        var report = await fx.SyncAsync(src);

        Assert.Equal(1, report.NewMessages);
        Assert.NotNull(await fx.MessageAsync(4));
        Assert.Equal("4", (await fx.Db.GetFolderAsync(fx.InboxId))!.SyncCursor);
    }

    // ───────── CONDSTORE：CHANGEDSINCE ─────────

    [Fact]
    public async Task CondStore_Pulls_Read_And_Flag_Changes_Of_Already_Synced_Mail()
    {
        await using var fx = await ImapEngineFixture.CreateAsync();
        var src = SeedThree(fx, CondStoreOnly);
        await fx.SyncAsync(src);
        src.SetFlags(2, read: true, flagged: true);
        src.Calls.Clear();

        var report = await fx.SyncAsync(src);

        Assert.Equal(1, report.FlagUpdates);
        Assert.Contains("changedsince:3", src.Calls);
        var m = (await fx.MessageAsync(2))!;
        Assert.True(m.IsRead);
        Assert.True(m.IsFlagged);
        Assert.False((await fx.MessageAsync(1))!.IsRead);
        Assert.Equal(4, (await fx.Db.GetFolderAsync(fx.InboxId))!.HighestModSeq);
    }

    [Fact]
    public async Task CondStore_Unread_Again_On_Server_Is_Pulled_Too()
    {
        await using var fx = await ImapEngineFixture.CreateAsync();
        var src = fx.Source(caps: CondStoreOnly);
        src.Add(1, read: true);
        await fx.SyncAsync(src);
        src.SetFlags(1, read: false, flagged: false);

        await fx.SyncAsync(src);

        Assert.False((await fx.MessageAsync(1))!.IsRead);
    }

    [Fact]
    public async Task CondStore_Deletion_Is_Found_By_Uid_Diff_When_ModSeq_Moves()
    {
        await using var fx = await ImapEngineFixture.CreateAsync();
        var src = SeedThree(fx, CondStoreOnly);
        await fx.SyncAsync(src);
        src.Expunge(1);

        var report = await fx.SyncAsync(src);

        Assert.Equal(1, report.Removed);
        Assert.Null(await fx.MessageAsync(1));
        Assert.NotNull(await fx.MessageAsync(2));
    }

    [Fact]
    public async Task CondStore_Silent_Expunge_Is_Found_On_The_Next_Reconcile_Tick_Or_Force()
    {
        await using var fx = await ImapEngineFixture.CreateAsync();
        var src = SeedThree(fx, CondStoreOnly);
        await fx.SyncAsync(src);
        src.Expunge(2, bumpModSeq: false);

        fx.Now += TimeSpan.FromMinutes(1);              // 还没到对账间隔：不查
        src.Calls.Clear();
        var early = await fx.SyncAsync(src);
        Assert.Equal(0, early.Removed);
        Assert.DoesNotContain(src.Calls, c => c.StartsWith("search"));

        var forced = await fx.SyncAsync(src, force: true);   // 刷新按钮
        Assert.Equal(1, forced.Removed);
        Assert.Null(await fx.MessageAsync(2));
    }

    [Fact]
    public async Task CondStore_Silent_Expunge_Found_After_Interval()
    {
        await using var fx = await ImapEngineFixture.CreateAsync();
        var src = SeedThree(fx, CondStoreOnly);
        await fx.SyncAsync(src);
        src.Expunge(3, bumpModSeq: false);

        fx.Now += fx.Options.ReconcileInterval + TimeSpan.FromSeconds(1);
        var report = await fx.SyncAsync(src);

        Assert.Equal(1, report.Removed);
    }

    [Fact]
    public async Task Pre_V4_Folder_With_Cursor_But_No_ModSeq_Upgrades_Gracefully()
    {
        await using var fx = await ImapEngineFixture.CreateAsync();
        var src = SeedThree(fx, CondStoreOnly);
        await fx.SyncAsync(src);
        // 模拟 V4 之前同步过的文件夹：只有 UID 游标
        await fx.Db.SaveFolderSyncStateAsync(new FolderSyncState(fx.InboxId, "3", null, null, null, null));
        src.SetFlags(1, read: true, flagged: false);

        var first = await fx.SyncAsync(src);
        Assert.Equal(ImapSyncStrategy.Reconcile, first.Strategy);
        Assert.True((await fx.MessageAsync(1))!.IsRead);
        Assert.NotNull((await fx.Db.GetFolderAsync(fx.InboxId))!.HighestModSeq);

        Assert.Equal(ImapSyncStrategy.CondStore, (await fx.SyncAsync(src)).Strategy);
    }

    // ───────── QRESYNC ─────────

    [Fact]
    public async Task QResync_Applies_Flag_Changes_And_Vanished_From_The_Select()
    {
        await using var fx = await ImapEngineFixture.CreateAsync();
        var src = SeedThree(fx, WithQResync);
        await fx.SyncAsync(src);
        src.SetFlags(1, read: true, flagged: false);
        src.Expunge(3);
        src.Calls.Clear();

        var report = await fx.SyncAsync(src);

        Assert.Equal(ImapSyncStrategy.QResync, report.Strategy);
        Assert.Equal(1, report.FlagUpdates);
        Assert.Equal(1, report.Removed);
        Assert.True((await fx.MessageAsync(1))!.IsRead);
        Assert.Null(await fx.MessageAsync(3));
        Assert.Equal(["qresync:777:3"], src.Calls.Where(c => c.StartsWith("qresync")).ToList());
        // QRESYNC 已经给出了全部变化：不需要再 FETCH CHANGEDSINCE / SEARCH
        Assert.DoesNotContain(src.Calls, c => c.StartsWith("changedsince") || c.StartsWith("search"));
    }

    [Fact]
    public async Task QResync_Failure_Degrades_To_CondStore()
    {
        await using var fx = await ImapEngineFixture.CreateAsync();
        var src = SeedThree(fx, WithQResync);
        await fx.SyncAsync(src);
        src.QResyncThrows = true;
        src.SetFlags(2, read: true, flagged: false);
        src.Expunge(1);

        var report = await fx.SyncAsync(src);

        Assert.Equal(ImapSyncStrategy.CondStore, report.Strategy);
        Assert.True((await fx.MessageAsync(2))!.IsRead);
        Assert.Null(await fx.MessageAsync(1));
        Assert.Contains(fx.Log.Lines, l => l.Contains("QRESYNC"));
    }

    [Fact]
    public async Task QResync_Is_Not_Used_When_The_Connection_Did_Not_Enable_It()
    {
        await using var fx = await ImapEngineFixture.CreateAsync();
        var src = SeedThree(fx, CondStoreOnly);     // Gmail 一类：有 CONDSTORE，没有 QRESYNC
        await fx.SyncAsync(src);
        src.Calls.Clear();

        await fx.SyncAsync(src);

        Assert.DoesNotContain(src.Calls, c => c.StartsWith("qresync"));
    }

    // ───────── 无 CONDSTORE：窗口对账 ─────────

    [Fact]
    public async Task Reconcile_Pulls_Flags_And_Deletions_Without_CondStore()
    {
        await using var fx = await ImapEngineFixture.CreateAsync();
        var src = SeedThree(fx, ImapServerCaps.None);
        await fx.SyncAsync(src);
        src.SetFlags(2, read: true, flagged: true);
        src.Expunge(3);
        fx.Now += fx.Options.ReconcileInterval + TimeSpan.FromSeconds(1);

        var report = await fx.SyncAsync(src);

        Assert.Equal(ImapSyncStrategy.Reconcile, report.Strategy);
        Assert.Equal(1, report.FlagUpdates);
        Assert.Equal(1, report.Removed);
        Assert.True((await fx.MessageAsync(2))!.IsFlagged);
        Assert.Null(await fx.MessageAsync(3));
    }

    [Fact]
    public async Task Reconcile_Is_Throttled_Unless_Forced()
    {
        await using var fx = await ImapEngineFixture.CreateAsync();
        var src = SeedThree(fx, ImapServerCaps.None);
        await fx.SyncAsync(src);
        fx.Now += fx.Options.ReconcileInterval + TimeSpan.FromSeconds(1);
        await fx.SyncAsync(src);                       // 对账一次，记下时间

        src.SetFlags(1, read: true, flagged: false);
        src.Calls.Clear();
        fx.Now += TimeSpan.FromSeconds(30);
        await fx.SyncAsync(src);
        Assert.DoesNotContain(src.Calls, c => c.StartsWith("uidflags"));
        Assert.False((await fx.MessageAsync(1))!.IsRead);

        await fx.SyncAsync(src, force: true);
        Assert.Contains(src.Calls, c => c.StartsWith("uidflags"));
        Assert.True((await fx.MessageAsync(1))!.IsRead);
    }

    [Fact]
    public async Task Reconcile_Only_Looks_At_The_Configured_Window()
    {
        var options = new SyncOptions(TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(5), WindowMaxMessages: 2, WindowMaxAge: TimeSpan.FromDays(1), IdleEnabled: true);
        await using var fx = await ImapEngineFixture.CreateAsync(options);
        var src = fx.Source(caps: ImapServerCaps.None);
        var old = SendFixture.T0.AddDays(-40);
        for (var uid = 1; uid <= 5; uid++)
        {
            src.Add(uid, received: old);
        }

        await fx.SyncAsync(src);
        src.Expunge(1);       // 窗口之外：不检查
        src.Expunge(5);       // 窗口之内（最新 2 封）
        src.SetFlags(2, read: true, flagged: false);   // 窗口之外：不拉
        src.SetFlags(4, read: true, flagged: false);
        src.Calls.Clear();

        var report = await fx.SyncAsync(src, force: true);

        Assert.Contains("uidflags:4", src.Calls);
        Assert.Equal(1, report.Removed);
        Assert.NotNull(await fx.MessageAsync(1));
        Assert.Null(await fx.MessageAsync(5));
        Assert.False((await fx.MessageAsync(2))!.IsRead);
        Assert.True((await fx.MessageAsync(4))!.IsRead);
    }

    [Fact]
    public async Task Window_Is_Union_Of_Count_And_Age()
    {
        var options = new SyncOptions(TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(5), WindowMaxMessages: 1, WindowMaxAge: TimeSpan.FromDays(30), IdleEnabled: true);
        await using var fx = await ImapEngineFixture.CreateAsync(options);
        var src = fx.Source(caps: ImapServerCaps.None);
        src.Add(1, received: SendFixture.T0.AddDays(-90));
        src.Add(2, received: SendFixture.T0.AddDays(-3));      // 30 天内
        src.Add(3, received: SendFixture.T0.AddDays(-1));
        await fx.SyncAsync(src);
        src.Calls.Clear();

        await fx.SyncAsync(src, force: true);

        Assert.Contains("uidflags:2", src.Calls);     // 最新 1 封是 UID 3，但 30 天内的 UID 2 也在窗口里
    }

    [Fact]
    public async Task A_Bogus_Empty_Listing_Does_Not_Wipe_The_Local_Window()
    {
        await using var fx = await ImapEngineFixture.CreateAsync();
        var src = SeedThree(fx, ImapServerCaps.None);
        await fx.SyncAsync(src);
        src.ForceEmptyListing = true;

        var report = await fx.SyncAsync(src, force: true);

        Assert.Equal(0, report.Removed);
        Assert.NotNull(await fx.MessageAsync(1));
        Assert.Contains(fx.Log.Lines, l => l.Contains("EXISTS"));
    }

    // ───────── UIDVALIDITY ─────────

    [Fact]
    public async Task UidValidity_Change_Drops_Local_Copy_Discards_Old_Pending_Ops_And_Resyncs()
    {
        await using var fx = await ImapEngineFixture.CreateAsync();
        var src = SeedThree(fx, CondStoreOnly);
        await fx.SyncAsync(src);
        await fx.Db.ApplyMessageChangeAsync((await fx.MessageAsync(2))!.Id, PendingOpKind.MarkRead);
        Assert.Single(await fx.Db.ListPendingOpsAsync());

        // 服务器重建了邮箱：新的 UIDVALIDITY，UID 从头编号
        src.UidValidity = 999;
        src.Messages.Clear();
        src.Add(1, read: true);
        src.Add(2);

        var report = await fx.SyncAsync(src);

        Assert.True(report.UidValidityReset);
        Assert.Equal(3, report.Removed);
        Assert.Equal(2, report.NewMessages);
        Assert.Empty(await fx.Db.ListPendingOpsAsync());
        Assert.Contains(fx.Log.Lines, l => l.Contains("UIDVALIDITY") && l.Contains("777") && l.Contains("999"));
        Assert.Contains(fx.Log.Lines, l => l.Contains("丢弃"));
        var folder = (await fx.Db.GetFolderAsync(fx.InboxId))!;
        Assert.Equal(999, folder.UidValidity);
        Assert.Equal("2", folder.SyncCursor);
        Assert.True((await fx.MessageAsync(1))!.IsRead);
        Assert.False((await fx.MessageAsync(2))!.IsRead);          // 旧 UID 2 上的“已读”操作已丢弃，不会误伤新邮件
    }

    [Fact]
    public async Task Same_UidValidity_Does_Not_Reset()
    {
        await using var fx = await ImapEngineFixture.CreateAsync();
        var src = SeedThree(fx, CondStoreOnly);
        await fx.SyncAsync(src);

        Assert.False((await fx.SyncAsync(src, force: true)).UidValidityReset);
        Assert.NotNull(await fx.MessageAsync(1));
    }

    // ───────── 冲突规则 ─────────

    [Fact]
    public async Task Pending_Local_Read_Wins_Over_Server_Snapshot_But_Other_Fields_Follow_Server()
    {
        await using var fx = await ImapEngineFixture.CreateAsync();
        var src = SeedThree(fx, CondStoreOnly);
        await fx.SyncAsync(src);
        await fx.Db.ApplyMessageChangeAsync((await fx.MessageAsync(1))!.Id, PendingOpKind.MarkRead);
        src.SetFlags(1, read: false, flagged: true);       // 服务器：仍未读，但被别处加了星标

        await fx.SyncAsync(src);

        var m = (await fx.MessageAsync(1))!;
        Assert.True(m.IsRead);         // 有未写回的已读：本地胜出
        Assert.True(m.IsFlagged);      // 没有未写回的星标：服务器胜出
    }

    [Fact]
    public async Task After_The_Op_Is_Acked_A_Stale_Snapshot_Does_Not_Flip_It_Back_But_A_Later_One_Does()
    {
        await using var fx = await ImapEngineFixture.CreateAsync();
        var src = SeedThree(fx, CondStoreOnly);
        await fx.SyncAsync(src);
        var id = (await fx.MessageAsync(1))!.Id;
        await fx.Db.ApplyMessageChangeAsync(id, PendingOpKind.MarkRead);
        await fx.Db.CompletePendingOpAsync(Assert.Single(await fx.Db.ListPendingOpsAsync()));      // 写回成功
        var staleRows = new[] { new ServerFlagState(1, false, false) };

        // 快照早于确认时刻（请求在写回之前发出）：保持本地
        await fx.Db.ApplyServerFlagsAsync(fx.InboxId, staleRows, DateTimeOffset.UtcNow.AddMinutes(-10));
        Assert.True((await fx.Db.GetMessageAsync(id))!.IsRead);

        // 快照晚于确认时刻：服务器胜出
        await fx.Db.ApplyServerFlagsAsync(fx.InboxId, staleRows, DateTimeOffset.UtcNow.AddMinutes(10));
        Assert.False((await fx.Db.GetMessageAsync(id))!.IsRead);
    }

    [Fact]
    public async Task Server_Delete_Of_A_Message_With_A_Pending_Op_Drops_The_Op()
    {
        await using var fx = await ImapEngineFixture.CreateAsync();
        var src = SeedThree(fx, CondStoreOnly);
        await fx.SyncAsync(src);
        await fx.Db.ApplyMessageChangeAsync((await fx.MessageAsync(1))!.Id, PendingOpKind.Flag);
        src.Expunge(1);

        var report = await fx.SyncAsync(src);

        Assert.Equal(1, report.Removed);
        Assert.Empty(await fx.Db.ListPendingOpsAsync());       // 邮件已经没了，写回只会失败
    }

    [Fact]
    public async Task Unread_Counts_Follow_Server_Changes()
    {
        await using var fx = await ImapEngineFixture.CreateAsync();
        var src = SeedThree(fx, CondStoreOnly);
        await fx.SyncAsync(src);
        Assert.Equal(3, (await fx.Db.GetFolderAsync(fx.InboxId))!.UnreadCount);

        src.SetFlags(1, read: true, flagged: false);
        src.Expunge(2);
        await fx.SyncAsync(src);

        var folder = (await fx.Db.GetFolderAsync(fx.InboxId))!;
        Assert.Equal(1, folder.UnreadCount);
        Assert.Equal(2, folder.TotalCount);
    }

    // ───────── Gmail ─────────

    [Fact]
    public async Task Gmail_Same_MsgId_In_Inbox_And_AllMail_Keeps_One_Row_And_One_Unread()
    {
        await using var fx = await ImapEngineFixture.CreateAsync(providerId: "gmail");
        await fx.Db.UpsertFolderAsync(new FolderEntity(fx.Fx.FolderId("All"), fx.Fx.AccountId, "所有邮件", SpecialFolder.Archive, "All", 0, 0, 9, null, 777));
        var inbox = fx.Source(caps: GmailCaps);
        inbox.Add(1, gmMsgId: "G100");
        var all = fx.Source("All", GmailCaps);
        all.Add(7, gmMsgId: "G100");      // 同一封邮件，也带“所有邮件”标签
        all.Add(8, gmMsgId: "G200");      // 已归档的另一封

        await fx.SyncAsync(inbox);
        var report = await fx.SyncAsync(all, "All");

        Assert.Equal(1, report.GmailDuplicatesMerged);
        Assert.NotNull(await fx.MessageAsync(1));                   // 收件箱副本留下
        Assert.Null(await fx.MessageAsync(7, "All"));
        Assert.NotNull(await fx.MessageAsync(8, "All"));
        Assert.Equal(1, (await fx.Db.GetFolderAsync(fx.InboxId))!.UnreadCount);
        Assert.Equal(1, (await fx.Db.GetFolderAsync(fx.Fx.FolderId("All")))!.UnreadCount);     // 只剩 G200，没有双计
    }

    [Fact]
    public async Task Gmail_Dedupe_Keeps_The_Copy_That_Has_A_Pending_Op()
    {
        var copies = new[]
        {
            new GmailCopyCandidate("a", SpecialFolder.Inbox, false),
            new GmailCopyCandidate("b", SpecialFolder.Archive, true),
        };

        Assert.Equal("b", GmailDedupe.PickKeeper(copies).MessageId);
        await Task.CompletedTask;
    }

    [Theory]
    [InlineData(SpecialFolder.Inbox, SpecialFolder.Archive, SpecialFolder.Inbox)]
    [InlineData(SpecialFolder.Archive, SpecialFolder.Trash, SpecialFolder.Trash)]
    [InlineData(SpecialFolder.Sent, SpecialFolder.Inbox, SpecialFolder.Inbox)]
    [InlineData(SpecialFolder.Custom, SpecialFolder.Archive, SpecialFolder.Archive)]
    public void Gmail_Keeper_Priority(SpecialFolder a, SpecialFolder b, SpecialFolder expected)
    {
        var keeper = GmailDedupe.PickKeeper([new GmailCopyCandidate("1", a, false), new GmailCopyCandidate("2", b, false)]);
        Assert.Equal(expected, keeper.Folder);
    }

    [Fact]
    public async Task Gmail_Label_Move_Out_Of_Inbox_Shows_Up_As_Removal_Then_Archive_Copy_Is_Not_Duplicated()
    {
        await using var fx = await ImapEngineFixture.CreateAsync(providerId: "gmail");
        await fx.Db.UpsertFolderAsync(new FolderEntity(fx.Fx.FolderId("All"), fx.Fx.AccountId, "所有邮件", SpecialFolder.Archive, "All", 0, 0, 9, null, 777));
        var inbox = fx.Source(caps: GmailCaps);
        inbox.Add(1, gmMsgId: "G1");
        var all = fx.Source("All", GmailCaps);
        all.Add(5, gmMsgId: "G1");
        await fx.SyncAsync(inbox);
        await fx.SyncAsync(all, "All");

        inbox.Expunge(1);           // 在网页上“归档”：去掉 Inbox 标签
        var report = await fx.SyncAsync(inbox);

        Assert.Equal(0, report.GmailDuplicatesMerged);
        Assert.Null(await fx.MessageAsync(1));
        Assert.Null(await fx.MessageAsync(5, "All"));      // 只剩的那份已在第一次去重时合并掉了
    }

    // ───────── 纯函数 ─────────

    private static LocalUidRow Row(long uid, bool read = false, DateTimeOffset? at = null)
        => new($"m{uid}", uid, read, false, null, at ?? SendFixture.T0);

    [Fact]
    public void SelectWindow_Takes_Newest_N_Or_Anything_Recent()
    {
        var rows = Enumerable.Range(1, 10).Select(i => Row(i, at: SendFixture.T0.AddDays(-100))).ToList();
        rows[2] = Row(3, at: SendFixture.T0.AddDays(-2));      // UID 3 很新

        var window = ImapDiff.SelectWindow(rows, SendFixture.T0, 2, TimeSpan.FromDays(30));

        Assert.Equal(Enumerable.Range(3, 8).Select(i => (long)i), window.Select(r => r.Uid));
    }

    [Fact]
    public void SelectWindow_Empty_And_Small_Folders()
    {
        Assert.Empty(ImapDiff.SelectWindow([], SendFixture.T0, 10, TimeSpan.FromDays(1)));
        Assert.Equal(2, ImapDiff.SelectWindow([Row(1), Row(2)], SendFixture.T0, 2000, TimeSpan.FromDays(30)).Count);
    }

    [Fact]
    public void FindMissing_And_ChangedFlags()
    {
        var window = new[] { Row(1), Row(2, read: true), Row(3) };
        Assert.Equal([2L], ImapDiff.FindMissing(window, new long[] { 1, 3, 9 }));
        var changed = ImapDiff.ChangedFlags(window, [new ServerFlagState(1, false, false), new ServerFlagState(2, false, false), new ServerFlagState(77, true, true)]);
        Assert.Equal([2L], changed.Select(c => c.Uid));
    }

    [Theory]
    [InlineData(false, 0, true)]
    [InlineData(false, 4, false)]
    [InlineData(false, 6, true)]
    [InlineData(true, 1, true)]
    public void ReconcileDue_Throttle(bool force, int minutesSinceLast, bool expected)
        => Assert.Equal(expected, ImapDiff.ReconcileDue(minutesSinceLast == 0 && !force ? null : SendFixture.T0, SendFixture.T0.AddMinutes(minutesSinceLast), TimeSpan.FromMinutes(5), force));

    [Fact]
    public void ReconcileDue_Handles_Clock_Going_Backwards()
        => Assert.True(ImapDiff.ReconcileDue(SendFixture.T0, SendFixture.T0.AddMinutes(-3), TimeSpan.FromMinutes(5), false));

    [Theory]
    [InlineData(0, 100, 50, true)]
    [InlineData(0, 0, 50, false)]
    [InlineData(0, 100, 0, false)]
    [InlineData(5, 100, 50, false)]
    public void Bogus_Empty_Listing_Detection(int serverCount, int exists, int window, bool expected)
        => Assert.Equal(expected, ImapDiff.LooksLikeBogusEmptyListing(serverCount, exists, window));
}
