using MailClient.Core;
using MailClient.Core.Sync;
using Microsoft.Data.Sqlite;

namespace MailClient.Storage;

/// <summary>双向同步：把服务器上的已读 / 星标 / 删除 / 移动变化写回本地库（冲突规则见 <see cref="SyncMerge"/>）。</summary>
public sealed partial class MailDatabase
{
    public Task<FolderEntity?> GetFolderAsync(string folderId, CancellationToken cancellationToken = default)
        => QueryAsync(async (connection, ct) =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT Id, AccountId, Name, Special, RemotePath, UnreadCount, TotalCount, SortOrder, SyncCursor, UidValidity, UidNext, HighestModSeq, LastReconcileAt
                FROM folders WHERE Id = $id;
                """;
            command.Parameters.AddParam("$id", folderId);
            await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
            return await reader.ReadAsync(ct).ConfigureAwait(false) ? ReadFolder(reader) : null;
        }, cancellationToken);

    /// <summary>只更新同步相关的列（游标、UIDVALIDITY、UIDNEXT、HIGHESTMODSEQ、上次对账时间）。</summary>
    public Task SaveFolderSyncStateAsync(FolderSyncState state, CancellationToken cancellationToken = default)
        => ExecuteAsync(async (connection, ct) =>
        {
            await NonQueryAsync(connection, null, """
                UPDATE folders SET SyncCursor = $c, UidValidity = $v, UidNext = $n, HighestModSeq = $m, LastReconcileAt = $r WHERE Id = $id;
                """,
                ("$c", state.SyncCursor), ("$v", state.UidValidity), ("$n", state.UidNext), ("$m", state.HighestModSeq),
                ("$r", state.LastReconcileAt is null ? null : SqliteHelpers.Fmt(state.LastReconcileAt.Value)), ("$id", state.FolderId), ct).ConfigureAwait(false);
        }, cancellationToken);

    /// <summary>某文件夹里“来自服务器”（LocalState=0）且有 UID 的邮件，按 UID 升序。</summary>
    public Task<IReadOnlyList<LocalUidRow>> ListFolderUidRowsAsync(string folderId, CancellationToken cancellationToken = default)
        => QueryAsync(async (connection, ct) =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT Id, RemoteUid, IsRead, IsFlagged, GmMsgId, ReceivedAt
                FROM messages WHERE FolderId = $f AND LocalState = 0 AND RemoteUid IS NOT NULL
                ORDER BY RemoteUid;
                """;
            command.Parameters.AddParam("$f", folderId);
            var list = new List<LocalUidRow>();
            await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                list.Add(new LocalUidRow(
                    reader.GetString(0),
                    reader.GetInt64(1),
                    reader.GetInt32(2) != 0,
                    reader.GetInt32(3) != 0,
                    reader.IsDBNull(4) ? null : reader.GetString(4),
                    reader.IsDBNull(5) ? null : SqliteHelpers.ParseDate(reader.GetString(5))));
            }

