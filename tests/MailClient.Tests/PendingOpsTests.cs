using MailClient.Core;
using MailClient.Core.Sending;
using MailClient.Core.Sync;
using MailClient.Providers.WriteBack;
using MailClient.Storage;

namespace MailClient.Tests;

public class PendingOpCoalescerTests
{
    private static readonly Guid Acct = Guid.NewGuid();
    private static readonly DateTimeOffset T = SendFixture.T0;

    private static PendingOpEntity Op(PendingOpKind kind, string? source = "INBOX", string? target = null, string key = "5", int attempts = 0)
        => new(0, Acct, "m1", kind, source, target, key, attempts, null, null, T, T);

    [Fact]
    public void No_Existing_Inserts()
        => Assert.Equal(CoalesceAction.Insert, PendingOpCoalescer.Coalesce(null, Op(PendingOpKind.MarkRead)).Action);

    [Fact]
    public void Latest_Read_State_Wins()
    {
        var r = PendingOpCoalescer.Coalesce(Op(PendingOpKind.MarkRead, attempts: 3), Op(PendingOpKind.MarkUnread));
        Assert.Equal(CoalesceAction.Replace, r.Action);
        Assert.Equal(PendingOpKind.MarkUnread, r.Op!.Kind);
        Assert.Equal(0, r.Op.Attempts); // 新变更重新计数
    }

    [Fact]
    public void Latest_Flag_State_Wins()
        => Assert.Equal(PendingOpKind.Unflag, PendingOpCoalescer.Coalesce(Op(PendingOpKind.Flag), Op(PendingOpKind.Unflag)).Op!.Kind);

    [Fact]
    public void Different_Fields_Cannot_Be_Coalesced()
        => Assert.Throws<InvalidOperationException>(() => PendingOpCoalescer.Coalesce(Op(PendingOpKind.MarkRead), Op(PendingOpKind.Flag)));

    [Fact]
    public void Move_Then_Move_Keeps_Original_Source()
    {
        var r = PendingOpCoalescer.Coalesce(
            Op(PendingOpKind.Move, "INBOX", "Archive"), Op(PendingOpKind.Move, "Archive", "Trash"));
        Assert.Equal(CoalesceAction.Replace, r.Action);
        Assert.Equal("INBOX", r.Op!.SourceFolderId);
        Assert.Equal("Trash", r.Op.TargetFolderId);
    }

    [Fact]
    public void Move_Back_To_Original_Cancels_Out()
        => Assert.Equal(CoalesceAction.Remove, PendingOpCoalescer.Coalesce(
            Op(PendingOpKind.Move, "INBOX", "Archive"), Op(PendingOpKind.Move, "Archive", "INBOX")).Action);

    [Fact]
    public void Move_Then_Delete_Becomes_Delete_From_Original_Folder()
    {
        var r = PendingOpCoalescer.Coalesce(Op(PendingOpKind.Move, "INBOX", "Trash"), Op(PendingOpKind.Delete, "Trash"));
        Assert.Equal(PendingOpKind.Delete, r.Op!.Kind);
        Assert.Equal("INBOX", r.Op.SourceFolderId);
    }

    [Fact]
    public void After_Delete_Nothing_Else_Matters()
        => Assert.Equal(CoalesceAction.Keep, PendingOpCoalescer.Coalesce(
            Op(PendingOpKind.Delete), Op(PendingOpKind.Move, "x", "Archive")).Action);

    [Fact]
    public void Move_To_Same_Folder_Is_Ignored()
        => Assert.Equal(CoalesceAction.Keep, PendingOpCoalescer.Coalesce(null, Op(PendingOpKind.Move, "A", "A")).Action);

