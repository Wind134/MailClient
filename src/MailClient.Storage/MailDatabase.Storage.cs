using System.Globalization;
using MailClient.Core.Retention;
using MailClient.Storage.Demo;
using Microsoft.Data.Sqlite;

namespace MailClient.Storage;

/// <summary>当前占用情况（设置 → 存储）。</summary>
/// <param name="DatabaseBytes">数据库主文件大小（page_count × page_size）。</param>
/// <param name="WalBytes">-wal 文件大小（日志模式为 DELETE 时恒为 0）。</param>
/// <param name="FreeBytes">已空闲、可通过 incremental_vacuum / VACUUM 还给磁盘的字节数。</param>
/// <param name="BodyCacheBytes">所有已缓存正文（文本 + HTML + 内嵌图）的字节数。</param>
/// <param name="BodyCount">已缓存正文的邮件数。</param>
/// <param name="EvictableBytes">其中允许被清理的字节数（排除草稿 / 本地已发送副本 / 有待写回操作的邮件 / 演示邮件）。</param>
/// <param name="EvictableCount">允许被清理的邮件数。</param>
/// <param name="AutoVacuumMode">0 = NONE，1 = FULL，2 = INCREMENTAL。</param>
public sealed record StorageUsage(
    long DatabaseBytes,
    long WalBytes,
    long FreeBytes,
    long BodyCacheBytes,
    int BodyCount,
    long EvictableBytes,
    int EvictableCount,
    int AutoVacuumMode);

/// <summary>一次正文清理的结果。</summary>
public sealed record BodyEvictionResult(int Evicted, long FreedBytes, int ByAge, int BySize)
{
    public static BodyEvictionResult None { get; } = new(0, 0, 0, 0);
}

/// <summary>一次库维护的结果。</summary>
public sealed record DatabaseMaintenanceResult(
    bool ConvertedAutoVacuum,
    bool Vacuumed,
    long ReclaimedBytes,
    bool SearchIndexRebuilt,
    string? Skipped);

public sealed partial class MailDatabase
{
    private const string EvictableWhere = """
        BodyFetched = 1
        AND LocalState = 0
        AND IsDraft = 0
        AND Id NOT LIKE 'draft:%'
        AND Id NOT IN (SELECT MessageId FROM pending_ops)
        """;

    private const string BodyBytesExpr = "(COALESCE(length(CAST(BodyText AS BLOB)), 0) + COALESCE(length(CAST(BodyHtml AS BLOB)), 0))";

    /// <summary>读取占用情况。</summary>
    public Task<StorageUsage> GetStorageUsageAsync(CancellationToken cancellationToken = default)
        => QueryAsync(async (connection, ct) =>
        {
            var pageSize = await ScalarLongAsync(connection, "PRAGMA page_size;", ct).ConfigureAwait(false);
            var pageCount = await ScalarLongAsync(connection, "PRAGMA page_count;", ct).ConfigureAwait(false);
            var freeCount = await ScalarLongAsync(connection, "PRAGMA freelist_count;", ct).ConfigureAwait(false);
            var autoVacuum = (int)await ScalarLongAsync(connection, "PRAGMA auto_vacuum;", ct).ConfigureAwait(false);

            long bodyBytes = 0;
            var bodyCount = 0;
            await using (var all = connection.CreateCommand())
            {
                all.CommandText = $"SELECT COUNT(*), COALESCE(SUM({BodyBytesExpr}), 0) FROM messages WHERE BodyFetched = 1;";
                await using var r = await all.ExecuteReaderAsync(ct).ConfigureAwait(false);
                if (await r.ReadAsync(ct).ConfigureAwait(false))
                {
                    bodyCount = r.GetInt32(0);
                    bodyBytes = r.GetInt64(1);
                }
            }

            var (evictCount, evictBytes) = await ReadEvictableTotalsAsync(connection, ct).ConfigureAwait(false);
            return new StorageUsage(
                pageSize * pageCount,
                FileLength(DatabasePath + "-wal"),
                pageSize * freeCount,
                bodyBytes,
                bodyCount,
                evictBytes,
                evictCount,
                autoVacuum);
        }, cancellationToken);

