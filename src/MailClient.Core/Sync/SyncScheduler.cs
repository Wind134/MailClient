using MailClient.Core.Services;

namespace MailClient.Core.Sync;

/// <param name="AccountId">null = 所有账号。</param>
public sealed record SyncJob(SyncRequest Request, Guid? AccountId);

/// <summary>
/// 后台同步的“什么时候该同步什么”——纯逻辑，不碰网络和线程：
/// 定期轮询（所有账号、所有文件夹）、IDLE 通知（只同步该账号的收件箱）、手动 / 立即请求；
/// 离线或应用挂起时暂停，恢复后立刻补一轮。
/// </summary>
public sealed class SyncScheduler
{
    private readonly object _lock = new();
    private readonly Func<DateTimeOffset> _clock;
    private SyncOptions _options;
    private bool _online = true;
    private bool _suspended;
    private DateTimeOffset _nextPoll;
    private bool _fullPending;
    private bool _fullForce;
    private readonly HashSet<Guid> _inboxPending = [];

    public SyncScheduler(SyncOptions options, Func<DateTimeOffset>? clock = null)
    {
        _options = options;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
        _nextPoll = _clock();     // 启动后立刻来一轮
    }

    public bool IsPaused
    {
        get
        {
            lock (_lock)
            {
                return !_online || _suspended;
            }
        }
    }

    public void UpdateOptions(SyncOptions options)
    {
        lock (_lock)
        {
            if (_options.PollInterval != options.PollInterval)
            {
                var now = _clock();
                _nextPoll = _nextPoll > now + options.PollInterval ? now + options.PollInterval : _nextPoll;
            }

            _options = options;
        }
    }

    public void SetOnline(bool online)
    {
        lock (_lock)
        {
            if (online && !_online)
            {
                _nextPoll = _clock();       // 网络恢复：马上补一轮
            }

            _online = online;
        }
    }

    public void SetSuspended(bool suspended)
    {
        lock (_lock)
        {
            if (!suspended && _suspended)
            {
                _nextPoll = _clock();       // 从挂起 / 休眠恢复：马上补一轮
            }

            _suspended = suspended;
        }
    }

    /// <summary>请求同步全部账号（刷新按钮 = force）。</summary>
    public void RequestFull(bool force)
    {
        lock (_lock)
        {
            _fullPending = true;
            _fullForce |= force;
        }
    }

    /// <summary>IDLE 通知：某账号的收件箱有变化。</summary>
    public void RequestInbox(Guid accountId)
    {
        lock (_lock)
        {
            _inboxPending.Add(accountId);
        }
    }

    /// <summary>取出现在该做的任务（暂停时返回空，请求保留到恢复后）。</summary>
    public IReadOnlyList<SyncJob> TakeDue()
    {
        lock (_lock)
        {
            if (!_online || _suspended)
            {
                return [];
            }

            var now = _clock();
            if (_fullPending)
            {
                var force = _fullForce;
                _fullPending = false;
                _fullForce = false;
                _inboxPending.Clear();
                _nextPoll = now + _options.PollInterval;
                return [new SyncJob(new SyncRequest(Force: force, Background: true), null)];
            }

            if (now >= _nextPoll)
            {
                _inboxPending.Clear();
                _nextPoll = now + _options.PollInterval;
                return [new SyncJob(new SyncRequest(Force: false, Background: true), null)];
            }

            if (_inboxPending.Count == 0)
            {
                return [];
            }

            var jobs = _inboxPending
                .Select(id => new SyncJob(new SyncRequest(Force: true, InboxOnly: true, Background: true), id))
                .ToList();
            _inboxPending.Clear();
            return jobs;
        }
    }

    /// <summary>下一次需要醒来的时间（暂停时 null：靠 Kick 唤醒）。</summary>
    public DateTimeOffset? NextDue()
    {
        lock (_lock)
        {
            if (!_online || _suspended)
            {
                return null;
            }

            return _fullPending || _inboxPending.Count > 0 ? _clock() : _nextPoll;
        }
    }
}