    [Theory]
    [InlineData(PendingOpKind.MarkRead, PendingField.Read)]
    [InlineData(PendingOpKind.MarkUnread, PendingField.Read)]
    [InlineData(PendingOpKind.Flag, PendingField.Flag)]
    [InlineData(PendingOpKind.Unflag, PendingField.Flag)]
    [InlineData(PendingOpKind.Move, PendingField.Location)]
    [InlineData(PendingOpKind.Delete, PendingField.Location)]
    public void Fields(PendingOpKind kind, PendingField field) => Assert.Equal(field, PendingOpKinds.FieldOf(kind));
}

public class SyncMergeTests
{
    private static readonly Guid Acct = Guid.NewGuid();

    private static MessageEntity Msg(bool read, bool flagged, string folder)
        => new("m1", Acct, folder, 1, "r@x", null, "s", "a@b.c", "A", "p", "t", "<p>t</p>",
            SendFixture.T0, SendFixture.T0, read, flagged, false, false, null, 10);

    [Fact]
    public void Without_Pending_Server_Wins()
    {
        var merged = SyncMerge.Apply(Msg(true, true, "INBOX"), Msg(false, false, "Archive"), PendingFieldMask.None)!;
        Assert.True(merged.IsRead);
        Assert.True(merged.IsFlagged);
        Assert.Equal("INBOX", merged.FolderId);
    }

    [Fact]
    public void Pending_Read_Keeps_Local_Read_Only()
    {
        var merged = SyncMerge.Apply(Msg(false, true, "INBOX"), Msg(true, false, "INBOX"), PendingFieldMask.Read)!;
        Assert.True(merged.IsRead);     // 本地
        Assert.True(merged.IsFlagged);  // 服务器
    }

    [Fact]
    public void Pending_Flag_Keeps_Local_Flag_Only()
    {
        var merged = SyncMerge.Apply(Msg(true, false, "INBOX"), Msg(false, true, "INBOX"), PendingFieldMask.Flag)!;
        Assert.True(merged.IsFlagged);
        Assert.True(merged.IsRead);
    }

    [Fact]
    public void Pending_Move_Keeps_Local_Folder()
        => Assert.Equal("Archive", SyncMerge.Apply(Msg(false, false, "INBOX"), Msg(false, false, "Archive"), PendingFieldMask.Location)!.FolderId);

    [Fact]
    public void Pending_Delete_Skips_The_Message()
        => Assert.Null(SyncMerge.Apply(Msg(false, false, "INBOX"), Msg(false, false, "INBOX"), PendingFieldMask.Deleted));

    [Fact]
    public void New_Message_Is_Taken_As_Is()
        => Assert.Equal("INBOX", SyncMerge.Apply(Msg(false, false, "INBOX"), null, PendingFieldMask.Read)!.FolderId);
}

public class PendingOpsDatabaseTests
{
    [Fact]
    public async Task Mark_Read_Applies_Locally_And_Queues_One_Op()
    {
        await using var fx = await SendFixture.CreateAsync();
        var m = await fx.AddMessageAsync("INBOX", 10);
        var events = 0;
        fx.Db.PendingOpsChanged += (_, _) => events++;

        Assert.True(await fx.Db.ApplyMessageChangeAsync(m.Id, PendingOpKind.MarkRead));

        Assert.True((await fx.Db.GetMessageAsync(m.Id))!.IsRead);
        var op = Assert.Single(await fx.Db.ListPendingOpsAsync());
        Assert.Equal(PendingOpKind.MarkRead, op.Kind);
        Assert.Equal("10", op.RemoteKey);
        Assert.Equal(fx.FolderId("INBOX"), op.SourceFolderId);
        Assert.True(events > 0);
    }

    [Fact]
    public async Task Toggling_Coalesces_To_Latest_Value()
    {
        await using var fx = await SendFixture.CreateAsync();
        var m = await fx.AddMessageAsync("INBOX", 10);

        await fx.Db.ApplyMessageChangeAsync(m.Id, PendingOpKind.MarkRead);
        await fx.Db.ApplyMessageChangeAsync(m.Id, PendingOpKind.MarkUnread);
        await fx.Db.ApplyMessageChangeAsync(m.Id, PendingOpKind.Flag);
        await fx.Db.ApplyMessageChangeAsync(m.Id, PendingOpKind.MarkRead);

        var ops = await fx.Db.ListPendingOpsAsync();
        Assert.Equal(2, ops.Count);
        Assert.Contains(ops, o => o.Kind == PendingOpKind.MarkRead);
        Assert.Contains(ops, o => o.Kind == PendingOpKind.Flag);
    }

