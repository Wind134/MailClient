using MailClient.Core;
using MailClient.Core.Services;
using MailClient.Core.Sync;
using MailClient.Storage;
using MailClient.Storage.Demo;

namespace MailClient.Providers.Sync;

/// <summary>
/// 后台同步总管：定期轮询 + 每个 IMAP 账号一条 IDLE 连接（收件箱）+ 网络 / 休眠暂停与恢复。
/// 决定“同步什么”的逻辑在 <see cref="SyncScheduler"/>，决定“IDLE 怎么重连”的逻辑在 <see cref="ImapIdleSupervisor"/>。
/// </summary>
public sealed class SyncCoordinator : IAsyncDisposable
{
    private readonly IIncrementalSyncService _sync;
    private readonly MailDatabase _db;
    private readonly IIdleRunner? _idle;
    private readonly Func<SyncOptions> _options;
    private readonly ISyncLog _log;
    private readonly SyncScheduler _scheduler;
    private readonly KickLoop _loop;
    private readonly Dictionary<Guid, ImapIdleSupervisor> _supervisors = [];
    private readonly SemaphoreSlim _supervisorGate = new(1, 1);

    public SyncCoordinator(
        IIncrementalSyncService sync,
        MailDatabase database,
        IIdleRunner? idle,
        Func<SyncOptions> options,
        ISyncLog? log = null,
        Func<DateTimeOffset>? clock = null)
    {
        _sync = sync;
        _db = database;
        _idle = idle;
        _options = options;
        _log = log ?? NullSyncLog.Instance;
        _scheduler = new SyncScheduler(options(), clock);
        _loop = new KickLoop(WorkAsync, NextDueAsync, clock, TimeSpan.FromMinutes(1));
    }

    public bool IsPaused => _scheduler.IsPaused;

    public int ActiveIdleConnections
    {
        get
        {
            lock (_supervisors)
            {
                return _supervisors.Values.Count(s => s.IsRunning);
            }
        }
    }

    public void Start() => _loop.Start();

    /// <summary>网络状态变化。恢复时马上补一轮同步，并让 IDLE 立刻重连。</summary>
    public void SetOnline(bool online)
    {
        _scheduler.SetOnline(online);
        if (online)
        {
            lock (_supervisors)
            {
                foreach (var s in _supervisors.Values)
                {
                    s.Kick();
                }
            }
        }

        _loop.Kick(ignoreBackoff: online);
    }

    /// <summary>系统休眠 / 应用挂起：暂停并断开 IDLE；恢复后重连并补一轮。</summary>
    public void SetSuspended(bool suspended)
    {
        _scheduler.SetSuspended(suspended);
        _loop.Kick(ignoreBackoff: !suspended);
    }

    /// <summary>立刻同步（刷新按钮之外的入口，例如设置里改了轮询间隔后）。</summary>
    public void RequestSync(bool force)
    {
        _scheduler.RequestFull(force);
        _loop.Kick();
    }

    private async Task WorkAsync(bool ignoreBackoff, CancellationToken ct)
    {
        _scheduler.UpdateOptions(_options());
        await EnsureSupervisorsAsync(ct).ConfigureAwait(false);
        foreach (var job in _scheduler.TakeDue())
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                if (job.AccountId is { } id)
                {
                    await _sync.SyncAccountAsync(id, job.Request, ct).ConfigureAwait(false);
                }
                else
                {
                    await _sync.SyncAllAsync(job.Request, ct).ConfigureAwait(false);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log.Warn($"后台同步失败：{ex.Message}");      // 失败已通过 ProgressChanged 报告；下一轮再试
            }
        }
    }

    private Task<DateTimeOffset?> NextDueAsync(CancellationToken ct) => Task.FromResult(_scheduler.NextDue());

    /// <summary>让“IDLE 连接集合”与“应该有 IDLE 的账号”一致：启用、非演示、非 Graph；暂停 / 关闭 IDLE 时全部断开。</summary>
    private async Task EnsureSupervisorsAsync(CancellationToken ct)
    {
        if (_idle is null)
        {
            return;
        }

        await _supervisorGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var wanted = new Dictionary<Guid, AccountEntity>();
            if (!_scheduler.IsPaused && _options().IdleEnabled)
            {
                foreach (var a in await _db.ListAccountsAsync(ct).ConfigureAwait(false))
                {
                    if (a.IsEnabled && !DemoDataSeeder.IsDemoAccount(a.Id) && a.ProviderId != "microsoft.graph")
                    {
                        wanted[a.Id] = a;
                    }
                }
            }

            List<ImapIdleSupervisor> stop = [];
            lock (_supervisors)
            {
                foreach (var id in _supervisors.Keys.Where(id => !wanted.ContainsKey(id)).ToList())
                {
                    stop.Add(_supervisors[id]);
                    _supervisors.Remove(id);
                }
            }

            foreach (var s in stop)
            {
                await s.DisposeAsync().ConfigureAwait(false);
            }

            foreach (var (id, account) in wanted)
            {
                lock (_supervisors)
                {
                    if (_supervisors.ContainsKey(id))
                    {
                        continue;      // 已在运行；或已确认不支持 IDLE（不再重试，靠轮询）
                    }
                }

                var supervisor = new ImapIdleSupervisor(
                    account.EmailAddress,
                    (onChange, token) => _idle.RunAsync(account, onChange, token),
                    token =>
                    {
                        _scheduler.RequestInbox(id);
                        _loop.Kick();
                        return Task.CompletedTask;
                    },
                    log: _log);
                lock (_supervisors)
                {
                    _supervisors[id] = supervisor;
                }

                supervisor.Start();
            }
        }
        finally
        {
            _supervisorGate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _loop.DisposeAsync().ConfigureAwait(false);
        List<ImapIdleSupervisor> all;
        lock (_supervisors)
        {
            all = _supervisors.Values.ToList();
            _supervisors.Clear();
        }

        foreach (var s in all)
        {
            await s.DisposeAsync().ConfigureAwait(false);
        }
    }
}
