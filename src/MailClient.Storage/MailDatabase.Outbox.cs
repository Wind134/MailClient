using MailClient.Core;
using Microsoft.Data.Sqlite;

namespace MailClient.Storage;

/// <summary>发件箱表：入队、状态流转、重试、删除（发送本身在 Providers 层的 OutboxProcessor 里）。</summary>
public sealed partial class MailDatabase
{
    private const string OutboxColumns =
        "Id, AccountId, PayloadJson, Status, Attempts, LastError, CreatedAt, UpdatedAt, " +
        "NextAttemptAt, RfcMessageId, IsPermanent, MaybeSent, SentCopyPending, CopyAttempts";

    /// <summary>发件箱内容发生变化（入队 / 状态更新 / 删除）。后台发送线程与 UI 都订阅它。</summary>
    public event EventHandler? OutboxChanged;

    private void RaiseOutboxChanged() => OutboxChanged?.Invoke(this, EventArgs.Empty);

    public async Task EnqueueOutboxAsync(OutboxEntity item, CancellationToken cancellationToken = default)
    {
        await ExecuteAsync(async (connection, ct) =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO outbox (Id, AccountId, PayloadJson, Status, Attempts, LastError, CreatedAt, UpdatedAt,
                                    NextAttemptAt, RfcMessageId, IsPermanent, MaybeSent, SentCopyPending, CopyAttempts)
                VALUES ($id, $accountId, $payload, $status, $attempts, $lastError, $createdAt, $updatedAt,
                        $next, $rfc, $perm, $maybe, $copy, $copyAttempts)
                ON CONFLICT(Id) DO UPDATE SET
                    PayloadJson = excluded.PayloadJson,
                    Status = excluded.Status,
                    Attempts = excluded.Attempts,
                    LastError = excluded.LastError,
                    UpdatedAt = excluded.UpdatedAt,
                    NextAttemptAt = excluded.NextAttemptAt,
                    RfcMessageId = excluded.RfcMessageId,
                    IsPermanent = excluded.IsPermanent,
                    MaybeSent = excluded.MaybeSent,
                    SentCopyPending = excluded.SentCopyPending,
                    CopyAttempts = excluded.CopyAttempts;
                """;
            BindOutbox(command, item);
            await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);
        RaiseOutboxChanged();
    }

    /// <summary>只更新已存在的行（用户中途删除了就不再复活）。返回是否更新到。</summary>
    public async Task<bool> UpdateOutboxAsync(OutboxEntity item, CancellationToken cancellationToken = default)
    {
        var updated = false;
        await ExecuteAsync(async (connection, ct) =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText = """
                UPDATE outbox SET
                    PayloadJson = $payload, Status = $status, Attempts = $attempts, LastError = $lastError,
                    UpdatedAt = $updatedAt, NextAttemptAt = $next, RfcMessageId = $rfc, IsPermanent = $perm,
                    MaybeSent = $maybe, SentCopyPending = $copy, CopyAttempts = $copyAttempts
                WHERE Id = $id;
                """;
            BindOutbox(command, item);
            updated = await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false) > 0;
        }, cancellationToken).ConfigureAwait(false);
        if (updated)
        {
            RaiseOutboxChanged();
        }

        return updated;
    }

    public Task<IReadOnlyList<OutboxEntity>> ListPendingOutboxAsync(CancellationToken cancellationToken = default)
        => QueryOutboxAsync("WHERE Status IN ('Pending', 'Failed') ORDER BY CreatedAt", null, cancellationToken);

    /// <summary>发件箱里仍需要用户关注的条目（待发 / 发送中 / 失败），不含已发送。</summary>
    public Task<IReadOnlyList<OutboxEntity>> ListOutboxItemsAsync(CancellationToken cancellationToken = default)
        => QueryOutboxAsync("WHERE Status <> 'Sent' ORDER BY CreatedAt", null, cancellationToken);

    public async Task<OutboxEntity?> GetOutboxItemAsync(string id, CancellationToken cancellationToken = default)
        => (await QueryOutboxAsync("WHERE Id = $id", c => c.Parameters.AddParam("$id", id), cancellationToken).ConfigureAwait(false)).FirstOrDefault();

    /// <summary>到期、可以处理的条目：待发（退避时间已到）与“已发出但副本还没存到服务器”的条目。</summary>
    public Task<IReadOnlyList<OutboxEntity>> ListDueOutboxAsync(DateTimeOffset now, bool ignoreBackoff = false, CancellationToken cancellationToken = default)
        => QueryOutboxAsync(
            "WHERE ((Status = 'Pending' OR (Status = 'Sent' AND SentCopyPending = 1)) " +
            (ignoreBackoff ? "" : "AND (NextAttemptAt IS NULL OR NextAttemptAt <= $now) ") +
            ") ORDER BY CreatedAt",
            c => c.Parameters.AddParam("$now", SqliteHelpers.Fmt(now)),
            cancellationToken);

    public Task<int> CountOutboxItemsAsync(CancellationToken cancellationToken = default)
        => QueryAsync(async (connection, ct) =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM outbox WHERE Status <> 'Sent';";
            return Convert.ToInt32(await command.ExecuteScalarAsync(ct).ConfigureAwait(false));
        }, cancellationToken);

    /// <summary>最早的下一次重试时间（用于后台线程睡眠多久）；没有等待中的条目则为 null。</summary>
    public Task<DateTimeOffset?> GetNextOutboxAttemptAsync(CancellationToken cancellationToken = default)
        => QueryAsync(async (connection, ct) =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT MIN(NextAttemptAt) FROM outbox WHERE NextAttemptAt IS NOT NULL AND (Status = 'Pending' OR (Status = 'Sent' AND SentCopyPending = 1));";
            var value = await command.ExecuteScalarAsync(ct).ConfigureAwait(false);
            return value is string text ? SqliteHelpers.ParseDate(text) : null;
        }, cancellationToken);

    /// <summary>Pending → Sending（原子地“抢占”，避免两个循环同时发同一封）。成功返回 true。</summary>
    public async Task<bool> TryMarkOutboxSendingAsync(string id, DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        var changed = false;
        await ExecuteAsync(async (connection, ct) =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText = """
                UPDATE outbox SET Status = 'Sending', Attempts = Attempts + 1, UpdatedAt = $now, NextAttemptAt = NULL, LastError = NULL
                WHERE Id = $id AND Status = 'Pending';
                """;
            command.Parameters.AddParam("$id", id);
            command.Parameters.AddParam("$now", SqliteHelpers.Fmt(now));
            changed = await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false) > 0;
        }, cancellationToken).ConfigureAwait(false);
        if (changed)
        {
            RaiseOutboxChanged();
        }

        return changed;
    }

    /// <summary>启动恢复：上次进程在“发送中”被打断的条目退回 Pending，并标记“可能已发出”（需要先核对再决定是否重发）。</summary>
    public async Task<int> ResetSendingOutboxAsync(DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        var count = 0;
        await ExecuteAsync(async (connection, ct) =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "UPDATE outbox SET Status = 'Pending', MaybeSent = 1, UpdatedAt = $now, NextAttemptAt = NULL WHERE Status = 'Sending';";
            command.Parameters.AddParam("$now", SqliteHelpers.Fmt(now));
            count = await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);
        if (count > 0)
        {
            RaiseOutboxChanged();
        }

        return count;
    }

    /// <summary>用户点“重试”：失败 → 待发，清空计数 / 错误，也不再要求核对“可能已发出”。</summary>
    public async Task<bool> RetryOutboxAsync(string id, DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        var changed = false;
        await ExecuteAsync(async (connection, ct) =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText = """
                UPDATE outbox SET Status = 'Pending', Attempts = 0, LastError = NULL, NextAttemptAt = NULL,
                                  IsPermanent = 0, MaybeSent = 0, UpdatedAt = $now
                WHERE Id = $id AND Status = 'Failed';
                """;
            command.Parameters.AddParam("$id", id);
            command.Parameters.AddParam("$now", SqliteHelpers.Fmt(now));
            changed = await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false) > 0;
        }, cancellationToken).ConfigureAwait(false);
        if (changed)
        {
            RaiseOutboxChanged();
        }

        return changed;
    }

    public async Task<bool> DeleteOutboxAsync(string id, CancellationToken cancellationToken = default)
    {
        var changed = false;
        await ExecuteAsync(async (connection, ct) =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM outbox WHERE Id = $id AND Status <> 'Sending';";
            command.Parameters.AddParam("$id", id);
            changed = await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false) > 0;
        }, cancellationToken).ConfigureAwait(false);
        if (changed)
        {
            RaiseOutboxChanged();
        }

        return changed;
    }

    /// <summary>清理已完成的发送记录（已发送且副本已处理）。</summary>
    public async Task<int> PruneSentOutboxAsync(DateTimeOffset olderThan, CancellationToken cancellationToken = default)
    {
        var count = 0;
        await ExecuteAsync(async (connection, ct) =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM outbox WHERE Status = 'Sent' AND SentCopyPending = 0 AND UpdatedAt < $t;";
            command.Parameters.AddParam("$t", SqliteHelpers.Fmt(olderThan));
            count = await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);
        return count;
    }

    private Task<IReadOnlyList<OutboxEntity>> QueryOutboxAsync(string tail, Action<SqliteCommand>? bind, CancellationToken cancellationToken)
        => QueryAsync(async (connection, ct) =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText = $"SELECT {OutboxColumns} FROM outbox {tail};";
            bind?.Invoke(command);
            var list = new List<OutboxEntity>();
            await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                list.Add(new OutboxEntity(
                    reader.GetString(0),
                    Guid.Parse(reader.GetString(1)),
                    reader.GetString(2),
                    SqliteHelpers.ParseEnum<OutboxStatus>(reader.GetString(3)),
                    reader.GetInt32(4),
                    reader.IsDBNull(5) ? null : reader.GetString(5),
                    SqliteHelpers.ParseDate(reader.GetString(6))!.Value,
                    SqliteHelpers.ParseDate(reader.GetString(7))!.Value,
                    reader.IsDBNull(8) ? null : SqliteHelpers.ParseDate(reader.GetString(8)),
                    reader.IsDBNull(9) ? null : reader.GetString(9),
                    reader.GetInt32(10) != 0,
                    reader.GetInt32(11) != 0,
                    reader.GetInt32(12) != 0,
                    reader.GetInt32(13)));
            }

            return (IReadOnlyList<OutboxEntity>)list;
        }, cancellationToken);

    private static void BindOutbox(SqliteCommand command, OutboxEntity item)
    {
        command.Parameters.AddParam("$id", item.Id);
        command.Parameters.AddParam("$accountId", item.AccountId.ToString("D"));
        command.Parameters.AddParam("$payload", item.PayloadJson);
        command.Parameters.AddParam("$status", SqliteHelpers.EnumName(item.Status));
        command.Parameters.AddParam("$attempts", item.Attempts);
        command.Parameters.AddParam("$lastError", item.LastError);
        command.Parameters.AddParam("$createdAt", SqliteHelpers.Fmt(item.CreatedAt));
        command.Parameters.AddParam("$updatedAt", SqliteHelpers.Fmt(item.UpdatedAt));
        command.Parameters.AddParam("$next", item.NextAttemptAt is null ? null : SqliteHelpers.Fmt(item.NextAttemptAt.Value));
        command.Parameters.AddParam("$rfc", item.RfcMessageId);
        command.Parameters.AddParam("$perm", item.IsPermanent ? 1 : 0);
        command.Parameters.AddParam("$maybe", item.MaybeSent ? 1 : 0);
        command.Parameters.AddParam("$copy", item.SentCopyPending ? 1 : 0);
        command.Parameters.AddParam("$copyAttempts", item.CopyAttempts);
    }
}