    [Fact]
    public async Task Move_And_Move_Back_Leaves_Nothing_To_Write()
    {
        await using var fx = await SendFixture.CreateAsync();
        var m = await fx.AddMessageAsync("INBOX", 10);

        await fx.Db.ApplyMessageChangeAsync(m.Id, PendingOpKind.Move, fx.FolderId("Archive"));
        Assert.Single(await fx.Db.ListPendingOpsAsync());
        await fx.Db.ApplyMessageChangeAsync(m.Id, PendingOpKind.Move, fx.FolderId("INBOX"));

        Assert.Empty(await fx.Db.ListPendingOpsAsync());
    }

    [Fact]
    public async Task Delete_Removes_Locally_And_Queues_Delete()
    {
        await using var fx = await SendFixture.CreateAsync();
        var m = await fx.AddMessageAsync("Trash", 3);

        Assert.True(await fx.Db.ApplyMessageChangeAsync(m.Id, PendingOpKind.Delete));

        Assert.Null(await fx.Db.GetMessageAsync(m.Id));
        Assert.Equal(PendingOpKind.Delete, Assert.Single(await fx.Db.ListPendingOpsAsync()).Kind);
    }

    [Fact]
    public async Task Missing_Message_Returns_False()
    {
        await using var fx = await SendFixture.CreateAsync();
        Assert.False(await fx.Db.ApplyMessageChangeAsync("nope", PendingOpKind.MarkRead));
    }

    [Fact]
    public async Task Demo_Account_Changes_Stay_Local()
    {
        await using var tmp = await TempDb.CreateSeededAsync();
        var any = (await tmp.Database.QueryMessagesAsync(new MessageListFilter(UnreadOnly: true, Limit: 1))).First();

        Assert.True(await tmp.Database.ApplyMessageChangeAsync(any.Id, PendingOpKind.MarkRead));

        Assert.Empty(await tmp.Database.ListPendingOpsAsync());
        Assert.True((await tmp.Database.GetMessageAsync(any.Id))!.IsRead);
    }

    [Fact]
    public async Task Graph_Messages_Use_Graph_Id_As_Remote_Key()
    {
        await using var fx = await SendFixture.CreateAsync("microsoft.graph");
        var folder = fx.FolderId("INBOX");
        var id = $"{fx.AccountId:D}:AAMk123=";
        await fx.Db.UpsertMessagesAsync([fx.Message("INBOX", 1) with { Id = id, RemoteUid = null }]);

        await fx.Db.ApplyMessageChangeAsync(id, PendingOpKind.Flag);

        var op = Assert.Single(await fx.Db.ListPendingOpsAsync());
        Assert.Equal("AAMk123=", op.RemoteKey);
        Assert.Equal(folder, op.SourceFolderId);
    }

    [Fact]
    public async Task Sync_Does_Not_Overwrite_Pending_Local_Changes()
    {
        await using var fx = await SendFixture.CreateAsync();
        var m = await fx.AddMessageAsync("INBOX", 10);
        await fx.Db.ApplyMessageChangeAsync(m.Id, PendingOpKind.MarkRead);
        await fx.Db.ApplyMessageChangeAsync(m.Id, PendingOpKind.Move, fx.FolderId("Archive"));

        // 服务器版本还是“未读、在收件箱、已加星标”
        await fx.Db.UpsertMessagesAsync([m with { IsRead = false, IsFlagged = true }]);

        var after = (await fx.Db.GetMessageAsync(m.Id))!;
        Assert.True(after.IsRead);                              // 有未写回的已读变更：保留本地
        Assert.Equal(fx.FolderId("Archive"), after.FolderId);   // 有未写回的移动：保留本地
        Assert.True(after.IsFlagged);                           // 没有标记变更：服务器说了算
    }

