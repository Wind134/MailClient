using MailClient.Core;
using MailClient.Core.Sync;
using MailClient.Storage.Demo;
using Microsoft.Data.Sqlite;

namespace MailClient.Storage;

/// <summary>
/// 本地优先的变更队列：UI 改本地库的同时（同一事务）写入 pending_ops，后台再写回服务器；
/// 同步写库时按 <see cref="SyncMerge"/> 规则避免覆盖尚未写回的本地变更。
/// </summary>
public sealed partial class MailDatabase
{
    private const string GraphProviderId = "microsoft.graph";

    /// <summary>pending_ops 有新增 / 变化时触发（后台写回线程订阅）。</summary>
    public event EventHandler? PendingOpsChanged;

    /// <summary>
    /// 应用一次邮件变更：本地库立刻生效，并（非演示账号、且能定位远程邮件时）在同一事务里排入写回队列，
    /// 同一邮件同一字段上的旧变更按“最后写入者胜出”合并。邮件不存在时返回 false。
    /// </summary>
    public async Task<bool> ApplyMessageChangeAsync(
        string messageId,
        PendingOpKind kind,
        string? targetFolderId = null,
        CancellationToken cancellationToken = default)
    {
        var applied = false;
        var queued = false;
        await ExecuteAsync(async (connection, ct) =>
        {
            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(ct).ConfigureAwait(false);

            Guid accountId;
            string folderId;
            long? remoteUid;
            int localState;
            await using (var read = Cmd(connection, transaction, "SELECT AccountId, FolderId, RemoteUid, LocalState FROM messages WHERE Id = $id;"))
            {
                read.Parameters.AddParam("$id", messageId);
                await using var reader = await read.ExecuteReaderAsync(ct).ConfigureAwait(false);
                if (!await reader.ReadAsync(ct).ConfigureAwait(false))
                {
                    return;
                }

                accountId = Guid.Parse(reader.GetString(0));
                folderId = reader.GetString(1);
                remoteUid = reader.IsDBNull(2) ? null : reader.GetInt64(2);
                localState = reader.GetInt32(3);
            }

            applied = true;

            // 1) 本地变更（同时记下该字段的本地改动时间，同步合并用它判断“服务器快照是不是比这次改动旧”）
            var stampNow = SqliteHelpers.Fmt(DateTimeOffset.UtcNow);
            switch (kind)
            {
                case PendingOpKind.MarkRead:
                case PendingOpKind.MarkUnread:
                    await NonQueryAsync(connection, transaction, "UPDATE messages SET IsRead = $v, ReadChangedAt = $t WHERE Id = $id;",
                        ("$v", kind == PendingOpKind.MarkRead ? 1 : 0), ("$t", stampNow), ("$id", messageId), ct).ConfigureAwait(false);
                    break;
                case PendingOpKind.Flag:
                case PendingOpKind.Unflag:
                    await NonQueryAsync(connection, transaction, "UPDATE messages SET IsFlagged = $v, FlagChangedAt = $t WHERE Id = $id;",
                        ("$v", kind == PendingOpKind.Flag ? 1 : 0), ("$t", stampNow), ("$id", messageId), ct).ConfigureAwait(false);
                    break;
                case PendingOpKind.Move:
                    if (string.IsNullOrEmpty(targetFolderId))
                    {
                        throw new ArgumentException("Move 需要目标文件夹。", nameof(targetFolderId));
                    }

                    await NonQueryAsync(connection, transaction, "UPDATE messages SET FolderId = $f, LocationChangedAt = $t WHERE Id = $id;",
                        ("$f", targetFolderId), ("$t", stampNow), ("$id", messageId), ct).ConfigureAwait(false);
                    break;
                case PendingOpKind.Delete:
                    await NonQueryAsync(connection, transaction, "DELETE FROM messages WHERE Id = $id;", ("$id", messageId), ct).ConfigureAwait(false);
                    break;
            }

            // 2) 写回队列
            if (!DemoDataSeeder.IsDemoAccount(accountId))
            {
                string provider;
                await using (var p = Cmd(connection, transaction, "SELECT ProviderId FROM accounts WHERE Id = $id;"))
                {
                    p.Parameters.AddParam("$id", accountId.ToString("D"));
                    provider = (await p.ExecuteScalarAsync(ct).ConfigureAwait(false)) as string ?? string.Empty;
                }

                var isGraph = string.Equals(provider, GraphProviderId, StringComparison.OrdinalIgnoreCase);
                // Graph：本地 Id = {账号}:{graphId}；刚在本地移动过（LocalState=2）的 Graph 邮件 id 已变，无法定位
                string? remoteKey = isGraph
                    ? (localState == 2 ? null : GraphKeyFromMessageId(messageId, accountId))
                    : remoteUid?.ToString(System.Globalization.CultureInfo.InvariantCulture);

                if (remoteKey is not null)
                {
                    var field = PendingOpKinds.FieldOf(kind);
                    var now = DateTimeOffset.UtcNow;
                    var existing = await GetPendingOpCoreAsync(connection, transaction, messageId, field, ct).ConfigureAwait(false);

                    var source = folderId;
                    if (field != PendingField.Location)
                    {
                        // 已有未写回的移动：已读 / 标记要在“原文件夹”里先于移动执行
                        var move = await GetPendingOpCoreAsync(connection, transaction, messageId, PendingField.Location, ct).ConfigureAwait(false);
                        if (move?.SourceFolderId is { } movedFrom)
                        {
                            source = movedFrom;
                        }
                    }

                    var incoming = new PendingOpEntity(0, accountId, messageId, kind, source,
                        kind == PendingOpKind.Move ? targetFolderId : null, remoteKey, 0, null, null, now, now);
                    var result = PendingOpCoalescer.Coalesce(existing, incoming);
                    switch (result.Action)
                    {
                        case CoalesceAction.Insert:
                        case CoalesceAction.Replace:
                            await UpsertPendingOpCoreAsync(connection, transaction, result.Op!, now, ct).ConfigureAwait(false);
                            queued = true;
                            break;
                        case CoalesceAction.Remove:
                            await NonQueryAsync(connection, transaction, "DELETE FROM pending_ops WHERE MessageId = $m AND Field = $f;",
                                ("$m", messageId), ("$f", field.ToString()), ct).ConfigureAwait(false);
                            queued = true;
                            break;
                    }
                }
            }

            await transaction.CommitAsync(ct).ConfigureAwait(false);
            await RecalculateFolderCountsCoreAsync(connection, ct).ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);

        if (queued)
        {
            PendingOpsChanged?.Invoke(this, EventArgs.Empty);
        }

        return applied;
    }

    public Task<IReadOnlyList<PendingOpEntity>> ListPendingOpsAsync(DateTimeOffset? dueAt = null, CancellationToken cancellationToken = default)
        => QueryAsync(async (connection, ct) =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText =
                "SELECT Id, AccountId, MessageId, Kind, SourceFolderId, TargetFolderId, RemoteKey, Attempts, LastError, NextAttemptAt, CreatedAt, UpdatedAt " +
                "FROM pending_ops " +
                (dueAt is null ? "" : "WHERE NextAttemptAt IS NULL OR NextAttemptAt <= $now ") +
                "ORDER BY AccountId, CASE Field WHEN 'Read' THEN 0 WHEN 'Flag' THEN 1 ELSE 2 END, CreatedAt, Id;";
            if (dueAt is not null)
            {
                command.Parameters.AddParam("$now", SqliteHelpers.Fmt(dueAt.Value));
            }

            var list = new List<PendingOpEntity>();
            await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                list.Add(ReadPendingOp(reader));
            }

            return (IReadOnlyList<PendingOpEntity>)list;
        }, cancellationToken);

    public Task<int> CountPendingOpsAsync(CancellationToken cancellationToken = default)
        => QueryAsync(async (connection, ct) =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM pending_ops;";
            return Convert.ToInt32(await command.ExecuteScalarAsync(ct).ConfigureAwait(false));
        }, cancellationToken);

    public Task<DateTimeOffset?> GetNextPendingOpAttemptAsync(CancellationToken cancellationToken = default)
        => QueryAsync(async (connection, ct) =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT MIN(NextAttemptAt) FROM pending_ops WHERE NextAttemptAt IS NOT NULL;";
            return (await command.ExecuteScalarAsync(ct).ConfigureAwait(false)) is string t ? SqliteHelpers.ParseDate(t) : null;
        }, cancellationToken);

    /// <summary>
    /// 写回成功 / 放弃：删除队列项（仅当它还是同一版本，避免删掉写回期间用户又改过的新变更）。
    /// 删除成功时把该字段的本地时间戳刷新为“确认时刻”：在此之前发起的服务器快照不会再把值翻回去。
    /// </summary>
    public Task CompletePendingOpAsync(PendingOpEntity op, CancellationToken cancellationToken = default)
        => ExecuteAsync(async (connection, ct) =>
        {
            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(ct).ConfigureAwait(false);
            int deleted;
            await using (var command = Cmd(connection, transaction, "DELETE FROM pending_ops WHERE Id = $id AND UpdatedAt = $u;"))
            {
                command.Parameters.AddParam("$id", op.Id);
                command.Parameters.AddParam("$u", SqliteHelpers.Fmt(op.UpdatedAt));
                deleted = await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }

            if (deleted > 0)
            {
                var column = op.Field switch
                {
                    PendingField.Read => "ReadChangedAt",
                    PendingField.Flag => "FlagChangedAt",
                    _ => "LocationChangedAt"
                };
                await NonQueryAsync(connection, transaction, $"UPDATE messages SET {column} = $t WHERE Id = $id;",
                    ("$t", SqliteHelpers.Fmt(DateTimeOffset.UtcNow)), ("$id", op.MessageId), ct).ConfigureAwait(false);
            }

            await transaction.CommitAsync(ct).ConfigureAwait(false);
        }, cancellationToken);

    public Task FailPendingOpAsync(PendingOpEntity op, string error, DateTimeOffset? nextAttemptAt, CancellationToken cancellationToken = default)
        => ExecuteAsync(async (connection, ct) =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText = """
                UPDATE pending_ops SET Attempts = Attempts + 1, LastError = $err, NextAttemptAt = $next
                WHERE Id = $id AND UpdatedAt = $u;
                """;
            command.Parameters.AddParam("$id", op.Id);
            command.Parameters.AddParam("$u", SqliteHelpers.Fmt(op.UpdatedAt));
            command.Parameters.AddParam("$err", error);
            command.Parameters.AddParam("$next", nextAttemptAt is null ? null : SqliteHelpers.Fmt(nextAttemptAt.Value));
            await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }, cancellationToken);

    /// <summary>IMAP / Graph 移动成功后：记录新的 UID（可能未知），并标记“等待服务器重新同步”，同步写库时会据此清理旧行。</summary>
    public Task MarkMovedRemoteAsync(string messageId, long? newRemoteUid, CancellationToken cancellationToken = default)
        => ExecuteAsync(async (connection, ct) =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "UPDATE messages SET RemoteUid = $uid, LocalState = 2 WHERE Id = $id;";
            command.Parameters.AddParam("$uid", newRemoteUid);
            command.Parameters.AddParam("$id", messageId);
            await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }, cancellationToken);

    /// <summary>每封邮件上未写回变更涉及的字段（同步合并用）。</summary>
    public Task<IReadOnlyDictionary<string, PendingFieldMask>> GetPendingMasksAsync(CancellationToken cancellationToken = default)
        => QueryAsync(async (connection, ct) => await GetPendingMasksCoreAsync(connection, null, ct).ConfigureAwait(false), cancellationToken);

    private static async Task<IReadOnlyDictionary<string, PendingFieldMask>> GetPendingMasksCoreAsync(
        SqliteConnection connection, SqliteTransaction? transaction, CancellationToken ct)
    {
        var map = new Dictionary<string, PendingFieldMask>(StringComparer.Ordinal);
        await using var command = Cmd(connection, transaction, "SELECT MessageId, Kind FROM pending_ops;");
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            var id = reader.GetString(0);
            var kind = SqliteHelpers.ParseEnum<PendingOpKind>(reader.GetString(1));
            var bit = kind switch
            {
                PendingOpKind.MarkRead or PendingOpKind.MarkUnread => PendingFieldMask.Read,
                PendingOpKind.Flag or PendingOpKind.Unflag => PendingFieldMask.Flag,
                PendingOpKind.Move => PendingFieldMask.Location,
                _ => PendingFieldMask.Deleted
            };
            map[id] = map.GetValueOrDefault(id) | bit;
        }

        return map;
    }

    /// <summary>
    /// 同步写库：合并规则（有未写回变更的字段保留本地值，其余以服务器为准）+ 清理重复行
    /// （本地移动后的旧行、本地生成的已发送副本都会被服务器版本取代）。
    /// </summary>
    public Task UpsertMessagesAsync(IEnumerable<MessageEntity> messages, CancellationToken cancellationToken = default)
        => UpsertMessagesAsync(messages, snapshotAt: null, cancellationToken);

    /// <param name="snapshotAt">服务器快照的时间（向服务器发起请求之前取）；给出时，本地时间戳晚于它的字段保留本地值。</param>
    public Task UpsertMessagesAsync(IEnumerable<MessageEntity> messages, DateTimeOffset? snapshotAt, CancellationToken cancellationToken = default)
        => ExecuteAsync(async (connection, ct) =>
        {
            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(ct).ConfigureAwait(false);
            var masks = await GetPendingMasksCoreAsync(connection, transaction, ct).ConfigureAwait(false);
            var foldersWithLocalRows = new Dictionary<string, bool>(StringComparer.Ordinal);

            foreach (var incoming in messages)
            {
                var mask = masks.GetValueOrDefault(incoming.Id);
                MessageEntity? existing = null;
                var stamps = default(LocalFieldStamps);
                if ((mask != PendingFieldMask.None || snapshotAt is not null) && !mask.HasFlag(PendingFieldMask.Deleted))
                {
                    await using var q = Cmd(connection, transaction, "SELECT FolderId, IsRead, IsFlagged, ReadChangedAt, FlagChangedAt, LocationChangedAt FROM messages WHERE Id = $id;");
                    q.Parameters.AddParam("$id", incoming.Id);
                    await using var r = await q.ExecuteReaderAsync(ct).ConfigureAwait(false);
                    if (await r.ReadAsync(ct).ConfigureAwait(false))
                    {
                        existing = incoming with
                        {
                            FolderId = r.GetString(0),
                            IsRead = r.GetInt32(1) != 0,
                            IsFlagged = r.GetInt32(2) != 0
                        };
                        stamps = new LocalFieldStamps(
                            r.IsDBNull(3) ? null : SqliteHelpers.ParseDate(r.GetString(3)),
                            r.IsDBNull(4) ? null : SqliteHelpers.ParseDate(r.GetString(4)),
                            r.IsDBNull(5) ? null : SqliteHelpers.ParseDate(r.GetString(5)));
                    }
                }

                var message = SyncMerge.Apply(incoming, existing, mask, stamps, snapshotAt);
                if (message is null)
                {
                    continue;
                }

                // 清理重复
                if (!foldersWithLocalRows.TryGetValue(message.FolderId, out var hasLocal))
                {
                    await using var q = Cmd(connection, transaction, "SELECT 1 FROM messages WHERE FolderId = $f AND LocalState <> 0 LIMIT 1;");
                    q.Parameters.AddParam("$f", message.FolderId);
                    hasLocal = await q.ExecuteScalarAsync(ct).ConfigureAwait(false) is not null;
                    foldersWithLocalRows[message.FolderId] = hasLocal;
                }

                if (message.RemoteUid is { } uid)
                {
                    await NonQueryAsync(connection, transaction,
                        "DELETE FROM messages WHERE FolderId = $f AND RemoteUid = $u AND Id <> $id AND Id NOT IN (SELECT MessageId FROM pending_ops);",
                        ("$f", message.FolderId), ("$u", uid), ("$id", message.Id), ct).ConfigureAwait(false);
                }

                if (hasLocal)
                {
                    if (!string.IsNullOrEmpty(message.RfcMessageId))
                    {
                        await NonQueryAsync(connection, transaction,
                            "DELETE FROM messages WHERE FolderId = $f AND LocalState <> 0 AND RfcMessageId = $rfc AND Id <> $id AND Id NOT IN (SELECT MessageId FROM pending_ops);",
                            ("$f", message.FolderId), ("$rfc", message.RfcMessageId), ("$id", message.Id), ct).ConfigureAwait(false);
                    }

                    // Graph 不允许自定义 Message-ID：本地已发送副本按“同主题 + 时间相近”认领
                    if (message.ReceivedAt is { } received)
                    {
                        await NonQueryAsync(connection, transaction,
                            """
                            DELETE FROM messages WHERE FolderId = $f AND LocalState = 1 AND Id <> $id AND Subject = $s
                              AND ReceivedAt IS NOT NULL AND ABS(julianday(ReceivedAt) - julianday($t)) < 0.007;
                            """,
                            ("$f", message.FolderId), ("$id", message.Id), ("$s", message.Subject), ("$t", SqliteHelpers.Fmt(received)), ct).ConfigureAwait(false);
                    }
                }

                await using var command = Cmd(connection, transaction, """
                    INSERT INTO messages (
                        Id, AccountId, FolderId, RemoteUid, RfcMessageId, ThreadKey,
                        Subject, FromAddress, FromName, Preview, BodyText, BodyHtml,
                        ReceivedAt, SentAt, IsRead, IsFlagged, IsDraft, HasAttachments, Category, SizeBytes, LocalState, BodyFetched)
                    VALUES (
                        $id, $accountId, $folderId, $remoteUid, $rfc, $thread,
                        $subject, $fromAddress, $fromName, $preview, $bodyText, $bodyHtml,
                        $receivedAt, $sentAt, $isRead, $isFlagged, $isDraft, $hasAttachments, $category, $size, 0, $bodyFetched)
                    ON CONFLICT(Id) DO UPDATE SET
                        FolderId = excluded.FolderId,
                        RemoteUid = excluded.RemoteUid,
                        RfcMessageId = excluded.RfcMessageId,
                        ThreadKey = excluded.ThreadKey,
                        Subject = excluded.Subject,
                        FromAddress = excluded.FromAddress,
                        FromName = excluded.FromName,
                        Preview = excluded.Preview,
                        BodyText = CASE WHEN excluded.BodyFetched = 1 THEN excluded.BodyText ELSE messages.BodyText END,
                        BodyHtml = CASE WHEN excluded.BodyFetched = 1 THEN excluded.BodyHtml ELSE messages.BodyHtml END,
                        BodyFetched = CASE WHEN excluded.BodyFetched = 1 THEN 1 ELSE messages.BodyFetched END,
                        ReceivedAt = excluded.ReceivedAt,
                        SentAt = excluded.SentAt,
                        IsRead = excluded.IsRead,
                        IsFlagged = excluded.IsFlagged,
                        IsDraft = excluded.IsDraft,
                        HasAttachments = excluded.HasAttachments,
                        Category = excluded.Category,
                        SizeBytes = excluded.SizeBytes,
                        LocalState = 0;
                    """);
                BindMessage(command, message);
                await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }

            await transaction.CommitAsync(ct).ConfigureAwait(false);
        }, cancellationToken);

    /// <summary>写入“本地生成的已发送副本”（LocalState=1）。服务器同步到真正的副本后会自动取代它。</summary>
    public async Task UpsertLocalSentCopyAsync(MessageEntity message, IEnumerable<RecipientEntity> recipients, IEnumerable<AttachmentEntity> attachments, CancellationToken cancellationToken = default)
    {
        await UpsertMessageAsync(message, cancellationToken).ConfigureAwait(false);
        await ExecuteAsync(async (connection, ct) =>
        {
            await NonQueryAsync(connection, null, "UPDATE messages SET LocalState = 1 WHERE Id = $id;", ("$id", message.Id), ct).ConfigureAwait(false);
            await NonQueryAsync(connection, null, "DELETE FROM recipients WHERE MessageId = $id;", ("$id", message.Id), ct).ConfigureAwait(false);
            await RecalculateFolderCountsCoreAsync(connection, ct).ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);
        foreach (var r in recipients)
        {
            await UpsertRecipientAsync(r with { Id = 0, MessageId = message.Id }, cancellationToken).ConfigureAwait(false);
        }

        foreach (var a in attachments)
        {
            await UpsertAttachmentAsync(a with { MessageId = message.Id }, cancellationToken).ConfigureAwait(false);
        }
    }

    public Task<int> GetLocalStateAsync(string messageId, CancellationToken cancellationToken = default)
        => QueryAsync(async (connection, ct) =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT LocalState FROM messages WHERE Id = $id;";
            command.Parameters.AddParam("$id", messageId);
            var value = await command.ExecuteScalarAsync(ct).ConfigureAwait(false);
            return value is null ? -1 : Convert.ToInt32(value);
        }, cancellationToken);

    /// <summary>邮件在服务器上的定位信息（IMAP：UID；Graph：消息 Id），供发件箱把副本 UID 回填等场景使用。</summary>
    public Task SetMessageRemoteUidAsync(string messageId, long? remoteUid, CancellationToken cancellationToken = default)
        => ExecuteAsync(async (connection, ct) =>
        {
            await NonQueryAsync(connection, null, "UPDATE messages SET RemoteUid = $u WHERE Id = $id;",
                ("$u", remoteUid), ("$id", messageId), ct).ConfigureAwait(false);
        }, cancellationToken);

    public static string? GraphKeyFromMessageId(string messageId, Guid accountId)
    {
        var prefix = accountId.ToString("D") + ":";
        return messageId.StartsWith(prefix, StringComparison.Ordinal) && messageId.Length > prefix.Length
            ? messageId[prefix.Length..]
            : null;
    }

    private static SqliteCommand Cmd(SqliteConnection connection, SqliteTransaction? transaction, string sql)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        return command;
    }

    private static async Task NonQueryAsync(SqliteConnection connection, SqliteTransaction? transaction, string sql, params object[] args)
    {
        // 最后一个参数是 CancellationToken，其余为 (name, value) 成对出现
        var ct = (CancellationToken)args[^1];
        await using var command = Cmd(connection, transaction, sql);
        for (var i = 0; i < args.Length - 1; i++)
        {
            var pair = (System.Runtime.CompilerServices.ITuple)args[i];
            command.Parameters.AddParam((string)pair[0]!, pair[1]);
        }

        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private static async Task<PendingOpEntity?> GetPendingOpCoreAsync(
        SqliteConnection connection, SqliteTransaction transaction, string messageId, PendingField field, CancellationToken ct)
    {
        await using var command = Cmd(connection, transaction,
            "SELECT Id, AccountId, MessageId, Kind, SourceFolderId, TargetFolderId, RemoteKey, Attempts, LastError, NextAttemptAt, CreatedAt, UpdatedAt " +
            "FROM pending_ops WHERE MessageId = $m AND Field = $f;");
        command.Parameters.AddParam("$m", messageId);
        command.Parameters.AddParam("$f", field.ToString());
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        return await reader.ReadAsync(ct).ConfigureAwait(false) ? ReadPendingOp(reader) : null;
    }

    private static async Task UpsertPendingOpCoreAsync(
        SqliteConnection connection, SqliteTransaction transaction, PendingOpEntity op, DateTimeOffset now, CancellationToken ct)
    {
        await using var command = Cmd(connection, transaction, """
            INSERT INTO pending_ops (AccountId, MessageId, Field, Kind, SourceFolderId, TargetFolderId, RemoteKey, Attempts, LastError, NextAttemptAt, CreatedAt, UpdatedAt)
            VALUES ($account, $message, $field, $kind, $source, $target, $key, 0, NULL, NULL, $created, $updated)
            ON CONFLICT(MessageId, Field) DO UPDATE SET
                Kind = excluded.Kind,
                SourceFolderId = excluded.SourceFolderId,
                TargetFolderId = excluded.TargetFolderId,
                RemoteKey = excluded.RemoteKey,
                Attempts = 0, LastError = NULL, NextAttemptAt = NULL,
                UpdatedAt = excluded.UpdatedAt;
            """);
        command.Parameters.AddParam("$account", op.AccountId.ToString("D"));
        command.Parameters.AddParam("$message", op.MessageId);
        command.Parameters.AddParam("$field", op.Field.ToString());
        command.Parameters.AddParam("$kind", op.Kind.ToString());
        command.Parameters.AddParam("$source", op.SourceFolderId);
        command.Parameters.AddParam("$target", op.TargetFolderId);
        command.Parameters.AddParam("$key", op.RemoteKey);
        command.Parameters.AddParam("$created", SqliteHelpers.Fmt(op.CreatedAt));
        command.Parameters.AddParam("$updated", SqliteHelpers.Fmt(now));
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private static PendingOpEntity ReadPendingOp(SqliteDataReader reader)
        => new(
            reader.GetInt64(0),
            Guid.Parse(reader.GetString(1)),
            reader.GetString(2),
            SqliteHelpers.ParseEnum<PendingOpKind>(reader.GetString(3)),
            reader.IsDBNull(4) ? null : reader.GetString(4),
            reader.IsDBNull(5) ? null : reader.GetString(5),
            reader.IsDBNull(6) ? null : reader.GetString(6),
            reader.GetInt32(7),
            reader.IsDBNull(8) ? null : reader.GetString(8),
            reader.IsDBNull(9) ? null : SqliteHelpers.ParseDate(reader.GetString(9)),
            SqliteHelpers.ParseDate(reader.GetString(10))!.Value,
            SqliteHelpers.ParseDate(reader.GetString(11))!.Value);
}
