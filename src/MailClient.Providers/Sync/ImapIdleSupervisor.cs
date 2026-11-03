using MailClient.Core;
using MailClient.Core.Services;
using MailClient.Core.Sync;

namespace MailClient.Providers.Sync;

/// <summary>某个账号的一次 IDLE 会话：连接 → IDLE → 有变化就调用 onChange → 一直到断开 / 取消。</summary>
public interface IIdleRunner
{
    Task<IdleOutcome> RunAsync(AccountEntity account, Func<CancellationToken, Task> onChange, CancellationToken cancellationToken);
}

/// <summary>
/// 看管一个账号的 IDLE 连接：会话断了（网络抖动 / 服务器踢人 / 休眠恢复）就按指数退避重连；
/// 服务器不支持 IDLE 则停下来，由定期轮询兜底。<see cref="Kick"/>（网络恢复）跳过当前的退避等待。
/// </summary>
public sealed class ImapIdleSupervisor : IAsyncDisposable
{
    private readonly Func<Func<CancellationToken, Task>, CancellationToken, Task<IdleOutcome>> _run;
    private readonly Func<CancellationToken, Task> _onChange;
    private readonly SyncBackoff _backoff;
    private readonly ISyncLog _log;
    private readonly string _name;
    private readonly Func<TimeSpan, CancellationToken, Task>? _wait;
    private readonly SemaphoreSlim _kick = new(0);
    private readonly CancellationTokenSource _cts = new();
    private Task? _loop;

    public ImapIdleSupervisor(
        string name,
        Func<Func<CancellationToken, Task>, CancellationToken, Task<IdleOutcome>> run,
        Func<CancellationToken, Task> onChange,
        SyncBackoff? backoff = null,
        ISyncLog? log = null,
        Func<TimeSpan, CancellationToken, Task>? wait = null)
    {
        _name = name;
        _run = run;
        _onChange = onChange;
        _backoff = backoff ?? new SyncBackoff();
        _log = log ?? NullSyncLog.Instance;
        _wait = wait;
    }

    /// <summary>服务器不支持 IDLE（已停止）。</summary>
    public bool Unsupported { get; private set; }

    public int ConsecutiveFailures => _backoff.Failures;

    public bool IsRunning => _loop is { IsCompleted: false };

    public void Start()
    {
        _loop ??= Task.Run(() => LoopAsync(_cts.Token));
    }

    public void Kick()
    {
        if (_kick.CurrentCount == 0)
        {
            try
            {
                _kick.Release();
            }
            catch (ObjectDisposedException)
            {
            }
        }
    }

    /// <summary>可单测的循环体：返回时表示已取消或不支持 IDLE。</summary>
    public async Task LoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TimeSpan delay;
            try
            {
                var outcome = await _run(_onChange, ct).ConfigureAwait(false);
                if (outcome == IdleOutcome.Unsupported)
                {
                    Unsupported = true;
                    _log.Info($"{_name}: 服务器不支持 IDLE，只用定期轮询。");
                    return;
                }

                // 正常结束（例如服务器关闭了连接）：稍等一下再重连，避免空转
                _backoff.Success();
                delay = TimeSpan.FromSeconds(1);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                delay = _backoff.Failure();
                _log.Warn($"{_name}: IDLE 连接失败（第 {_backoff.Failures} 次），{delay.TotalSeconds:0} 秒后重试：{ex.Message}");
            }

            try
            {
                if (_wait is not null)
                {
                    await _wait(delay, ct).ConfigureAwait(false);
                }
                else
                {
                    await _kick.WaitAsync(delay, ct).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
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
        _kick.Dispose();
    }
}