    [Fact]
    public async Task Sync_Takes_Server_Values_After_Ops_Are_Written_Back()
    {
        await using var fx = await SendFixture.CreateAsync();
        var m = await fx.AddMessageAsync("INBOX", 10);
        await fx.Db.ApplyMessageChangeAsync(m.Id, PendingOpKind.MarkRead);
        await fx.Db.CompletePendingOpAsync(Assert.Single(await fx.Db.ListPendingOpsAsync()));

        await fx.Db.UpsertMessagesAsync([m with { IsRead = false }]);

        Assert.False((await fx.Db.GetMessageAsync(m.Id))!.IsRead);
    }

    [Fact]
    public async Task Sync_Does_Not_Resurrect_A_Message_Deleted_Locally()
    {
        await using var fx = await SendFixture.CreateAsync();
        var m = await fx.AddMessageAsync("Trash", 3);
        await fx.Db.ApplyMessageChangeAsync(m.Id, PendingOpKind.Delete);

        await fx.Db.UpsertMessagesAsync([m]);

        Assert.Null(await fx.Db.GetMessageAsync(m.Id));
    }

    [Fact]
    public async Task Failed_Op_Is_Backed_Off_And_Completed_Op_Is_Removed()
    {
        await using var fx = await SendFixture.CreateAsync();
        var m = await fx.AddMessageAsync("INBOX", 10);
        await fx.Db.ApplyMessageChangeAsync(m.Id, PendingOpKind.Flag);
        var op = Assert.Single(await fx.Db.ListPendingOpsAsync());

        await fx.Db.FailPendingOpAsync(op, "boom", SendFixture.T0.AddMinutes(5));

        Assert.Empty(await fx.Db.ListPendingOpsAsync(SendFixture.T0));
        var due = Assert.Single(await fx.Db.ListPendingOpsAsync(SendFixture.T0.AddMinutes(6)));
        Assert.Equal(1, due.Attempts);
        Assert.Equal("boom", due.LastError);
        Assert.Equal(SendFixture.T0.AddMinutes(5), await fx.Db.GetNextPendingOpAttemptAsync());

        await fx.Db.CompletePendingOpAsync(due);
        Assert.Equal(0, await fx.Db.CountPendingOpsAsync());
    }
}

internal sealed class FakeGateway : IMessageWriteGateway
{
    public List<string> Calls { get; } = new();
    public Queue<Exception> Failures { get; } = new();
    public long? MoveResultUid { get; set; } = 99;

    private void Step(string call)
    {
        Calls.Add(call);
        if (Failures.Count > 0)
        {
            throw Failures.Dequeue();
        }
    }

    public Task SetReadAsync(RemoteMessage message, bool read, CancellationToken ct)
    {
        Step($"read:{message.Key}:{read}:{message.Folder.Path}:{message.Folder.UidValidity}");
        return Task.CompletedTask;
    }

    public Task SetFlaggedAsync(RemoteMessage message, bool flagged, CancellationToken ct)
    {
        Step($"flag:{message.Key}:{flagged}");
        return Task.CompletedTask;
    }

    public Task<long?> MoveAsync(RemoteMessage message, RemoteFolder destination, CancellationToken ct)
    {
        Step($"move:{message.Key}:{message.Folder.Path}->{destination.Path}");
        return Task.FromResult(MoveResultUid);
    }

