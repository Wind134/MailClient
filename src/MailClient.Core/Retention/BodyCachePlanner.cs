namespace MailClient.Core.Retention;

/// <summary>一封已缓存正文、且允许被清出的邮件。</summary>
/// <param name="MessageId">邮件 Id。</param>
/// <param name="Bytes">正文（文本 + HTML，含内嵌图的 data URI）占用的字节数。</param>
/// <param name="LastAccess">上次打开 / 拉取正文的时间（没有记录时由调用方用收信时间代替）。</param>
public sealed record BodyCacheEntry(string MessageId, long Bytes, DateTimeOffset LastAccess);

/// <summary>清理方案。</summary>
public sealed record BodyEvictionPlan(
    IReadOnlyList<string> MessageIds,
    int EvictedByAge,
    int EvictedBySize,
    long FreedBytes,
    long RemainingBytes)
{
    public static BodyEvictionPlan Empty { get; } = new(Array.Empty<string>(), 0, 0, 0, 0);

    public int Count => MessageIds.Count;
}

/// <summary>
/// 正文缓存的清理策略（纯函数，不碰数据库）：
/// 1) 先清掉超过保留天数的；2) 剩下的总量仍超过大小上限，就从最久没打开的开始清，直到不超。
/// 同样久的按大的先清；结果稳定、可测试。
/// </summary>
public static class BodyCachePlanner
{
    public static BodyEvictionPlan Plan(IReadOnlyList<BodyCacheEntry> entries, DateTimeOffset now, StorageOptions options)
    {
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentNullException.ThrowIfNull(options);

        var total = entries.Sum(e => Math.Max(0, e.Bytes));
        var chosen = new List<string>();
        var byAge = 0;
        var bySize = 0;
        long freed = 0;

        var remaining = new List<BodyCacheEntry>(entries.Count);
        var maxAge = options.MaxAge;
        foreach (var entry in entries)
        {
            if (maxAge is { } age && now - entry.LastAccess > age)
            {
                chosen.Add(entry.MessageId);
                freed += Math.Max(0, entry.Bytes);
                byAge++;
            }
            else
            {
                remaining.Add(entry);
            }
        }

        var left = total - freed;
        var limit = options.MaxCacheBytes;
        if (left > limit)
        {
            foreach (var entry in remaining
                         .OrderBy(e => e.LastAccess)
                         .ThenByDescending(e => e.Bytes)
                         .ThenBy(e => e.MessageId, StringComparer.Ordinal))
            {
                if (left <= limit)
                {
                    break;
                }

                chosen.Add(entry.MessageId);
                var bytes = Math.Max(0, entry.Bytes);
                freed += bytes;
                left -= bytes;
                bySize++;
            }
        }

        return new BodyEvictionPlan(chosen, byAge, bySize, freed, Math.Max(0, total - freed));
    }

    /// <summary>用户点“清理缓存”：所有可清出的正文都清掉。</summary>
    public static BodyEvictionPlan PlanAll(IReadOnlyList<BodyCacheEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        var freed = entries.Sum(e => Math.Max(0, e.Bytes));
        return new BodyEvictionPlan(entries.Select(e => e.MessageId).ToList(), 0, 0, freed, 0);
    }
}
