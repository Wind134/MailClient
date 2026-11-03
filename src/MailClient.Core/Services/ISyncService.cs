namespace MailClient.Core.Services;

public interface ISyncService
{
    Task SyncAccountAsync(Guid accountId, CancellationToken cancellationToken = default);
    Task SyncAllAsync(CancellationToken cancellationToken = default);
}

public interface ISendService
{
    Task EnqueueAsync(OutgoingMailMessage message, CancellationToken cancellationToken = default);
}

public interface ISearchService
{
    Task<IReadOnlyList<MailMessageSummary>> SearchAsync(string query, CancellationToken cancellationToken = default);
}

public enum SyncPhase
{
    Started,
    Progress,
    Completed,
    Failed
}

/// <param name="Changed">仅 Completed：这一轮同步改动了本地库（新邮件 / 标志变化 / 删除），界面需要刷新。</param>
/// <param name="Done">已完成的步骤数（文件夹数）。</param>
/// <param name="Total">总步骤数；未知时为 0。</param>
public sealed record SyncProgress(
    Guid AccountId,
    string AccountName,
    SyncPhase Phase,
    string Message,
    int Done = 0,
    int Total = 0,
    string? Error = null,
    bool Background = false,
    bool Changed = false);

/// <summary>能报告进度的同步服务（事件可能在后台线程触发）。</summary>
public interface IProgressSyncService : ISyncService
{
    event EventHandler<SyncProgress>? ProgressChanged;
}

/// <summary>一次同步请求的选项。</summary>
/// <param name="Force">忽略“多久对账一次”的节流，立即做完整的增量检查（刷新按钮 / IDLE 通知用）。</param>
/// <param name="InboxOnly">只同步各账号的收件箱（IDLE 通知触发时使用，省流量）。</param>
/// <param name="Background">后台自动触发（轮询 / IDLE）：UI 不弹“正在同步”提示，只在有变化后静默刷新。</param>
public sealed record SyncRequest(bool Force = false, bool InboxOnly = false, bool Background = false)
{
    public static SyncRequest Default { get; } = new();
}

/// <summary>支持带选项同步的服务（双向同步：强制对账 / 只同步收件箱 / 后台静默）。</summary>
public interface IIncrementalSyncService : IProgressSyncService
{
    Task SyncAccountAsync(Guid accountId, SyncRequest request, CancellationToken cancellationToken = default);

    Task SyncAllAsync(SyncRequest request, CancellationToken cancellationToken = default);
}