    public Task DeleteAsync(RemoteMessage message, CancellationToken ct)
    {
        Step($"delete:{message.Key}");
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

internal sealed class FakeGatewayFactory : IWriteGatewayFactory
{
    public FakeGateway Gateway { get; } = new();
    public Exception? CreateFailure { get; set; }
    public int Created { get; private set; }

    public Task<IMessageWriteGateway> CreateAsync(AccountEntity account, CancellationToken cancellationToken)
    {
        Created++;
        if (CreateFailure is not null)
        {
            throw CreateFailure;
        }

        return Task.FromResult<IMessageWriteGateway>(Gateway);
    }
}

public class PendingOpsProcessorTests
{
    private static (PendingOpsProcessor Processor, FakeGatewayFactory Factory, List<PendingOpDropped> Dropped, Func<DateTimeOffset> Clock, Action<DateTimeOffset> SetClock)
        Make(SendFixture fx)
    {
        var factory = new FakeGatewayFactory();
        var clock = SendFixture.T0;
        var processor = new PendingOpsProcessor(fx.Db, factory, () => clock);
        var dropped = new List<PendingOpDropped>();
        processor.OpDropped += (_, d) => dropped.Add(d);
        return (processor, factory, dropped, () => clock, c => clock = c);
    }

    [Fact]
    public async Task Writes_Read_Flag_Then_Location_In_Order_And_Clears_Queue()
    {
        await using var fx = await SendFixture.CreateAsync();
        var m = await fx.AddMessageAsync("INBOX", 10);
        // 故意以相反顺序排队
        await fx.Db.ApplyMessageChangeAsync(m.Id, PendingOpKind.Move, fx.FolderId("Archive"));
        await fx.Db.ApplyMessageChangeAsync(m.Id, PendingOpKind.Flag);
        await fx.Db.ApplyMessageChangeAsync(m.Id, PendingOpKind.MarkRead);
        var (processor, factory, dropped, _, _) = Make(fx);

        Assert.Equal(3, await processor.ProcessOnceAsync());

        Assert.Equal(
            ["read:10:True:INBOX:777", "flag:10:True", "move:10:INBOX->Archive"],
            factory.Gateway.Calls);
        Assert.Equal(0, await fx.Db.CountPendingOpsAsync());
        Assert.Empty(dropped);
        Assert.Equal(1, factory.Created); // 同一账号复用一个连接
        Assert.Equal(99, (await fx.Db.GetMessageAsync(m.Id))!.RemoteUid); // 移动后记下新 UID
    }

    [Fact]
    public async Task Transient_Failure_Keeps_Op_With_Backoff()
    {
        await using var fx = await SendFixture.CreateAsync();
        var m = await fx.AddMessageAsync("INBOX", 10);
        await fx.Db.ApplyMessageChangeAsync(m.Id, PendingOpKind.MarkRead);
        var (processor, factory, dropped, clock, setClock) = Make(fx);
        factory.Gateway.Failures.Enqueue(new TimeoutException());

        Assert.Equal(0, await processor.ProcessOnceAsync());

        Assert.Equal(1, await fx.Db.CountPendingOpsAsync());
        Assert.Empty(dropped);
        Assert.Equal(0, await processor.ProcessOnceAsync()); // 还没到期
        Assert.Single(factory.Gateway.Calls);

        setClock(clock().AddMinutes(1));
        Assert.Equal(1, await processor.ProcessOnceAsync());
        Assert.Equal(0, await fx.Db.CountPendingOpsAsync());
    }

    [Fact]
    public async Task Network_Down_At_Connect_Stops_The_Round()
    {
        await using var fx = await SendFixture.CreateAsync();
        var a = await fx.AddMessageAsync("INBOX", 10);
        var b = await fx.AddMessageAsync("INBOX", 11);
        await fx.Db.ApplyMessageChangeAsync(a.Id, PendingOpKind.MarkRead);
        await fx.Db.ApplyMessageChangeAsync(b.Id, PendingOpKind.MarkRead);
        var (processor, factory, _, _, _) = Make(fx);
        factory.CreateFailure = new System.Net.Sockets.SocketException();

        await processor.ProcessOnceAsync();

        Assert.Equal(1, factory.Created); // 不会对每条操作都重新连接
        Assert.Equal(2, await fx.Db.CountPendingOpsAsync());
    }

    [Fact]
    public async Task Auth_Failure_Keeps_Ops_And_Backs_Off_For_An_Hour()
    {
        await using var fx = await SendFixture.CreateAsync();
        var m = await fx.AddMessageAsync("INBOX", 10);
        await fx.Db.ApplyMessageChangeAsync(m.Id, PendingOpKind.MarkRead);
        await fx.Db.ApplyMessageChangeAsync(m.Id, PendingOpKind.Flag);
        var (processor, factory, dropped, _, _) = Make(fx);
        factory.Gateway.Failures.Enqueue(new MailKit.Security.AuthenticationException("expired"));

        await processor.ProcessOnceAsync();

        Assert.Single(factory.Gateway.Calls); // 本轮不再继续该账号
        Assert.Equal(2, await fx.Db.CountPendingOpsAsync());
        Assert.Empty(dropped);
        Assert.Equal(SendFixture.T0 + PendingOpsProcessor.AuthBackoff, await fx.Db.GetNextPendingOpAttemptAsync());
    }

    [Theory]
    [InlineData(FailureKind.NotFound)]
    [InlineData(FailureKind.UidValidityChanged)]
    [InlineData(FailureKind.ServerRejected)]
    public async Task Permanent_Failure_Drops_The_Op_And_Reports_It(FailureKind kind)
    {
        await using var fx = await SendFixture.CreateAsync();
        var m = await fx.AddMessageAsync("INBOX", 10);
        await fx.Db.ApplyMessageChangeAsync(m.Id, PendingOpKind.Flag);
        var (processor, factory, dropped, _, _) = Make(fx);
        factory.Gateway.Failures.Enqueue(new ClassifiedFailureException(kind, "已不存在"));

        await processor.ProcessOnceAsync();

        Assert.Equal(0, await fx.Db.CountPendingOpsAsync());
        var d = Assert.Single(dropped);
        Assert.Equal(kind, d.Failure.Kind);
        Assert.Equal(PendingOpKind.Flag, d.Op.Kind);
    }

    [Fact]
    public async Task One_Dropped_Op_Does_Not_Block_The_Next()
    {
        await using var fx = await SendFixture.CreateAsync();
        var m = await fx.AddMessageAsync("INBOX", 10);
        await fx.Db.ApplyMessageChangeAsync(m.Id, PendingOpKind.MarkRead);
        await fx.Db.ApplyMessageChangeAsync(m.Id, PendingOpKind.Flag);
        var (processor, factory, dropped, _, _) = Make(fx);
        factory.Gateway.Failures.Enqueue(new ClassifiedFailureException(FailureKind.NotFound, "gone"));

        Assert.Equal(1, await processor.ProcessOnceAsync());

        Assert.Single(dropped);
        Assert.Equal(0, await fx.Db.CountPendingOpsAsync());
    }

    [Fact]
    public async Task Uses_Source_Folder_Uid_Validity_For_Verification()
    {
        await using var fx = await SendFixture.CreateAsync();
        var m = await fx.AddMessageAsync("INBOX", 10);
        await fx.Db.ApplyMessageChangeAsync(m.Id, PendingOpKind.MarkRead);
        var (processor, factory, _, _, _) = Make(fx);

        await processor.ProcessOnceAsync();

        Assert.Contains("INBOX:777", factory.Gateway.Calls.Single()); // 把本地记录的 UIDVALIDITY 交给网关校验
    }

    [Fact]
    public async Task Delete_Op_Calls_Permanent_Delete()
    {
        await using var fx = await SendFixture.CreateAsync();
        var m = await fx.AddMessageAsync("Trash", 3);
        await fx.Db.ApplyMessageChangeAsync(m.Id, PendingOpKind.Delete);
        var (processor, factory, _, _, _) = Make(fx);

        await processor.ProcessOnceAsync();

        Assert.Equal(["delete:3"], factory.Gateway.Calls);
    }
}
