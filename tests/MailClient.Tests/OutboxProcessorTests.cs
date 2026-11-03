using MailClient.Core;
using MailClient.Core.Sending;
using MailClient.Providers.Outbox;
using MailClient.Storage;
using MailClient.Storage.Services;
using MailKit.Net.Smtp;

namespace MailClient.Tests;

internal sealed class FakeTransport : IOutgoingTransport
{
    private readonly FakeTransportFactory _owner;

    public FakeTransport(FakeTransportFactory owner) => _owner = owner;

    public bool SentLookupIsAuthoritative => _owner.Authoritative;

    public Task SendAsync(OutgoingMailMessage message, string fromAddress, string? fromName, DateTimeOffset date, CancellationToken cancellationToken)
    {
        _owner.SendCalls++;
        if (_owner.SendFailures.Count > 0)
        {
            throw _owner.SendFailures.Dequeue();
        }

        _owner.Sent.Add(message);
        return Task.CompletedTask;
    }

    public Task<long?> AppendToSentAsync(OutgoingMailMessage message, string fromAddress, string? fromName, DateTimeOffset date, string? sentFolderPath, CancellationToken cancellationToken)
    {
        _owner.AppendCalls++;
        _owner.AppendedFolders.Add(sentFolderPath);
        if (_owner.AppendFailure is not null)
        {
            throw _owner.AppendFailure;
        }

        return Task.FromResult<long?>(4242);
    }

