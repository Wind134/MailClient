namespace MailClient.Core.Auth;

/// <summary>
/// 记录哪些账号“登录已失效、需要重新登录”，并决定什么时候弹提醒（纯内存状态机，线程安全）。
/// <list type="bullet">
/// <item>同步 / IDLE / 加载正文遇到认证失败 → <see cref="ReportAuthFailure"/>：账号标记为需要重新登录。</item>
/// <item>同一次失效只提醒一次（<see cref="TryBeginPrompt"/>）；用户点了“稍后”（<see cref="Snooze"/>）就不再弹，直到重启应用，
/// 或者重新登录成功之后再次失效。</item>
/// <item>任何一次成功（同步完成 / 连接检查通过 / 重新登录成功）→ <see cref="ReportSuccess"/>：清掉标记和“稍后”。</item>
/// </list>
/// 状态不落盘：重启后由启动时的连接检查重新发现。
/// </summary>
public sealed class ReloginTracker
{
    private sealed class State
    {
        public bool NeedsRelogin;
        public bool Prompted;
        public bool Snoozed;
        public TaskCompletionSource? Cleared;
    }

    private readonly Dictionary<Guid, State> _states = [];
    private readonly object _lock = new();

    /// <summary>某账号“需要重新登录”的状态变了（参数为新状态）。可能在后台线程触发。</summary>
    public event EventHandler<ReloginStateChangedEventArgs>? StateChanged;

    public bool NeedsRelogin(Guid accountId)
    {
        lock (_lock)
        {
            return _states.TryGetValue(accountId, out var s) && s.NeedsRelogin;
        }
    }

    /// <summary>当前需要重新登录的账号。</summary>
    public IReadOnlyList<Guid> AccountsNeedingRelogin
    {
        get
        {
            lock (_lock)
            {
                return _states.Where(p => p.Value.NeedsRelogin).Select(p => p.Key).ToList();
            }
        }
    }

    /// <summary>认证失败。返回 true 表示这是一次新的失效（之前是正常状态）。</summary>
    public bool ReportAuthFailure(Guid accountId)
    {
        lock (_lock)
        {
            var s = Get(accountId);
            if (s.NeedsRelogin)
            {
                return false;
            }

            s.NeedsRelogin = true;
            s.Prompted = false;
            s.Cleared ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        StateChanged?.Invoke(this, new ReloginStateChangedEventArgs(accountId, true));
        return true;
    }

    /// <summary>连接 / 登录成功：清掉“需要重新登录”和“稍后”。返回 true 表示状态确实从失效变回了正常。</summary>
    public bool ReportSuccess(Guid accountId)
    {
        bool wasNeeded;
        TaskCompletionSource? cleared;
        lock (_lock)
        {
            if (!_states.TryGetValue(accountId, out var s))
            {
                return false;
            }

            wasNeeded = s.NeedsRelogin;
            cleared = s.Cleared;
            _states.Remove(accountId);
        }

        cleared?.TrySetResult();
        if (wasNeeded)
        {
            StateChanged?.Invoke(this, new ReloginStateChangedEventArgs(accountId, false));
        }

        return wasNeeded;
    }

    /// <summary>现在是否该弹“登录已失效”提醒：需要重新登录、这次失效还没提醒过、用户也没点过“稍后”。</summary>
    public bool ShouldPrompt(Guid accountId)
    {
        lock (_lock)
        {
            return _states.TryGetValue(accountId, out var s) && s.NeedsRelogin && !s.Prompted && !s.Snoozed;
        }
    }

    /// <summary>该弹提醒的账号（按失效先后无特定顺序）。</summary>
    public IReadOnlyList<Guid> PendingPrompts
    {
        get
        {
            lock (_lock)
            {
                return _states.Where(p => p.Value.NeedsRelogin && !p.Value.Prompted && !p.Value.Snoozed).Select(p => p.Key).ToList();
            }
        }
    }

    /// <summary>界面准备弹提醒时调用：返回 true 才弹（并记为“已提醒”，同一次失效不会再弹）。</summary>
    public bool TryBeginPrompt(Guid accountId)
    {
        lock (_lock)
        {
            if (!_states.TryGetValue(accountId, out var s) || !s.NeedsRelogin || s.Prompted || s.Snoozed)
            {
                return false;
            }

            s.Prompted = true;
            return true;
        }
    }

    /// <summary>用户点了“稍后”：重启应用或重新登录成功之前不再弹。</summary>
    public void Snooze(Guid accountId)
    {
        lock (_lock)
        {
            Get(accountId).Snoozed = true;
        }
    }

    /// <summary>等到该账号不再需要重新登录（已经正常时立即完成）。给 IDLE 等后台循环用，避免失效期间反复重连。</summary>
    public Task WaitUntilClearedAsync(Guid accountId, CancellationToken cancellationToken)
    {
        Task task;
        lock (_lock)
        {
            if (!_states.TryGetValue(accountId, out var s) || !s.NeedsRelogin)
            {
                return Task.CompletedTask;
            }

            s.Cleared ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            task = s.Cleared.Task;
        }

        return task.WaitAsync(cancellationToken);
    }

    private State Get(Guid accountId)
    {
        if (!_states.TryGetValue(accountId, out var s))
        {
            s = new State();
            _states[accountId] = s;
        }

        return s;
    }
}

public sealed class ReloginStateChangedEventArgs(Guid accountId, bool needsRelogin) : EventArgs
{
    public Guid AccountId { get; } = accountId;

    public bool NeedsRelogin { get; } = needsRelogin;
}