    /// <summary>读邮件正文时调用：记录“最后访问时间”，避免刚看过的正文被当成陈旧缓存清掉。</summary>
    public Task TouchBodyAccessAsync(string messageId, CancellationToken cancellationToken = default)
        => ExecuteAsync(async (connection, ct) =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "UPDATE messages SET BodyAccessedAt = $now WHERE Id = $id AND BodyFetched = 1;";
            command.Parameters.AddParam("$id", messageId);
            command.Parameters.AddParam("$now", SqliteHelpers.Fmt(DateTimeOffset.UtcNow));
            await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }, cancellationToken);

    /// <summary>
    /// 按上限清理正文缓存（<paramref name="all"/>=true 时清掉全部可清理的正文，对应“清理缓存”按钮）。
    /// 只清 BodyText / BodyHtml 并把 BodyFetched 置 0：列表行、附件元数据、草稿、本地已发送副本、有待写回操作的邮件都不动；
    /// 之后打开这封邮件会走原来的按需拉取。
    /// </summary>
    public async Task<BodyEvictionResult> EvictBodyCacheAsync(
        StorageOptions options,
        DateTimeOffset now,
        bool all = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        var candidates = await ListBodyCacheEntriesAsync(cancellationToken).ConfigureAwait(false);
        var plan = all ? BodyCachePlanner.PlanAll(candidates) : BodyCachePlanner.Plan(candidates, now, options);
        if (plan.Count == 0)
        {
            return BodyEvictionResult.None;
        }

        var (evicted, freed) = await ClearBodiesAsync(plan.MessageIds, candidates, cancellationToken).ConfigureAwait(false);
        return new BodyEvictionResult(evicted, freed, plan.EvictedByAge, plan.EvictedBySize);
    }

    /// <summary>所有允许被清理的已缓存正文（Id / 大小 / 最后访问时间）。</summary>
    public Task<IReadOnlyList<BodyCacheEntry>> ListBodyCacheEntriesAsync(CancellationToken cancellationToken = default)
        => QueryAsync(async (connection, ct) =>
        {
            var list = new List<BodyCacheEntry>();
            await using var command = connection.CreateCommand();
            command.CommandText = $"""
                SELECT Id, AccountId, {BodyBytesExpr}, BodyAccessedAt, ReceivedAt, SentAt
                FROM messages
                WHERE {EvictableWhere};
                """;
            await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                if (Guid.TryParse(reader.GetString(1), out var accountId) && DemoDataSeeder.IsDemoAccount(accountId))
                {
                    continue; // 演示邮件没有服务器可拉，永不清理
                }

                var accessed = ParseDateOrNull(reader, 3) ?? ParseDateOrNull(reader, 4) ?? ParseDateOrNull(reader, 5) ?? DateTimeOffset.UtcNow;
                list.Add(new BodyCacheEntry(reader.GetString(0), reader.GetInt64(2), accessed));
            }

            return (IReadOnlyList<BodyCacheEntry>)list;
        }, cancellationToken);

    private Task<(int Evicted, long Freed)> ClearBodiesAsync(
        IReadOnlyList<string> ids,
        IReadOnlyList<BodyCacheEntry> candidates,
        CancellationToken cancellationToken)
    {
        var sizes = candidates.ToDictionary(c => c.MessageId, c => c.Bytes, StringComparer.Ordinal);
        return QueryAsync(async (connection, ct) =>
        {
            var evicted = 0;
            long freed = 0;
            const int batch = 400;
            for (var offset = 0; offset < ids.Count; offset += batch)
            {
                var chunk = ids.Skip(offset).Take(batch).ToList();
                await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(ct).ConfigureAwait(false);
                await using var command = Cmd(connection, transaction, string.Empty);
                var names = new List<string>(chunk.Count);
                for (var i = 0; i < chunk.Count; i++)
                {
                    names.Add($"$p{i}");
                    command.Parameters.AddParam($"$p{i}", chunk[i]);
                }

                // 写入时再校验一遍保护条件（枚举之后可能刚好产生了待写回操作）
                command.CommandText = $"""
                    UPDATE messages
                    SET BodyText = NULL, BodyHtml = NULL, BodyFetched = 0, BodyAccessedAt = NULL
                    WHERE Id IN ({string.Join(", ", names)}) AND {EvictableWhere};
                    """;
                var changed = await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                await transaction.CommitAsync(ct).ConfigureAwait(false);
                evicted += changed;
                if (changed == chunk.Count)
                {
                    freed += chunk.Sum(id => sizes.GetValueOrDefault(id));
                }
                else
                {
                    // 有被保护条件拦下的：按实际影响数估算（保守取平均）
                    freed += chunk.Sum(id => sizes.GetValueOrDefault(id)) * changed / Math.Max(1, chunk.Count);
                }
            }

            return (evicted, freed);
        }, cancellationToken);
    }

    /// <summary>当前 auto_vacuum 模式：0 NONE / 1 FULL / 2 INCREMENTAL。</summary>
    public Task<int> GetAutoVacuumModeAsync(CancellationToken cancellationToken = default)
        => QueryAsync(async (connection, ct) => (int)await ScalarLongAsync(connection, "PRAGMA auto_vacuum;", ct).ConfigureAwait(false), cancellationToken);

    /// <summary>
    /// 库维护：1) 老库还不是 INCREMENTAL 就做一次 VACUUM 转换；2) incremental_vacuum 把空闲页还给磁盘；
    /// 3) wal_checkpoint(TRUNCATE)（日志模式为 DELETE 时是空操作，将来切到 WAL 也不用再改）。
    /// <paramref name="forceFullVacuum"/>=true（“压缩数据库”按钮）则无论如何做一次完整 VACUUM。
    /// 全程持有数据库闸门，其间其他读写会短暂排队，所以调用方要放到后台线程。
    /// </summary>
    public Task<DatabaseMaintenanceResult> RunDatabaseMaintenanceAsync(
        bool forceFullVacuum = false,
        long minFreeBytesForIncremental = 1024 * 1024,
        CancellationToken cancellationToken = default)
        => QueryAsync(async (connection, ct) =>
        {
            var pageSize = await ScalarLongAsync(connection, "PRAGMA page_size;", ct).ConfigureAwait(false);
            var sizeBefore = pageSize * await ScalarLongAsync(connection, "PRAGMA page_count;", ct).ConfigureAwait(false);
            var mode = (int)await ScalarLongAsync(connection, "PRAGMA auto_vacuum;", ct).ConfigureAwait(false);
            var needConvert = mode != 2;

            var converted = false;
            var vacuumed = false;
            var rebuilt = false;
            string? skipped = null;

            if (needConvert || forceFullVacuum)
            {
                if (HasRoomForVacuum(sizeBefore))
                {
                    var fingerprint = await RowIdFingerprintAsync(connection, ct).ConfigureAwait(false);
                    await ExecAsync(connection, "PRAGMA auto_vacuum = INCREMENTAL;", ct).ConfigureAwait(false);
                    await ExecAsync(connection, "VACUUM;", ct).ConfigureAwait(false);
                    vacuumed = true;
                    converted = needConvert;

                    // 外部内容 FTS 依赖 messages.rowid；SQLite 的 VACUUM 实现会保留 rowid，这里仍做校验，万一变了就重建索引。
                    var after = await RowIdFingerprintAsync(connection, ct).ConfigureAwait(false);
                    if (!string.Equals(fingerprint, after, StringComparison.Ordinal))
                    {
                        await RebuildSearchIndexCoreAsync(connection, ct).ConfigureAwait(false);
                        rebuilt = true;
                    }
                }
                else
                {
                    skipped = "磁盘剩余空间不足，已跳过 VACUUM";
                }
            }

            if (!vacuumed)
            {
                var free = pageSize * await ScalarLongAsync(connection, "PRAGMA freelist_count;", ct).ConfigureAwait(false);
                if (mode == 2 && free >= minFreeBytesForIncremental)
                {
                    await ExecAsync(connection, "PRAGMA incremental_vacuum;", ct).ConfigureAwait(false);
                }
            }

            await ExecAsync(connection, "PRAGMA wal_checkpoint(TRUNCATE);", ct).ConfigureAwait(false);

            var sizeAfter = pageSize * await ScalarLongAsync(connection, "PRAGMA page_count;", ct).ConfigureAwait(false);
            return new DatabaseMaintenanceResult(converted, vacuumed, Math.Max(0, sizeBefore - sizeAfter), rebuilt, skipped);
        }, cancellationToken);

    /// <summary>重建 messages_fts / messages_trgm（rowid 对不上时的兜底，也可手动修复搜索索引）。</summary>
    public Task RebuildSearchIndexAsync(CancellationToken cancellationToken = default)
        => ExecuteAsync((connection, ct) => RebuildSearchIndexCoreAsync(connection, ct), cancellationToken);

    private async Task RebuildSearchIndexCoreAsync(SqliteConnection connection, CancellationToken ct)
    {
        await ExecAsync(connection, "INSERT INTO messages_fts(messages_fts) VALUES('rebuild');", ct).ConfigureAwait(false);
        if (SupportsTrigram)
        {
            await ExecAsync(connection, "INSERT INTO messages_trgm(messages_trgm) VALUES('rebuild');", ct).ConfigureAwait(false);
        }
    }

    /// <summary>SQLite 自检（测试 / 诊断用）：返回 "ok" 或错误描述。</summary>
    public Task<string> IntegrityCheckAsync(CancellationToken cancellationToken = default)
        => QueryAsync(async (connection, ct) =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA integrity_check;";
            return Convert.ToString(await command.ExecuteScalarAsync(ct).ConfigureAwait(false), CultureInfo.InvariantCulture) ?? "?";
        }, cancellationToken);

    private bool HasRoomForVacuum(long dbBytes)
    {
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(DatabasePath));
            if (string.IsNullOrEmpty(root))
            {
                return true;
            }

            // VACUUM 需要约 1 份库大小的临时空间 + 回滚日志，留足余量
            return new DriveInfo(root).AvailableFreeSpace > dbBytes * 2 + 32L * 1024 * 1024;
        }
        catch (Exception)
        {
            return true;
        }
    }

    private static async Task<string> RowIdFingerprintAsync(SqliteConnection connection, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT COUNT(*) || '|' || COALESCE(SUM(rowid), 0) || '|' || COALESCE(
                (SELECT group_concat(rowid || ':' || Id, ',') FROM (SELECT rowid, Id FROM messages ORDER BY rowid DESC LIMIT 40)), '')
            FROM messages;
            """;
        return Convert.ToString(await command.ExecuteScalarAsync(ct).ConfigureAwait(false), CultureInfo.InvariantCulture) ?? string.Empty;
    }

    private static async Task<(int Count, long Bytes)> ReadEvictableTotalsAsync(SqliteConnection connection, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT Id, AccountId, {BodyBytesExpr} FROM messages WHERE {EvictableWhere};";
        var count = 0;
        long bytes = 0;
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            if (Guid.TryParse(reader.GetString(1), out var accountId) && DemoDataSeeder.IsDemoAccount(accountId))
            {
                continue;
            }

            count++;
            bytes += reader.GetInt64(2);
        }

        return (count, bytes);
    }

    private static async Task<long> ScalarLongAsync(SqliteConnection connection, string sql, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        var value = await command.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return value is null or DBNull ? 0 : Convert.ToInt64(value, CultureInfo.InvariantCulture);
    }

    private static async Task ExecAsync(SqliteConnection connection, string sql, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private static DateTimeOffset? ParseDateOrNull(SqliteDataReader reader, int ordinal)
    {
        if (reader.IsDBNull(ordinal))
        {
            return null;
        }

        return DateTimeOffset.TryParse(reader.GetString(ordinal), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var value)
            ? value
            : null;
    }

    private static long FileLength(string path)
    {
        try
        {
            return File.Exists(path) ? new FileInfo(path).Length : 0;
        }
        catch (Exception)
        {
            return 0;
        }
    }
}