    public Task<SentLookup> FindInSentAsync(OutgoingMailMessage message, DateTimeOffset notBefore, string? sentFolderPath, CancellationToken cancellationToken)
    {
        _owner.LookupCalls++;
        return Task.FromResult(_owner.Lookup);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

internal sealed class FakeTransportFactory : IOutgoingTransportFactory
{
    public Queue<Exception> SendFailures { get; } = new();
    public List<OutgoingMailMessage> Sent { get; } = new();
    public List<string?> AppendedFolders { get; } = new();
    public Exception? AppendFailure { get; set; }
    public int SendCalls { get; set; }
    public int AppendCalls { get; set; }
    public int LookupCalls { get; set; }
    public bool AppendSentCopy { get; set; } = true;
    public bool Authoritative { get; set; }
    public SentLookup Lookup { get; set; } = SentLookup.Unknown;

    public Task<IOutgoingTransport> CreateAsync(AccountEntity account, CancellationToken cancellationToken)
        => Task.FromResult<IOutgoingTransport>(new FakeTransport(this));

    public bool ShouldAppendSentCopy(AccountEntity account) => AppendSentCopy;
}

public class OutboxProcessorTests
{
    private static readonly DateTimeOffset Now = SendFixture.T0;

    private sealed class Rig : IAsyncDisposable
    {
        public required SendFixture Fx { get; init; }
        public required FakeTransportFactory Factory { get; init; }
        public required OutboxProcessor Processor { get; init; }
        public DateTimeOffset Clock { get; set; } = Now;
        public List<OutboxResult> Results { get; } = new();
        public MailDatabase Db => Fx.Db;

        public async Task<string> EnqueueAsync(OutgoingMailMessage? message = null)
        {
            await new OutboxSendService(Db).EnqueueAsync(message ?? Fx.Outgoing());
            return (await Db.ListOutboxItemsAsync()).Last().Id;
        }

        public async Task<OutboxEntity> ItemAsync(string id) => (await Db.GetOutboxItemAsync(id))!;

        public ValueTask DisposeAsync() => Fx.DisposeAsync();
    }

    private static async Task<Rig> CreateAsync(string providerId = "imap.generic")
    {
        var fx = await SendFixture.CreateAsync(providerId);
        var factory = new FakeTransportFactory();
        Rig? rig = null;
        var processor = new OutboxProcessor(fx.Db, factory, () => rig!.Clock);
        rig = new Rig { Fx = fx, Factory = factory, Processor = processor };
        processor.ItemProcessed += (_, r) => rig.Results.Add(r);
        return rig;
    }

    [Fact]
    public async Task Success_Marks_Sent_Stores_Message_Id_Creates_Local_Copy_And_Appends_To_Server()
    {
        await using var rig = await CreateAsync();
        var id = await rig.EnqueueAsync();
        var queued = await rig.ItemAsync(id);
        Assert.Equal(OutboxStatus.Pending, queued.Status);
        Assert.False(string.IsNullOrEmpty(queued.RfcMessageId)); // 发送前就已经记下 Message-ID（幂等的基础）

        Assert.Equal(1, await rig.Processor.ProcessOnceAsync());

        var item = await rig.ItemAsync(id);
        Assert.Equal(OutboxStatus.Sent, item.Status);
        Assert.Equal(1, item.Attempts);
        Assert.Equal(1, rig.Factory.SendCalls);
        Assert.Equal(queued.RfcMessageId, rig.Factory.Sent[0].MessageId);

        // 配置为需要追加：APPEND 到“已发送”，随后不再挂起
        Assert.Equal(1, rig.Factory.AppendCalls);
        Assert.Equal("Sent", rig.Factory.AppendedFolders[0]);
        Assert.False(item.SentCopyPending);

        // 本地已发送里立刻能看到，并带上服务器返回的 UID
        var copy = await rig.Db.GetMessageAsync(OutboxProcessor.LocalCopyId(id));
        Assert.NotNull(copy);
        Assert.Equal(rig.Fx.FolderId("Sent"), copy!.FolderId);
        Assert.True(copy.IsRead);
        Assert.Equal(4242, copy.RemoteUid);
        Assert.Contains(rig.Results, r => r.Sent);
    }

    [Fact]
    public async Task Sent_Copy_Is_Not_Appended_When_Policy_Says_Server_Saves_It()
    {
        await using var rig = await CreateAsync("gmail.oauth");
        rig.Factory.AppendSentCopy = false;
        var id = await rig.EnqueueAsync();

        await rig.Processor.ProcessOnceAsync();

        Assert.Equal(OutboxStatus.Sent, (await rig.ItemAsync(id)).Status);
        Assert.Equal(0, rig.Factory.AppendCalls);
        Assert.NotNull(await rig.Db.GetMessageAsync(OutboxProcessor.LocalCopyId(id))); // 本地仍然显示在已发送
    }

    [Fact]
    public async Task Transient_Failure_Retries_With_Exponential_Backoff_Then_Gives_Up()
    {
        await using var rig = await CreateAsync();
        var id = await rig.EnqueueAsync();
        for (var i = 0; i < 10; i++)
        {
            rig.Factory.SendFailures.Enqueue(new TimeoutException());
        }

        var expectedDelays = new[] { 30, 120, 480, 1920 };
        for (var attempt = 1; attempt <= 4; attempt++)
        {
            await rig.Processor.ProcessOnceAsync();
            var item = await rig.ItemAsync(id);
            Assert.Equal(OutboxStatus.Pending, item.Status);
            Assert.Equal(attempt, item.Attempts);
            Assert.Equal(rig.Clock.AddSeconds(expectedDelays[attempt - 1]), item.NextAttemptAt);
            Assert.False(string.IsNullOrEmpty(item.LastError));

            // 没到期不会重试
            Assert.Equal(0, await rig.Processor.ProcessOnceAsync());
            Assert.Equal(attempt, rig.Factory.SendCalls);
            rig.Clock = item.NextAttemptAt!.Value;
        }

        await rig.Processor.ProcessOnceAsync(); // 第 5 次
        var failed = await rig.ItemAsync(id);
        Assert.Equal(OutboxStatus.Failed, failed.Status);
        Assert.Equal(RetryPolicy.MaxAttempts, failed.Attempts);
        Assert.Contains("放弃", failed.LastError);
        Assert.Equal(5, rig.Factory.SendCalls);

        // 用户点“重试”：重新计数
        Assert.True(await rig.Db.RetryOutboxAsync(id, rig.Clock));
        var retried = await rig.ItemAsync(id);
        Assert.Equal(OutboxStatus.Pending, retried.Status);
        Assert.Equal(0, retried.Attempts);
    }

    [Fact]
    public async Task Transient_Then_Success_Sends_Exactly_Once()
    {
        await using var rig = await CreateAsync();
        var id = await rig.EnqueueAsync();
        rig.Factory.SendFailures.Enqueue(new System.Net.Sockets.SocketException());

        await rig.Processor.ProcessOnceAsync();
        Assert.Equal(OutboxStatus.Pending, (await rig.ItemAsync(id)).Status);

        await rig.Processor.ProcessOnceAsync(ignoreBackoff: true); // “网络恢复”：忽略退避
        Assert.Equal(OutboxStatus.Sent, (await rig.ItemAsync(id)).Status);
        Assert.Single(rig.Factory.Sent);
    }

    public static IEnumerable<object[]> PermanentFailures()
    {
        yield return [new MailKit.Security.AuthenticationException("535 bad credentials"), FailureKind.Auth];
        yield return [new SmtpCommandException(SmtpErrorCode.RecipientNotAccepted, SmtpStatusCode.MailboxUnavailable, MimeKit.MailboxAddress.Parse("x@y.com"), "no such user"), FailureKind.RecipientRejected];
        yield return [new SmtpCommandException(SmtpErrorCode.MessageNotAccepted, SmtpStatusCode.ExceededStorageAllocation, "too big"), FailureKind.MessageRejected];
    }

    [Theory]
    [MemberData(nameof(PermanentFailures))]
    public async Task Permanent_Failure_Fails_Immediately_Without_Retry(Exception error, FailureKind expected)
    {
        await using var rig = await CreateAsync();
        var id = await rig.EnqueueAsync();
        rig.Factory.SendFailures.Enqueue(error);

        await rig.Processor.ProcessOnceAsync();

        var item = await rig.ItemAsync(id);
        Assert.Equal(OutboxStatus.Failed, item.Status);
        Assert.True(item.IsPermanent);
        Assert.Null(item.NextAttemptAt);
        Assert.Equal(1, item.Attempts);
        Assert.Contains(rig.Results, r => r.Failure?.Kind == expected && !r.WillRetry);

        rig.Clock = rig.Clock.AddDays(1);
        Assert.Equal(0, await rig.Processor.ProcessOnceAsync()); // Failed 不会被自动再次投递
        Assert.Equal(1, rig.Factory.SendCalls);
    }

    [Fact]
    public async Task Demo_Account_Fails_Permanently_Without_Touching_The_Network()
    {
        await using var tmp = await TempDb.CreateSeededAsync();
        var factory = new FakeTransportFactory();
        var processor = new OutboxProcessor(tmp.Database, factory, () => SendFixture.T0);
        await new OutboxSendService(tmp.Database).EnqueueAsync(new OutgoingMailMessage(null, ["a@example.com"], [], [], "演示", "<p>x</p>", []));

        await processor.ProcessOnceAsync();

        var item = (await tmp.Database.ListOutboxItemsAsync()).Single();
        Assert.Equal(OutboxStatus.Failed, item.Status);
        Assert.Equal(0, factory.SendCalls);
        Assert.Contains("演示账号", item.LastError);
    }

    [Fact]
    public async Task Append_Failure_After_Send_Never_Causes_A_Resend()
    {
        await using var rig = await CreateAsync();
        rig.Factory.AppendFailure = new MailKit.Security.AuthenticationException("denied");
        var id = await rig.EnqueueAsync();

        await rig.Processor.ProcessOnceAsync();

        var item = await rig.ItemAsync(id);
        Assert.Equal(OutboxStatus.Sent, item.Status);
        Assert.False(item.SentCopyPending);
        Assert.Contains("已发送", item.LastError);
        rig.Clock = rig.Clock.AddDays(2);
        await rig.Processor.ProcessOnceAsync(ignoreBackoff: true);
        Assert.Equal(1, rig.Factory.SendCalls);
    }

    [Fact]
    public async Task Transient_Append_Failure_Is_Retried_Without_Resending()
    {
        await using var rig = await CreateAsync();
        rig.Factory.AppendFailure = new TimeoutException();
        var id = await rig.EnqueueAsync();

        await rig.Processor.ProcessOnceAsync();
        var item = await rig.ItemAsync(id);
        Assert.Equal(OutboxStatus.Sent, item.Status);
        Assert.True(item.SentCopyPending);
        Assert.Equal(1, item.CopyAttempts);

        rig.Factory.AppendFailure = null;
        rig.Clock = rig.Clock.AddMinutes(5);
        await rig.Processor.ProcessOnceAsync();

        item = await rig.ItemAsync(id);
        Assert.False(item.SentCopyPending);
        Assert.Equal(1, rig.Factory.SendCalls);
        Assert.Equal(2, rig.Factory.AppendCalls);
    }

    [Fact]
    public async Task Recover_Resets_Items_Stuck_In_Sending_And_Marks_Them_Maybe_Sent()
    {
        await using var rig = await CreateAsync();
        var id = await rig.EnqueueAsync();
        Assert.True(await rig.Db.TryMarkOutboxSendingAsync(id, rig.Clock)); // 模拟：发送中途进程崩溃
        Assert.Equal(OutboxStatus.Sending, (await rig.ItemAsync(id)).Status);

        Assert.Equal(1, await rig.Processor.RecoverAsync());

        var item = await rig.ItemAsync(id);
        Assert.Equal(OutboxStatus.Pending, item.Status);
        Assert.True(item.MaybeSent);
    }

    [Fact]
    public async Task Maybe_Sent_Found_On_Server_Is_Not_Sent_Again()
    {
        await using var rig = await CreateAsync();
        var id = await rig.EnqueueAsync();
        await rig.Db.TryMarkOutboxSendingAsync(id, rig.Clock);
        await rig.Processor.RecoverAsync();
        rig.Factory.Lookup = SentLookup.Found;

        await rig.Processor.ProcessOnceAsync();

        Assert.Equal(OutboxStatus.Sent, (await rig.ItemAsync(id)).Status);
        Assert.Equal(0, rig.Factory.SendCalls); // 幂等：不重复发送
        Assert.Equal(1, rig.Factory.LookupCalls);
    }

    [Fact]
    public async Task Maybe_Sent_Unverifiable_Asks_The_User_Instead_Of_Resending()
    {
        await using var rig = await CreateAsync(); // 普通 IMAP：服务器不保存已发送，“找不到”不可信
        var id = await rig.EnqueueAsync();
        await rig.Db.TryMarkOutboxSendingAsync(id, rig.Clock);
        await rig.Processor.RecoverAsync();
        rig.Factory.Lookup = SentLookup.NotFound;
        rig.Factory.Authoritative = false;

        await rig.Processor.ProcessOnceAsync();

        var item = await rig.ItemAsync(id);
        Assert.Equal(OutboxStatus.Failed, item.Status);
        Assert.Contains("无法确认", item.LastError);
        Assert.Equal(0, rig.Factory.SendCalls);

        // 用户确认没发出，点“重试”后才会真正发送
        await rig.Db.RetryOutboxAsync(id, rig.Clock);
        await rig.Processor.ProcessOnceAsync();
        Assert.Equal(1, rig.Factory.SendCalls);
    }

    [Fact]
    public async Task Maybe_Sent_Not_Found_On_Authoritative_Server_Is_Resent()
    {
        await using var rig = await CreateAsync("gmail.oauth");
        var id = await rig.EnqueueAsync();
        await rig.Db.TryMarkOutboxSendingAsync(id, rig.Clock);
        await rig.Processor.RecoverAsync();
        rig.Factory.Lookup = SentLookup.NotFound;
        rig.Factory.Authoritative = true;
        rig.Factory.AppendSentCopy = false;

        await rig.Processor.ProcessOnceAsync();

        Assert.Equal(OutboxStatus.Sent, (await rig.ItemAsync(id)).Status);
        Assert.Equal(1, rig.Factory.SendCalls);
    }

    [Fact]
    public async Task Connection_Lost_During_Data_Phase_Marks_Maybe_Sent_And_Checks_Before_Retry()
    {
        await using var rig = await CreateAsync();
        var id = await rig.EnqueueAsync();
        rig.Factory.SendFailures.Enqueue(new SendOutcomeUnknownException(new System.IO.IOException("reset")));

        await rig.Processor.ProcessOnceAsync();

        var item = await rig.ItemAsync(id);
        Assert.Equal(OutboxStatus.Pending, item.Status);
        Assert.True(item.MaybeSent);
    }

    [Fact]
    public async Task Concurrent_Processing_Does_Not_Send_Twice()
    {
        await using var rig = await CreateAsync();
        await rig.EnqueueAsync();

        await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => rig.Processor.ProcessOnceAsync()));

        Assert.Equal(1, rig.Factory.SendCalls);
    }

