using MailClient.Core;

namespace MailClient.Providers.Graph;

/// <summary>delta 响应里的一条变化。</summary>
/// <param name="GraphId">Graph 邮件 Id。</param>
/// <param name="Removed">带 @removed：被删除，或被移到别的文件夹（Graph 里移动会换 Id，目标文件夹里是一封“新”邮件）。</param>
/// <param name="Item">完整邮件（新邮件 / 被移入本文件夹 / 属性完整的更新）；只带部分字段的更新为 null。</param>
/// <param name="IsRead">部分更新里携带的已读状态（没带 = null）。</param>
/// <param name="IsFlagged">部分更新里携带的星标状态（没带 = null）。</param>
public sealed record GraphDeltaEntry(
    string GraphId,
    bool Removed,
    MailMessageSyncItem? Item,
    bool? IsRead = null,
    bool? IsFlagged = null);

/// <param name="DeltaLink">下次增量用的 @odata.deltaLink；回退成普通列表时为 null。</param>
public sealed record GraphDeltaResult(IReadOnlyList<GraphDeltaEntry> Entries, string? DeltaLink);

/// <summary>delta token 失效（HTTP 410 / syncStateNotFound / resyncRequired）：需要丢掉游标做一次完整同步。</summary>
public sealed class GraphSyncStateNotFoundException : Exception
{
    public GraphSyncStateNotFoundException(string message, Exception? inner = null)
        : base(message, inner)
    {
    }
}

/// <summary>Graph 同步管线需要的 delta 查询（<see cref="MicrosoftGraphProvider"/> 实现；测试用假实现）。</summary>
public interface IGraphDeltaSource
{
    /// <summary>
    /// 取一个文件夹的 delta。<paramref name="deltaLink"/> 为空 = 首次 / 完整同步（只要 <paramref name="receivedSince"/> 之后收到的邮件）。
    /// token 失效时抛 <see cref="GraphSyncStateNotFoundException"/>。
    /// </summary>
    Task<GraphDeltaResult> FetchDeltaAsync(string remoteFolderId, string? deltaLink, int pageSize, DateTimeOffset? receivedSince, CancellationToken ct);
}