            return (IReadOnlyList<LocalUidRow>)list;
        }, cancellationToken);

    /// <summary>
    /// 把服务器上的标志变化应用到本地行（按 UID 定位，只处理本文件夹里 LocalState=0 的行）。
    /// 字段上有未写回操作，或本地改动时间晚于 <paramref name="snapshotAt"/> 时保留本地值。
    /// </summary>
    public Task<ServerFlagApplyResult> ApplyServerFlagsAsync(
        string folderId, IReadOnlyList<ServerFlagState> flags, DateTimeOffset snapshotAt, CancellationToken cancellationToken = default)
        => QueryAsync(async (connection, ct) =>
        {
            var updated = new List<string>();
            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(ct).ConfigureAwait(false);
            var masks = await GetPendingMasksCoreAsync(connection, transaction, ct).ConfigureAwait(false);
            foreach (var flag in flags)
            {
                string id;
                bool read, flagged;
                LocalFieldStamps stamps;
                await using (var q = Cmd(connection, transaction, """
                    SELECT Id, IsRead, IsFlagged, ReadChangedAt, FlagChangedAt FROM messages
                    WHERE FolderId = $f AND RemoteUid = $u AND LocalState = 0;
                    """))
                {
                    q.Parameters.AddParam("$f", folderId);
                    q.Parameters.AddParam("$u", flag.Uid);
                    await using var r = await q.ExecuteReaderAsync(ct).ConfigureAwait(false);
                    if (!await r.ReadAsync(ct).ConfigureAwait(false))
                    {
                        continue;
                    }

                    id = r.GetString(0);
                    read = r.GetInt32(1) != 0;
                    flagged = r.GetInt32(2) != 0;
                    stamps = new LocalFieldStamps(
                        r.IsDBNull(3) ? null : SqliteHelpers.ParseDate(r.GetString(3)),
                        r.IsDBNull(4) ? null : SqliteHelpers.ParseDate(r.GetString(4)),
                        null);
                }

                var (newRead, newFlagged) = SyncMerge.ResolveFlags(
                    read, flagged, flag.IsRead, flag.IsFlagged, masks.GetValueOrDefault(id), stamps, snapshotAt);
                if (newRead == read && newFlagged == flagged)
                {
                    continue;
                }

                await NonQueryAsync(connection, transaction, "UPDATE messages SET IsRead = $r, IsFlagged = $f WHERE Id = $id;",
                    ("$r", newRead ? 1 : 0), ("$f", newFlagged ? 1 : 0), ("$id", id), ct).ConfigureAwait(false);
                updated.Add(id);
            }

            await transaction.CommitAsync(ct).ConfigureAwait(false);
            if (updated.Count > 0)
            {
                await RecalculateFolderCountsCoreAsync(connection, ct).ConfigureAwait(false);
            }

            return new ServerFlagApplyResult(updated.Count, updated);
        }, cancellationToken);

    /// <summary>
    /// 服务器上这些 UID 已不存在（被删除 / 被移走）：删掉本文件夹里对应的本地行及其待写回操作。
    /// 只匹配 FolderId = 本文件夹且 LocalState=0 的行——本地刚移走（FolderId 已变）的行不会被误删。
    /// </summary>
    public Task<IReadOnlyList<RemovedRow>> RemoveServerDeletedAsync(string folderId, IReadOnlyList<long> uids, CancellationToken cancellationToken = default)
        => QueryAsync(async (connection, ct) =>
        {
            var removed = new List<RemovedRow>();
            if (uids.Count == 0)
            {
                return (IReadOnlyList<RemovedRow>)removed;
            }

            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(ct).ConfigureAwait(false);
            foreach (var chunk in uids.Distinct().Chunk(400))
            {
                var names = new List<string>();
                await using var q = Cmd(connection, transaction, "");
                for (var i = 0; i < chunk.Length; i++)
                {
                    names.Add($"$u{i}");
                    q.Parameters.AddParam($"$u{i}", chunk[i]);
                }

                q.Parameters.AddParam("$f", folderId);
                q.CommandText = $"SELECT Id, RemoteUid, GmMsgId FROM messages WHERE FolderId = $f AND LocalState = 0 AND RemoteUid IN ({string.Join(", ", names)});";
                await using (var r = await q.ExecuteReaderAsync(ct).ConfigureAwait(false))
                {
                    while (await r.ReadAsync(ct).ConfigureAwait(false))
                    {
                        removed.Add(new RemovedRow(r.GetString(0), r.GetInt64(1), r.IsDBNull(2) ? null : r.GetString(2)));
                    }
                }
            }

            foreach (var row in removed)
            {
                await NonQueryAsync(connection, transaction, "DELETE FROM pending_ops WHERE MessageId = $id;", ("$id", row.MessageId), ct).ConfigureAwait(false);
                await NonQueryAsync(connection, transaction, "DELETE FROM messages WHERE Id = $id;", ("$id", row.MessageId), ct).ConfigureAwait(false);
            }

            await transaction.CommitAsync(ct).ConfigureAwait(false);
            if (removed.Count > 0)
            {
                await RecalculateFolderCountsCoreAsync(connection, ct).ConfigureAwait(false);
            }

            return (IReadOnlyList<RemovedRow>)removed;
        }, cancellationToken);

    /// <summary>
    /// UIDVALIDITY 变了：该文件夹里所有旧 UID 作废。删除本文件夹里“来自服务器”的邮件，丢弃所有以该文件夹为源的待写回操作
    /// （它们的 UID 指向的已不是原来的邮件，写回会改错邮件），返回被丢弃的操作供记录日志。
    /// 本地生成的已发送副本 / 等待重新同步的行 / 本地草稿保留。游标、MODSEQ、UIDNEXT 清空，随后会像首次同步一样重新拉取。
    /// </summary>
    public Task<UidValidityResetResult> ResetFolderForUidValidityAsync(string folderId, long? newUidValidity, CancellationToken cancellationToken = default)
        => QueryAsync(async (connection, ct) =>
        {
            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(ct).ConfigureAwait(false);
            var discarded = new List<PendingOpEntity>();
            await using (var q = Cmd(connection, transaction,
                "SELECT Id, AccountId, MessageId, Kind, SourceFolderId, TargetFolderId, RemoteKey, Attempts, LastError, NextAttemptAt, CreatedAt, UpdatedAt " +
                "FROM pending_ops WHERE SourceFolderId = $f OR MessageId IN (SELECT Id FROM messages WHERE FolderId = $f AND LocalState = 0);"))
            {
                q.Parameters.AddParam("$f", folderId);
                await using var r = await q.ExecuteReaderAsync(ct).ConfigureAwait(false);
                while (await r.ReadAsync(ct).ConfigureAwait(false))
                {
                    discarded.Add(ReadPendingOp(r));
                }
            }

            var orphanIds = new List<string>();
            await using (var q = Cmd(connection, transaction, "SELECT MessageId FROM pending_ops WHERE SourceFolderId = $f;"))
            {
                q.Parameters.AddParam("$f", folderId);
                await using var r = await q.ExecuteReaderAsync(ct).ConfigureAwait(false);
                while (await r.ReadAsync(ct).ConfigureAwait(false))
                {
                    orphanIds.Add(r.GetString(0));
                }
            }

            foreach (var op in discarded)
            {
                await NonQueryAsync(connection, transaction, "DELETE FROM pending_ops WHERE Id = $id;", ("$id", op.Id), ct).ConfigureAwait(false);
            }

            // 本地已移到别处、但移动操作还没写回（源文件夹就是这里）的行：它们的服务器副本会随重新同步回到本文件夹，这里一并删掉避免重复
            foreach (var id in orphanIds)
            {
                await NonQueryAsync(connection, transaction, "DELETE FROM messages WHERE Id = $id AND LocalState = 0;", ("$id", id), ct).ConfigureAwait(false);
            }

            int removed;
            await using (var d = Cmd(connection, transaction, "DELETE FROM messages WHERE FolderId = $f AND LocalState = 0 AND Id NOT LIKE 'draft:%';"))
            {
                d.Parameters.AddParam("$f", folderId);
                removed = await d.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }

            await NonQueryAsync(connection, transaction,
                "UPDATE folders SET SyncCursor = NULL, UidValidity = $v, UidNext = NULL, HighestModSeq = NULL, LastReconcileAt = NULL WHERE Id = $id;",
                ("$v", newUidValidity), ("$id", folderId), ct).ConfigureAwait(false);
            await transaction.CommitAsync(ct).ConfigureAwait(false);
            await RecalculateFolderCountsCoreAsync(connection, ct).ConfigureAwait(false);
            return new UidValidityResetResult(removed + orphanIds.Count, discarded);
        }, cancellationToken);

    /// <summary>
    /// 记录 Gmail 的 X-GM-MSGID / X-GM-THRID，并让同一账号里同一个 X-GM-MSGID 只保留一行
    /// （收件箱 / 已发送 / 所有邮件里的副本不会重复显示，也不会重复计入未读）。返回被合并掉的重复行数。
    /// </summary>
    public Task<int> ApplyGmailIdentityAsync(
        Guid accountId, IReadOnlyList<(string MessageId, string? GmMsgId, string? GmThrId)> identities, CancellationToken cancellationToken = default)
        => QueryAsync(async (connection, ct) =>
        {
            var removed = 0;
            if (identities.Count == 0)
            {
                return 0;
            }

            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(ct).ConfigureAwait(false);
            var touched = new HashSet<string>(StringComparer.Ordinal);
            foreach (var (messageId, msgId, thrId) in identities)
            {
                if (string.IsNullOrEmpty(msgId))
                {
                    continue;
                }

                await NonQueryAsync(connection, transaction, "UPDATE messages SET GmMsgId = $m, GmThrId = $t WHERE Id = $id;",
                    ("$m", msgId), ("$t", thrId), ("$id", messageId), ct).ConfigureAwait(false);
                touched.Add(msgId);
            }

            foreach (var msgId in touched)
            {
                var copies = new List<GmailCopyCandidate>();
                await using (var q = Cmd(connection, transaction, """
                    SELECT m.Id, f.Special, EXISTS (SELECT 1 FROM pending_ops p WHERE p.MessageId = m.Id)
                    FROM messages m JOIN folders f ON f.Id = m.FolderId
                    WHERE m.AccountId = $a AND m.GmMsgId = $g;
                    """))
                {
                    q.Parameters.AddParam("$a", accountId.ToString("D"));
                    q.Parameters.AddParam("$g", msgId);
                    await using var r = await q.ExecuteReaderAsync(ct).ConfigureAwait(false);
                    while (await r.ReadAsync(ct).ConfigureAwait(false))
                    {
                        copies.Add(new GmailCopyCandidate(
                            r.GetString(0), SqliteHelpers.ParseEnum<SpecialFolder>(r.GetString(1)), r.GetInt32(2) != 0));
                    }
                }

                if (copies.Count < 2)
                {
                    continue;
                }

                var keeper = GmailDedupe.PickKeeper(copies);
                foreach (var copy in copies.Where(c => c.MessageId != keeper.MessageId))
                {
                    await NonQueryAsync(connection, transaction, "DELETE FROM pending_ops WHERE MessageId = $id;", ("$id", copy.MessageId), ct).ConfigureAwait(false);
                    await NonQueryAsync(connection, transaction, "DELETE FROM messages WHERE Id = $id;", ("$id", copy.MessageId), ct).ConfigureAwait(false);
                    removed++;
                }
            }

            await transaction.CommitAsync(ct).ConfigureAwait(false);
            if (removed > 0)
            {
                await RecalculateFolderCountsCoreAsync(connection, ct).ConfigureAwait(false);
            }

            return removed;
        }, cancellationToken);

    /// <summary>Graph 增量里的 @removed（被删除 / 被移出本文件夹）：删除本文件夹里对应的本地行。</summary>
    public Task<int> RemoveGraphMessagesAsync(Guid accountId, string folderId, IReadOnlyList<string> graphIds, CancellationToken cancellationToken = default)
        => QueryAsync(async (connection, ct) =>
        {
            var removed = 0;
            if (graphIds.Count == 0)
            {
                return 0;
            }

            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(ct).ConfigureAwait(false);
            foreach (var graphId in graphIds.Distinct())
            {
                var id = accountId.ToString("D") + ":" + graphId;
                string? rowFolder;
                await using (var q = Cmd(connection, transaction, "SELECT FolderId FROM messages WHERE Id = $id;"))
                {
                    q.Parameters.AddParam("$id", id);
                    rowFolder = await q.ExecuteScalarAsync(ct).ConfigureAwait(false) as string;
                }

                // 本地已把它移到别的文件夹（FolderId 已变）时不删：那是用户刚做的移动
                if (rowFolder is null || !string.Equals(rowFolder, folderId, StringComparison.Ordinal))
                {
                    continue;
                }

                await NonQueryAsync(connection, transaction, "DELETE FROM pending_ops WHERE MessageId = $id;", ("$id", id), ct).ConfigureAwait(false);
                await NonQueryAsync(connection, transaction, "DELETE FROM messages WHERE Id = $id;", ("$id", id), ct).ConfigureAwait(false);
                removed++;
            }

            await transaction.CommitAsync(ct).ConfigureAwait(false);
            if (removed > 0)
            {
                await RecalculateFolderCountsCoreAsync(connection, ct).ConfigureAwait(false);
            }

            return removed;
        }, cancellationToken);

    /// <summary>
    /// Graph 增量里只带了 id + 变化字段的更新：按邮件 Id 合并已读 / 星标（规则同 <see cref="SyncMerge"/>）。本地没有的邮件忽略。
    /// </summary>
    public Task<ServerFlagApplyResult> ApplyFlagsByMessageIdAsync(
        IReadOnlyList<(string MessageId, bool? IsRead, bool? IsFlagged)> updates, DateTimeOffset snapshotAt, CancellationToken cancellationToken = default)
        => QueryAsync(async (connection, ct) =>
        {
            var updated = new List<string>();
            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(ct).ConfigureAwait(false);
            var masks = await GetPendingMasksCoreAsync(connection, transaction, ct).ConfigureAwait(false);
            foreach (var (id, serverRead, serverFlagged) in updates)
            {
                bool read, flagged;
                LocalFieldStamps stamps;
                await using (var q = Cmd(connection, transaction,
                    "SELECT IsRead, IsFlagged, ReadChangedAt, FlagChangedAt FROM messages WHERE Id = $id;"))
                {
                    q.Parameters.AddParam("$id", id);
                    await using var r = await q.ExecuteReaderAsync(ct).ConfigureAwait(false);
                    if (!await r.ReadAsync(ct).ConfigureAwait(false))
                    {
                        continue;
                    }

                    read = r.GetInt32(0) != 0;
                    flagged = r.GetInt32(1) != 0;
                    stamps = new LocalFieldStamps(
                        r.IsDBNull(2) ? null : SqliteHelpers.ParseDate(r.GetString(2)),
                        r.IsDBNull(3) ? null : SqliteHelpers.ParseDate(r.GetString(3)),
                        null);
                }

                var (newRead, newFlagged) = SyncMerge.ResolveFlags(
                    read, flagged, serverRead ?? read, serverFlagged ?? flagged, masks.GetValueOrDefault(id), stamps, snapshotAt);
                if (newRead == read && newFlagged == flagged)
                {
                    continue;
                }

                await NonQueryAsync(connection, transaction, "UPDATE messages SET IsRead = $r, IsFlagged = $f WHERE Id = $id;",
                    ("$r", newRead ? 1 : 0), ("$f", newFlagged ? 1 : 0), ("$id", id), ct).ConfigureAwait(false);
                updated.Add(id);
            }

            await transaction.CommitAsync(ct).ConfigureAwait(false);
            if (updated.Count > 0)
            {
                await RecalculateFolderCountsCoreAsync(connection, ct).ConfigureAwait(false);
            }

            return new ServerFlagApplyResult(updated.Count, updated);
        }, cancellationToken);

    /// <summary>
    /// Graph 完整重新同步（410 syncStateNotFound）之后：删掉本文件夹里“服务器这次没再给出”的本地邮件。
    /// 只处理收到时间不早于 <paramref name="receivedNotBefore"/> 的（更早的不在这次拉取范围内）；有待写回操作的、本地独有的不动。
    /// </summary>
    public Task<int> PruneServerMessagesNotInAsync(
        string folderId, IReadOnlySet<string> keepMessageIds, DateTimeOffset receivedNotBefore, CancellationToken cancellationToken = default)
        => QueryAsync(async (connection, ct) =>
        {
            var doomed = new List<string>();
            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(ct).ConfigureAwait(false);
            var masks = await GetPendingMasksCoreAsync(connection, transaction, ct).ConfigureAwait(false);
            await using (var q = Cmd(connection, transaction, """
                SELECT Id, ReceivedAt FROM messages WHERE FolderId = $f AND LocalState = 0 AND Id NOT LIKE 'draft:%';
                """))
            {
                q.Parameters.AddParam("$f", folderId);
                await using var r = await q.ExecuteReaderAsync(ct).ConfigureAwait(false);
                while (await r.ReadAsync(ct).ConfigureAwait(false))
                {
                    var id = r.GetString(0);
                    var received = r.IsDBNull(1) ? null : SqliteHelpers.ParseDate(r.GetString(1));
                    if (!keepMessageIds.Contains(id) && !masks.ContainsKey(id) && (received is null || received >= receivedNotBefore))
                    {
                        doomed.Add(id);
                    }
                }
            }

            foreach (var id in doomed)
            {
                await NonQueryAsync(connection, transaction, "DELETE FROM messages WHERE Id = $id;", ("$id", id), ct).ConfigureAwait(false);
            }

            await transaction.CommitAsync(ct).ConfigureAwait(false);
            if (doomed.Count > 0)
            {
                await RecalculateFolderCountsCoreAsync(connection, ct).ConfigureAwait(false);
            }

            return doomed.Count;
        }, cancellationToken);

    /// <summary>
    /// 服务器上已不存在的文件夹：删除本地文件夹及其邮件。保守：服务器列表为空、文件夹里有本地独有内容
    /// （本地草稿 / 已发送副本 / 待写回操作）时不删。返回被删除的文件夹 Id。
    /// </summary>
    public Task<IReadOnlyList<string>> PruneMissingFoldersAsync(Guid accountId, IReadOnlyCollection<string> serverFolderIds, CancellationToken cancellationToken = default)
        => QueryAsync(async (connection, ct) =>
        {
            var pruned = new List<string>();
            if (serverFolderIds.Count == 0)
            {
                return (IReadOnlyList<string>)pruned;
            }

            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(ct).ConfigureAwait(false);
            var local = new List<string>();
            await using (var q = Cmd(connection, transaction, "SELECT Id FROM folders WHERE AccountId = $a;"))
            {
                q.Parameters.AddParam("$a", accountId.ToString("D"));
                await using var r = await q.ExecuteReaderAsync(ct).ConfigureAwait(false);
                while (await r.ReadAsync(ct).ConfigureAwait(false))
                {
                    local.Add(r.GetString(0));
                }
            }

            var keep = new HashSet<string>(serverFolderIds, StringComparer.Ordinal);
            foreach (var folderId in local.Where(id => !keep.Contains(id)))
            {
                await using var guard = Cmd(connection, transaction, """
                    SELECT EXISTS (SELECT 1 FROM messages WHERE FolderId = $f AND (LocalState <> 0 OR Id LIKE 'draft:%'))
                        OR EXISTS (SELECT 1 FROM pending_ops WHERE SourceFolderId = $f OR TargetFolderId = $f
                                   OR MessageId IN (SELECT Id FROM messages WHERE FolderId = $f));
                    """);
                guard.Parameters.AddParam("$f", folderId);
                if (Convert.ToInt32(await guard.ExecuteScalarAsync(ct).ConfigureAwait(false)) != 0)
                {
                    continue;
                }

                await NonQueryAsync(connection, transaction, "DELETE FROM folders WHERE Id = $f;", ("$f", folderId), ct).ConfigureAwait(false);
                pruned.Add(folderId);
            }

            await transaction.CommitAsync(ct).ConfigureAwait(false);
            return (IReadOnlyList<string>)pruned;
        }, cancellationToken);
}
