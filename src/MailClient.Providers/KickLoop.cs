namespace MailClient.Providers;

/// <summary>
/// 后台“有事就干、没事睡觉”的循环：被 <see cref="Kick"/> 唤醒（入队 / 网络恢复），或到了下一个到期时间（退避结束）自动醒来。
/// 发件箱与待同步操作两个 worker 共用。
/// </summary>
public sealed class KickLoop : IAsyncDisposable
{
    private readonly Func<bool, CancellationToken, Task> _work;
    private readonly Func<CancellationToken, Task<DateTimeOffset?>> _nextDue;
    private readonly Func<DateTimeOffset> _clock;
    private readonly TimeSpan _maxIdle;
    private readonly SemaphoreSlim _signal = new(0);
    private readonly CancellationTokenSource _cts = new();
    private Task? _loop;
    private int _ignoreBackoff;

    public KickLoop(
        Func<bool, CancellationToken, Task> work,
        Func<CancellationToken, Task<DateTimeOffset?>> nextDue,
        Func<DateTimeOffset>? clock = null,
        TimeSpan? maxIdle = null)
    {
        _work = work;
        _nextDue = nextDue;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
        _maxIdle = maxIdle ?? TimeSpan.FromMinutes(5);
    }

    public bool IsStarted => _loop is not null;

    public void Start(Func<CancellationToken, Task>? startup = null)
    {
        if (_loop is not null)
        {
            return;
        }

        _loop = Task.Run(() => RunAsync(startup, _cts.Token));
    }

    /// <summary>唤醒循环。<paramref name="ignoreBackoff"/> = 网络恢复等场景：忽略退避时间立刻重试。</summary>
    public void Kick(bool ignoreBackoff = false)
    {
        if (ignoreBackoff)
        {
            Interlocked.Exchange(ref _ignoreBackoff, 1);
        }

        if (_signal.CurrentCount == 0)
        {
            try
            {
                _signal.Release();
            }
            catch (ObjectDisposedException)
            {
            }
        }
    }

    private async Task RunAsync(Func<CancellationToken, Task>? startup, CancellationToken ct)
    {
        try
        {
            if (startup is not null)
            {
                try
                {
                    await startup(ct).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                }
            }

            while (!ct.IsCancellationRequested)
            {
                var ignore = Interlocked.Exchange(ref _ignoreBackoff, 0) == 1;
                var wait = _maxIdle;
                try
                {
                    await _work(ignore, ct).ConfigureAwait(false);
                    var next = await _nextDue(ct).ConfigureAwait(false);
                    if (next is { } due)
                    {
                        wait = due - _clock();
                        if (wait < TimeSpan.FromSeconds(1))
                        {
                            wait = TimeSpan.FromSeconds(1);
                        }

                        if (wait > _maxIdle)
                        {
                            wait = _maxIdle;
                        }
                    }
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception)
                {
                    // 本轮失败（如数据库暂时不可用）：按最大空闲时间后再试
                }

                try
                {
                    await _signal.WaitAsync(wait, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        if (_loop is not null)
        {
            try
            {
                await _loop.ConfigureAwait(false);
            }
            catch (Exception)
            {
            }
        }

        _cts.Dispose();
        _signal.Dispose();
    }
}
