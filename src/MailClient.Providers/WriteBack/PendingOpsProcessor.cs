using MailClient.Core;
using MailClient.Core.Sending;
using MailClient.Core.Sync;
using MailClient.Providers.Outbox;
using MailClient.Storage;
using MailClient.Storage.Demo;

namespace MailClient.Providers.WriteBack;

/// <summary>一次写回被放弃（永久失败）：本地状态会在下一次同步时被服务器版本覆盖。</summary>
public sealed record PendingOpDropped(PendingOpEntity Op, FailureInfo Failure);

/// <summary>
/// 写回队列的处理器：按账号分组，同一封邮件内顺序为 已读 → 星标 → 位置（移动 / 删除）。
/// 暂时性 / 认证类失败：保留并退避重试（认证失败退避更久，且本轮跳过该账号剩余操作）；
/// 永久性失败（邮件已不存在、UIDVALIDITY 变化、服务器拒绝）：丢弃并触发 <see cref="OpDropped"/>。
/// </summary>
public sealed class PendingOpsProcessor
{
    public static readonly TimeSpan AuthBackoff = TimeSpan.FromHours(1);

    private readonly MailDatabase _db;
    private readonly IWriteGatewayFactory _factory;
    private readonly Func<DateTimeOffset> _clock;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public PendingOpsProcessor(MailDatabase db, IWriteGatewayFactory factory, Func<DateTimeOffset>? clock = null)
    {
        _db = db;
        _factory = factory;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    public event EventHandler<PendingOpDropped>? OpDropped;

    /// <summary>处理所有到期的写回操作，返回成功写回的数量。</summary>
    public async Task<int> ProcessOnceAsync(bool ignoreBackoff = false, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var ops = await _db.ListPendingOpsAsync(ignoreBackoff ? null : _clock(), cancellationToken).ConfigureAwait(false);
            if (ops.Count == 0)
            {
                return 0;
            }

            var accounts = (await _db.ListAccountsAsync(cancellationToken).ConfigureAwait(false)).ToDictionary(a => a.Id);
            var done = 0;
            foreach (var group in ops.GroupBy(o => o.AccountId))
            {
                done += await ProcessAccountAsync(group.Key, accounts.GetValueOrDefault(group.Key), group.ToList(), cancellationToken)
                    .ConfigureAwait(false);
            }

            return done;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<int> ProcessAccountAsync(Guid accountId, AccountEntity? account, List<PendingOpEntity> ops, CancellationToken ct)
    {
        if (account is null || !account.IsEnabled || DemoDataSeeder.IsDemoAccount(accountId))
        {
            foreach (var op in ops)
            {
                await DropAsync(op, new FailureInfo(FailureKind.Configuration, "账号不存在、已停用或是演示账号，变更只保留在本地。"), ct)
                    .ConfigureAwait(false);
            }

            return 0;
        }

        var folders = (await _db.ListFoldersAsync(accountId, ct).ConfigureAwait(false)).ToDictionary(f => f.Id, StringComparer.Ordinal);
        IMessageWriteGateway? gateway = null;
        var done = 0;
        try
        {
            foreach (var op in ops)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    gateway ??= await _factory.CreateAsync(account, ct).ConfigureAwait(false);
                    await ApplyAsync(gateway, op, folders, ct).ConfigureAwait(false);
                    await _db.CompletePendingOpAsync(op, ct).ConfigureAwait(false);
                    done++;
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    var info = SendFailureClassifier.Classify(ex);
                    switch (info.Kind)
                    {
                        case FailureKind.Transient:
                            await _db.FailPendingOpAsync(op, info.Message, _clock() + RetryPolicy.Delay(op.Attempts + 1), ct).ConfigureAwait(false);
                            break;
                        case FailureKind.Auth:
                        case FailureKind.Configuration:
                            // 需要用户处理；保留变更，之后再试，本轮不再打扰这个账号
                            await _db.FailPendingOpAsync(op, info.Message, _clock() + AuthBackoff, ct).ConfigureAwait(false);
                            return done;
                        default:
                            await DropAsync(op, info, ct).ConfigureAwait(false);
                            break;
                    }

                    if (info.Kind == FailureKind.Transient && gateway is null)
                    {
                        return done; // 连接都没建起来：网络问题，别逐条重试
                    }
                }
            }
        }
        finally
        {
            if (gateway is not null)
            {
                await gateway.DisposeAsync().ConfigureAwait(false);
            }
        }

        return done;
    }

