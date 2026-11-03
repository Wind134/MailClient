using MailClient.Storage;

namespace MailClient.Providers.Outbox;

/// <summary>
/// 发件箱后台服务：启动时恢复卡在 Sending 的条目并立即处理一轮；之后由
/// 入队（<see cref="MailDatabase.OutboxChanged"/>）、网络恢复（<see cref="KickNetworkAvailable"/>）或退避到期唤醒。
/// </summary>
public sealed class OutboxWorker : IAsyncDisposable
{
    private readonly OutboxProcessor _processor;
    private readonly MailDatabase _db;
    private readonly KickLoop _loop;

    public OutboxWorker(OutboxProcessor processor, MailDatabase db, Func<DateTimeOffset>? clock = null, TimeSpan? maxIdle = null)
    {
        _processor = processor;
        _db = db;
        _loop = new KickLoop(
            async (ignore, ct) => await _processor.ProcessOnceAsync(ignore, ct).ConfigureAwait(false),
            ct => _db.GetNextOutboxAttemptAsync(ct),
            clock,
            maxIdle);
    }

    public OutboxProcessor Processor => _processor;

    public void Start()
    {
        _db.OutboxChanged += OnOutboxChanged;
        _loop.Start(async ct =>
        {
            await _processor.RecoverAsync(ct).ConfigureAwait(false);
        });
        _loop.Kick();
    }

    public void Kick() => _loop.Kick();

    public void KickNetworkAvailable() => _loop.Kick(ignoreBackoff: true);

    private void OnOutboxChanged(object? sender, EventArgs e) => _loop.Kick();

    public async ValueTask DisposeAsync()
    {
        _db.OutboxChanged -= OnOutboxChanged;
        await _loop.DisposeAsync().ConfigureAwait(false);
    }
}
