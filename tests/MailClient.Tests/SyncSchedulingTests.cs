using MailClient.Core;
using MailClient.Core.Services;
using MailClient.Core.Sync;
using MailClient.Providers;
using MailClient.Providers.Sync;
using MailClient.Storage;

namespace MailClient.Tests;

public class SyncSchedulingTests
{
    private static readonly DateTimeOffset T = SendFixture.T0;

    // ───────── 退避 ─────────

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 5)]
    [InlineData(2, 10)]
    [InlineData(3, 20)]
    [InlineData(8, 640)]
    [InlineData(9, 900)]
    [InlineData(500, 900)]
    public void Backoff_Grows_Exponentially_And_Caps(int failures, int expectedSeconds)
        => Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), SyncBackoff.DelayFor(failures, TimeSpan.FromSeconds(5), TimeSpan.FromMinutes(15)));

    [Fact]
    public void Backoff_Success_Resets()
    {
        var b = new SyncBackoff();
        b.Failure();
        b.Failure();
        Assert.Equal(2, b.Failures);
        b.Success();
        Assert.Equal(0, b.Failures);
        Assert.Equal(TimeSpan.FromSeconds(5), b.Failure());
    }

    // ───────── 调度器 ─────────

    private static (SyncScheduler S, Func<DateTimeOffset> Clock, Action<TimeSpan> Advance) NewScheduler(SyncOptions? options = null)
    {
        var now = T;
        var s = new SyncScheduler(options ?? SyncOptions.Default, () => now);
        return (s, () => now, d => now += d);
    }

    [Fact]
    public void Starts_With_A_Poll_Right_Away_Then_Waits_For_The_Interval()
    {
        var (s, _, advance) = NewScheduler();

        var first = Assert.Single(s.TakeDue());
        Assert.Null(first.AccountId);
        Assert.True(first.Request.Background);
        Assert.False(first.Request.Force);

        Assert.Empty(s.TakeDue());
        advance(TimeSpan.FromMinutes(4));
        Assert.Empty(s.TakeDue());
        advance(TimeSpan.FromMinutes(1));
        Assert.Single(s.TakeDue());
    }

    [Fact]
    public void Idle_Notification_Syncs_Only_That_Accounts_Inbox_Forced()
    {
        var (s, _, _) = NewScheduler();
        s.TakeDue();
        var id = Guid.NewGuid();
        s.RequestInbox(id);
        s.RequestInbox(id);

        var job = Assert.Single(s.TakeDue());

        Assert.Equal(id, job.AccountId);
        Assert.True(job.Request.InboxOnly);
        Assert.True(job.Request.Force);
        Assert.Empty(s.TakeDue());
    }

    [Fact]
    public void Full_Request_Wins_And_Swallows_Pending_Inbox_Requests()
    {
        var (s, _, _) = NewScheduler();
        s.TakeDue();
        s.RequestInbox(Guid.NewGuid());
        s.RequestFull(force: true);

        var job = Assert.Single(s.TakeDue());

        Assert.Null(job.AccountId);
        Assert.True(job.Request.Force);
        Assert.Empty(s.TakeDue());
    }

    [Fact]
    public void Offline_Pauses_And_Coming_Back_Triggers_An_Immediate_Round()
    {
        var (s, _, advance) = NewScheduler();
        s.TakeDue();
        s.SetOnline(false);
        s.RequestInbox(Guid.NewGuid());
        advance(TimeSpan.FromMinutes(30));

        Assert.True(s.IsPaused);
        Assert.Empty(s.TakeDue());
        Assert.Null(s.NextDue());

        s.SetOnline(true);

        Assert.False(s.IsPaused);
        Assert.Single(s.TakeDue());       // 全量一轮；其中已包含被保留的收件箱请求
    }

    [Fact]
    public void Suspend_Resume_Behaves_Like_Offline()
    {
        var (s, _, advance) = NewScheduler();
        s.TakeDue();
        s.SetSuspended(true);
        advance(TimeSpan.FromMinutes(10));
        Assert.Empty(s.TakeDue());

        s.SetSuspended(false);
        Assert.Single(s.TakeDue());
    }

    [Fact]
    public void NextDue_Reports_The_Poll_Time_Or_Now_When_Work_Is_Pending()
    {
        var (s, _, _) = NewScheduler();
        s.TakeDue();
        Assert.Equal(T + TimeSpan.FromMinutes(5), s.NextDue());
        s.RequestInbox(Guid.NewGuid());
        Assert.Equal(T, s.NextDue());
    }

    [Fact]
    public void Shorter_Poll_Interval_Takes_Effect_Without_Waiting_For_The_Old_One()
    {
        var (s, _, advance) = NewScheduler();
        s.TakeDue();
        s.UpdateOptions(SyncOptions.FromSettings(1, null, null, null));
        advance(TimeSpan.FromMinutes(1));
        Assert.Single(s.TakeDue());
    }

    // ───────── IDLE 看管 ─────────

    private static ImapIdleSupervisor Supervisor(Queue<object> outcomes, List<TimeSpan> waits, CancellationTokenSource cts, MemorySyncLog? log = null)
        => new("test", (_, _) =>
            {
                if (outcomes.Count == 0)
                {
                    cts.Cancel();
                    return Task.FromResult(IdleOutcome.Ended);
                }

                var next = outcomes.Dequeue();
                if (next is Exception ex)
                {
                    throw ex;
                }

                return Task.FromResult((IdleOutcome)next);
            },
            _ => Task.CompletedTask,
            new SyncBackoff(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(60)),
            log,
            (delay, _) =>
            {
                waits.Add(delay);
                return Task.CompletedTask;
            });

    [Fact]
    public async Task Idle_Failures_Back_Off_And_A_Success_Resets()
    {
        var cts = new CancellationTokenSource();
        var waits = new List<TimeSpan>();
        var outcomes = new Queue<object>([new IOException("a"), new IOException("b"), new IOException("c"), IdleOutcome.Ended, new IOException("d")]);
        var log = new MemorySyncLog();
        await using var sup = Supervisor(outcomes, waits, cts, log);

        await sup.LoopAsync(cts.Token);

        Assert.Equal([5, 10, 20, 1, 5, 1], waits.Select(w => (int)w.TotalSeconds));
        Assert.Equal(0, sup.ConsecutiveFailures);     // 最后一次正常结束清零了退避
        Assert.False(sup.Unsupported);
        Assert.Contains(log.Lines, l => l.Contains("第 3 次"));
    }

    [Fact]
    public async Task Idle_Backoff_Is_Capped()
    {
        var cts = new CancellationTokenSource();
        var waits = new List<TimeSpan>();
        var outcomes = new Queue<object>(Enumerable.Range(0, 8).Select(_ => (object)new IOException("x")));
        await using var sup = Supervisor(outcomes, waits, cts);

        await sup.LoopAsync(cts.Token);

        Assert.Equal(60, (int)waits.Max().TotalSeconds);
    }

    [Fact]
    public async Task Idle_Unsupported_Stops_The_Loop_Without_Retrying()
    {
        var cts = new CancellationTokenSource();
        var waits = new List<TimeSpan>();
        var outcomes = new Queue<object>([IdleOutcome.Unsupported]);
        var log = new MemorySyncLog();
        await using var sup = Supervisor(outcomes, waits, cts, log);

        await sup.LoopAsync(cts.Token);

        Assert.True(sup.Unsupported);
        Assert.Empty(waits);
        Assert.Contains(log.Lines, l => l.Contains("不支持 IDLE"));
    }

    [Fact]
    public async Task Idle_Loop_Stops_On_Cancellation()
    {
        using var cts = new CancellationTokenSource();
        var runs = 0;
        await using var sup = new ImapIdleSupervisor("t", (_, ct) =>
        {
            runs++;
            cts.Cancel();
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(IdleOutcome.Ended);
        }, _ => Task.CompletedTask);

        await sup.LoopAsync(cts.Token);

        Assert.Equal(1, runs);
    }

    // ───────── 设置 → 选项 ─────────

    [Fact]
    public void Options_From_Settings_Clamp_Out_Of_Range_Values()
    {
        var o = SyncOptions.FromSettings(0, 5, 100000, false);
        Assert.Equal(TimeSpan.FromMinutes(1), o.PollInterval);
        Assert.Equal(50, o.WindowMaxMessages);
        Assert.Equal(TimeSpan.FromDays(3650), o.WindowMaxAge);
        Assert.False(o.IdleEnabled);

        var d = SyncOptions.FromSettings(null, null, null, null);
        Assert.Equal(SyncOptions.Default, d);
    }

    // ───────── 迁移 V4 ─────────

    [Fact]
    public async Task Folder_Sync_State_And_Gmail_Ids_Round_Trip_Through_V4_Columns()
    {
        await using var fx = await SendFixture.CreateAsync();
        var folderId = fx.FolderId("INBOX");

        await fx.Db.SaveFolderSyncStateAsync(new FolderSyncState(folderId, "42", 777, 43, 9001, T));

        var f = (await fx.Db.GetFolderAsync(folderId))!;
        Assert.Equal("42", f.SyncCursor);
        Assert.Equal(777, f.UidValidity);
        Assert.Equal(43, f.UidNext);
        Assert.Equal(9001, f.HighestModSeq);
        Assert.Equal(T, f.LastReconcileAt);
    }

    [Fact]
    public async Task Saving_Sync_State_Does_Not_Touch_Counts()
    {
        await using var fx = await SendFixture.CreateAsync();
        await fx.AddMessageAsync("INBOX", 1);
        var folderId = fx.FolderId("INBOX");

        await fx.Db.SaveFolderSyncStateAsync(new FolderSyncState(folderId, "1", 777, 2, null, null));

        var f = (await fx.Db.GetFolderAsync(folderId))!;
        Assert.Equal(1, f.TotalCount);
        Assert.Equal(1, f.UnreadCount);
    }

    // ───────── 冲突规则矩阵（存储层） ─────────

    public static IEnumerable<object[]> ConflictMatrix()
    {
        //          本地改动状态, 快照时间, 服务器上的已读值, 期望本地已读
        yield return ["pending", "before", false, true];    // 有待写回的“已读”：不管快照多新，本地胜出
        yield return ["pending", "after", false, true];
        yield return ["acked", "before", false, true];      // 已写回确认，但快照早于这次改动（陈旧快照）：不翻回去
        yield return ["acked", "after", false, false];      // 快照晚于改动：服务器胜出（别处又标成未读了）
        yield return ["none", "before", true, true];        // 本地没动过：服务器胜出
        yield return ["none", "after", true, true];
    }

    [Theory]
    [MemberData(nameof(ConflictMatrix))]
    public async Task Read_State_Conflict_Matrix(string local, string snapshot, bool serverRead, bool expectRead)
    {
        await using var fx = await SendFixture.CreateAsync();
        var m = await fx.AddMessageAsync("INBOX", 1);        // 本地：未读
        if (local != "none")
        {
            await fx.Db.ApplyMessageChangeAsync(m.Id, PendingOpKind.MarkRead);       // 本地变成已读 + 排入写回队列
            if (local == "acked")
            {
                await fx.Db.CompletePendingOpAsync(Assert.Single(await fx.Db.ListPendingOpsAsync()));
            }
        }

        var snapshotAt = snapshot == "before" ? DateTimeOffset.UtcNow.AddHours(-1) : DateTimeOffset.UtcNow.AddHours(1);

        await fx.Db.ApplyServerFlagsAsync(fx.FolderId("INBOX"), [new ServerFlagState(1, serverRead, false)], snapshotAt);

        Assert.Equal(expectRead, (await fx.Db.GetMessageAsync(m.Id))!.IsRead);
    }

    [Fact]
    public async Task Read_And_Flag_Are_Independent_Fields()
    {
        await using var fx = await SendFixture.CreateAsync();
        var m = await fx.AddMessageAsync("INBOX", 1);
        await fx.Db.ApplyMessageChangeAsync(m.Id, PendingOpKind.Flag);          // 只有星标待写回

        await fx.Db.ApplyServerFlagsAsync(fx.FolderId("INBOX"), [new ServerFlagState(1, true, false)], DateTimeOffset.UtcNow.AddHours(1));

        var after = (await fx.Db.GetMessageAsync(m.Id))!;
        Assert.True(after.IsFlagged);     // 本地星标胜出
        Assert.True(after.IsRead);        // 已读没有本地改动：服务器胜出
    }

    [Fact]
    public async Task Server_Flags_Only_Touch_Server_Rows_Of_That_Folder()
    {
        await using var fx = await SendFixture.CreateAsync();
        await fx.AddMessageAsync("INBOX", 1);
        await fx.AddMessageAsync("Archive", 1);

        await fx.Db.ApplyServerFlagsAsync(fx.FolderId("INBOX"), [new ServerFlagState(1, true, true)], DateTimeOffset.UtcNow.AddHours(1));

        Assert.True((await fx.Db.GetMessageAsync($"{fx.FolderId("INBOX")}:1"))!.IsRead);
        Assert.False((await fx.Db.GetMessageAsync($"{fx.FolderId("Archive")}:1"))!.IsRead);
    }

    [Fact]
    public async Task Server_Deleted_Removes_Rows_And_Recounts()
    {
        await using var fx = await SendFixture.CreateAsync();
        await fx.AddMessageAsync("INBOX", 1);
        await fx.AddMessageAsync("INBOX", 2);

        await fx.Db.RemoveServerDeletedAsync(fx.FolderId("INBOX"), [1]);

        Assert.Null(await fx.Db.GetMessageAsync($"{fx.FolderId("INBOX")}:1"));
        Assert.Equal(1, (await fx.Db.GetFolderAsync(fx.FolderId("INBOX")))!.TotalCount);
    }

    [Fact]
    public async Task Locally_Moved_Message_Survives_The_Source_Folder_Listing_It_Left()
    {
        await using var fx = await SendFixture.CreateAsync();
        var m = await fx.AddMessageAsync("INBOX", 5);
        await fx.Db.ApplyMessageChangeAsync(m.Id, PendingOpKind.Move, fx.FolderId("Archive"));

        // 服务器上 INBOX 的 UID 5 没了（正是我们自己写回的移动）：本地已经在 Archive 里的那一行不能被删
        await fx.Db.RemoveServerDeletedAsync(fx.FolderId("INBOX"), [5]);

        var moved = await fx.Db.GetMessageAsync(m.Id);
        Assert.NotNull(moved);
        Assert.Equal(fx.FolderId("Archive"), moved!.FolderId);
        Assert.Single(await fx.Db.ListPendingOpsAsync());
    }
}
