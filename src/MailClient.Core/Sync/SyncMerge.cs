namespace MailClient.Core.Sync;

/// <summary>本地各字段最近一次被“用户改动 / 写回确认”的时间（来自 messages.*ChangedAt）。</summary>
public readonly record struct LocalFieldStamps(DateTimeOffset? ReadAt, DateTimeOffset? FlagAt, DateTimeOffset? LocationAt);

/// <summary>
/// 同步合并规则（纯函数，可单测）。一个字段保留本地值，当且仅当：
///  ① 该字段上还有未写回服务器的本地变更（pending 掩码），或
///  ② 该字段的本地时间戳晚于“本次服务器快照的时间”（<c>snapshotAt</c>，在向服务器发起请求之前取）——
///     说明服务器给出的是改动 / 写回确认之前的旧快照，不能把值翻回去。
/// 其余情况一律以服务器为准（服务器胜出）。
/// 写回成功（ack）时本地时间戳会刷新为“确认时刻”，因此 ack 之前发出的旧快照不会把刚写回的值翻回。
/// </summary>
public static class SyncMerge
{
    /// <returns>应写入数据库的邮件；返回 null 表示这封邮件本地已请求永久删除，同步应跳过它。</returns>
    public static MessageEntity? Apply(
        MessageEntity incoming,
        MessageEntity? existing,
        PendingFieldMask pending,
        LocalFieldStamps stamps = default,
        DateTimeOffset? snapshotAt = null)
    {
        ArgumentNullException.ThrowIfNull(incoming);
        if (pending.HasFlag(PendingFieldMask.Deleted))
        {
            return null;
        }

        if (existing is null)
        {
            return incoming;
        }

        var merged = incoming;
        if (KeepLocal(pending, PendingFieldMask.Read, stamps.ReadAt, snapshotAt))
        {
            merged = merged with { IsRead = existing.IsRead };
        }

        if (KeepLocal(pending, PendingFieldMask.Flag, stamps.FlagAt, snapshotAt))
        {
            merged = merged with { IsFlagged = existing.IsFlagged };
        }

        if (KeepLocal(pending, PendingFieldMask.Location, stamps.LocationAt, snapshotAt))
        {
            merged = merged with { FolderId = existing.FolderId };
        }

        return merged;
    }

    /// <summary>只合并已读 / 星标（IMAP 标志变更、Graph 增量里的字段更新用）。</summary>
    public static (bool IsRead, bool IsFlagged) ResolveFlags(
        bool localRead, bool localFlagged, bool serverRead, bool serverFlagged,
        PendingFieldMask pending, LocalFieldStamps stamps, DateTimeOffset? snapshotAt)
        => (KeepLocal(pending, PendingFieldMask.Read, stamps.ReadAt, snapshotAt) ? localRead : serverRead,
            KeepLocal(pending, PendingFieldMask.Flag, stamps.FlagAt, snapshotAt) ? localFlagged : serverFlagged);

    public static bool KeepLocal(PendingFieldMask pending, PendingFieldMask field, DateTimeOffset? localStamp, DateTimeOffset? snapshotAt)
        => pending.HasFlag(field) || (snapshotAt is { } snap && localStamp is { } stamp && stamp > snap);
}