    private async Task DropAsync(PendingOpEntity op, FailureInfo info, CancellationToken ct)
    {
        await _db.CompletePendingOpAsync(op, ct).ConfigureAwait(false);
        OpDropped?.Invoke(this, new PendingOpDropped(op, info));
    }

    private async Task ApplyAsync(
        IMessageWriteGateway gateway, PendingOpEntity op, IReadOnlyDictionary<string, FolderEntity> folders, CancellationToken ct)
    {
        if (op.RemoteKey is null || op.SourceFolderId is null || !folders.TryGetValue(op.SourceFolderId, out var source))
        {
            throw new ClassifiedFailureException(FailureKind.NotFound, "找不到邮件在服务器上的位置。");
        }

        var message = new RemoteMessage(new RemoteFolder(source.RemotePath ?? string.Empty, source.UidValidity), op.RemoteKey);
        switch (op.Kind)
        {
            case PendingOpKind.MarkRead:
            case PendingOpKind.MarkUnread:
                await gateway.SetReadAsync(message, op.Kind == PendingOpKind.MarkRead, ct).ConfigureAwait(false);
                break;
            case PendingOpKind.Flag:
            case PendingOpKind.Unflag:
                await gateway.SetFlaggedAsync(message, op.Kind == PendingOpKind.Flag, ct).ConfigureAwait(false);
                break;
            case PendingOpKind.Move:
            {
                if (op.TargetFolderId is null || !folders.TryGetValue(op.TargetFolderId, out var target) || target.RemotePath is null)
                {
                    throw new ClassifiedFailureException(FailureKind.NotFound, "找不到移动的目标文件夹。");
                }

                var newUid = await gateway.MoveAsync(message, new RemoteFolder(target.RemotePath, target.UidValidity), ct).ConfigureAwait(false);
                await _db.MarkMovedRemoteAsync(op.MessageId, newUid, ct).ConfigureAwait(false);
                break;
            }
            case PendingOpKind.Delete:
                await gateway.DeleteAsync(message, ct).ConfigureAwait(false);
                break;
        }
    }
}

/// <summary>写回后台服务：启动时处理一轮；本地变更入队、网络恢复、退避到期都会唤醒。</summary>
public sealed class PendingOpsWorker : IAsyncDisposable
{
    private readonly PendingOpsProcessor _processor;
    private readonly MailDatabase _db;
    private readonly KickLoop _loop;

    public PendingOpsWorker(PendingOpsProcessor processor, MailDatabase db, Func<DateTimeOffset>? clock = null, TimeSpan? maxIdle = null)
    {
        _processor = processor;
        _db = db;
        _loop = new KickLoop(
            async (ignore, ct) => await _processor.ProcessOnceAsync(ignore, ct).ConfigureAwait(false),
            ct => _db.GetNextPendingOpAttemptAsync(ct),
            clock,
            maxIdle);
    }

    public PendingOpsProcessor Processor => _processor;

    public void Start()
    {
        _db.PendingOpsChanged += OnChanged;
        _loop.Start();
        _loop.Kick();
    }

    public void Kick() => _loop.Kick();

    public void KickNetworkAvailable() => _loop.Kick(ignoreBackoff: true);

    private void OnChanged(object? sender, EventArgs e) => _loop.Kick();

    public async ValueTask DisposeAsync()
    {
        _db.PendingOpsChanged -= OnChanged;
        await _loop.DisposeAsync().ConfigureAwait(false);
    }
}