    [Fact]
    public async Task Marking_Sending_Is_Atomic()
    {
        await using var rig = await CreateAsync();
        var id = await rig.EnqueueAsync();

        Assert.True(await rig.Db.TryMarkOutboxSendingAsync(id, rig.Clock));
        Assert.False(await rig.Db.TryMarkOutboxSendingAsync(id, rig.Clock));
    }

    [Fact]
    public async Task Outbox_Raises_Changed_And_Delete_Removes_The_Item()
    {
        await using var rig = await CreateAsync();
        var changes = 0;
        rig.Db.OutboxChanged += (_, _) => changes++;

        var id = await rig.EnqueueAsync();
        Assert.True(changes > 0);
        Assert.Equal(1, await rig.Db.CountOutboxItemsAsync());

        Assert.True(await rig.Db.DeleteOutboxAsync(id));
        Assert.Equal(0, await rig.Db.CountOutboxItemsAsync());
    }

    [Fact]
    public async Task Invalid_Payload_Fails_Permanently()
    {
        await using var rig = await CreateAsync();
        var now = rig.Clock;
        await rig.Db.EnqueueOutboxAsync(new OutboxEntity("bad", rig.Fx.AccountId, "not json{", OutboxStatus.Pending, 0, null, now, now));

        await rig.Processor.ProcessOnceAsync();

        Assert.Equal(OutboxStatus.Failed, (await rig.ItemAsync("bad")).Status);
        Assert.Equal(0, rig.Factory.SendCalls);
    }
}
