using MailClient.Core;
using MailClient.Core.Services;
using MailClient.Core.Sync;
using MailClient.Storage;

namespace MailClient.Providers.Imap;

/// <summary>
/// 单个 IMAP 文件夹的双向增量同步。按服务器能力选择策略（详见 docs/two-way-sync.md）：
/// QRESYNC → CONDSTORE → 窗口对账；再加“新邮件”检查（UIDNEXT 没变就跳过）。
/// 合并规则：未写回的本地变更优先，其余服务器胜出（<see cref="SyncMerge"/>）。
/// </summary>
public sealed class ImapFolderSyncEngine
{
    private const int KnownUidCap = 10_000;

    private readonly MailDatabase _db;
    private readonly SyncOptions _options;
    private readonly ISyncLog _log;
    private readonly Func<DateTimeOffset> _clock;

    public ImapFolderSyncEngine(MailDatabase database, SyncOptions? options = null, ISyncLog? log = null, Func<DateTimeOffset>? clock = null)
    {
        _db = database ?? throw new ArgumentNullException(nameof(database));
        _options = options ?? SyncOptions.Default;
        _log = log ?? NullSyncLog.Instance;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    /// <param name="newLimit">新邮件一次最多取多少封（首次 = InitialLimit，之后 = IncrementalLimit）。</param>
    /// <param name="force">忽略对账节流（刷新按钮 / IDLE 通知）。</param>
    public async Task<FolderSyncReport> SyncFolderAsync(
        IImapSyncSource source, FolderEntity folder, int newLimit, bool force, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(folder);
        var path = folder.RemotePath ?? folder.Id;
        var accountId = folder.AccountId;
        var snapshotAt = _clock();     // 在向服务器发任何请求之前取：本地时间戳晚于它的字段不会被这次快照覆盖
        var caps = await source.GetCapabilitiesAsync(ct).ConfigureAwait(false);
        var state = await _db.GetFolderAsync(folder.Id, ct).ConfigureAwait(false) ?? folder;
        var hasCursor = long.TryParse(state.SyncCursor, out var cursor) && cursor > 0;

        // ---- 1. SELECT（能 QRESYNC 就带上上次的状态） ----
        var knownRows = Array.Empty<LocalUidRow>() as IReadOnlyList<LocalUidRow>;
        ImapFolderOpen open;
        IReadOnlyList<ServerFlagState> qFlags = Array.Empty<ServerFlagState>();
        IReadOnlyList<long> vanished = Array.Empty<long>();
        var usedQResync = false;
        var canQResync = caps.QResync && caps.CondStore && hasCursor && state.UidValidity is > 0 && state.HighestModSeq is > 0;
        if (canQResync)
        {
            try
            {
                knownRows = await _db.ListFolderUidRowsAsync(folder.Id, ct).ConfigureAwait(false);
                var known = knownRows.OrderByDescending(r => r.Uid).Take(KnownUidCap).Select(r => r.Uid).ToList();
                var q = await source.SelectQResyncAsync(path, state.UidValidity!.Value, state.HighestModSeq!.Value, known, ct).ConfigureAwait(false);
                open = q.Open;
                qFlags = q.Flags;
                vanished = q.Vanished;
                usedQResync = true;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log.Warn($"{folder.Id}: QRESYNC 失败，降级到 CONDSTORE：{ex.Message}");
                open = await source.SelectAsync(path, ct).ConfigureAwait(false);
            }
        }
        else
        {
            open = await source.SelectAsync(path, ct).ConfigureAwait(false);
        }

        // ---- 2. UIDVALIDITY ----
        var reset = false;
        var removed = 0;
        if (state.UidValidity is { } oldValidity && open.UidValidity > 0 && oldValidity != open.UidValidity)
        {
            var result = await _db.ResetFolderForUidValidityAsync(folder.Id, open.UidValidity, ct).ConfigureAwait(false);
            _log.Warn($"{folder.Id}: UIDVALIDITY {oldValidity} → {open.UidValidity}，已清空本地 {result.MessagesRemoved} 封邮件并重新同步。");
            foreach (var op in result.DiscardedOps)
            {
                _log.Warn($"{folder.Id}: 丢弃指向旧 UID 的待写回操作 {op.Kind}（邮件 {op.MessageId}，RemoteKey={op.RemoteKey}）。");
            }

            reset = true;
            removed += result.MessagesRemoved;
            state = state with { SyncCursor = null, UidNext = null, HighestModSeq = null, LastReconcileAt = null, UidValidity = open.UidValidity };
            hasCursor = false;
            cursor = 0;
            usedQResync = false;
            qFlags = Array.Empty<ServerFlagState>();
            vanished = Array.Empty<long>();
        }

        var hasModSeq = caps.CondStore && open.HighestModSeq > 0;
        var storedModSeq = state.HighestModSeq ?? 0;
        var strategy = !hasCursor ? ImapSyncStrategy.Initial
            : usedQResync ? ImapSyncStrategy.QResync
            : hasModSeq && storedModSeq > 0 ? ImapSyncStrategy.CondStore
            : ImapSyncStrategy.Reconcile;

        var flagUpdates = 0;
        var reconciledAt = state.LastReconcileAt;
        if (strategy == ImapSyncStrategy.Initial)
        {
            reconciledAt = snapshotAt;     // 首次同步刚列过一遍，不需要立刻再对账
        }

        // ---- 3. 已有邮件：标志变化 + 删除 ----
        switch (strategy)
        {
            case ImapSyncStrategy.QResync:
                if (qFlags.Count > 0)
                {
                    flagUpdates += (await _db.ApplyServerFlagsAsync(folder.Id, qFlags, snapshotAt, ct).ConfigureAwait(false)).Updated;
                }

                if (vanished.Count > 0)
                {
                    removed += (await _db.RemoveServerDeletedAsync(folder.Id, vanished, ct).ConfigureAwait(false)).Count;
                }

                break;

            case ImapSyncStrategy.CondStore:
            {
                var modSeqChanged = open.HighestModSeq != storedModSeq;
                if (modSeqChanged || force)
                {
                    var changed = await source.FetchFlagsChangedSinceAsync(path, storedModSeq, ct).ConfigureAwait(false);
                    if (changed.Count > 0)
                    {
                        flagUpdates += (await _db.ApplyServerFlagsAsync(folder.Id, changed, snapshotAt, ct).ConfigureAwait(false)).Updated;
                    }
                }

                // 删除（EXPUNGE）不一定让 MODSEQ 前进，所以除了 MODSEQ / UIDNEXT 变化外，到点也要查一次窗口
                var diffDue = modSeqChanged || open.UidNext != (state.UidNext ?? 0) ||
                              ImapDiff.ReconcileDue(state.LastReconcileAt, snapshotAt, _options.ReconcileInterval, force);
                if (diffDue)
                {
                    removed += await RemoveDeletedInWindowAsync(source, folder, path, open, snapshotAt, ct).ConfigureAwait(false);
                    reconciledAt = snapshotAt;
                }

                break;
            }

            case ImapSyncStrategy.Reconcile:
                if (ImapDiff.ReconcileDue(state.LastReconcileAt, snapshotAt, _options.ReconcileInterval, force))
                {
                    var rows = await _db.ListFolderUidRowsAsync(folder.Id, ct).ConfigureAwait(false);
                    var window = ImapDiff.SelectWindow(rows, snapshotAt, _options.WindowMaxMessages, _options.WindowMaxAge);
                    if (window.Count > 0)
                    {
                        var minUid = window[0].Uid;
                        var server = (await source.FetchUidFlagsAsync(path, minUid, ct).ConfigureAwait(false))
                            .Where(s => s.Uid >= minUid).ToList();
                        if (ImapDiff.LooksLikeBogusEmptyListing(server.Count, open.Count, window.Count))
                        {
                            _log.Warn($"{folder.Id}: 服务器返回空列表但 EXISTS={open.Count}，跳过本轮删除检测。");
                        }
                        else
                        {
                            var changed = ImapDiff.ChangedFlags(window, server);
                            if (changed.Count > 0)
                            {
                                flagUpdates += (await _db.ApplyServerFlagsAsync(folder.Id, changed, snapshotAt, ct).ConfigureAwait(false)).Updated;
                            }

                            var missing = ImapDiff.FindMissing(window, server.Select(s => s.Uid).ToHashSet());
                            if (missing.Count > 0)
                            {
                                removed += (await _db.RemoveServerDeletedAsync(folder.Id, missing, ct).ConfigureAwait(false)).Count;
                            }
                        }
                    }

                    reconciledAt = snapshotAt;
                }

                break;
        }

        // ---- 4. 新邮件（UIDNEXT 没变 = 没有新邮件，省一次 SEARCH） ----
        var newCount = 0;
        var merged = 0;
        var needNew = !hasCursor || force || open.UidNext <= 0 || state.UidNext is null || open.UidNext != state.UidNext;
        if (needNew)
        {
            var items = await source.FetchNewAsync(path, hasCursor ? cursor : null, newLimit, ct).ConfigureAwait(false);
            var fresh = items.Where(i => !hasCursor || i.RemoteUid > cursor).ToList();
            if (fresh.Count > 0)
            {
                var entities = fresh.Select(i => ImapSyncPipeline.ToMessageEntity(accountId, folder.Id, i)).ToList();
                await _db.UpsertMessagesAsync(entities, snapshotAt, ct).ConfigureAwait(false);
                await ImapSyncPipeline.PersistFetchedBodiesAsync(_db, fresh, ct).ConfigureAwait(false);
                newCount = fresh.Count;
                await _db.RecalculateFolderCountsAsync(ct).ConfigureAwait(false);
                cursor = Math.Max(cursor, fresh.Max(i => i.RemoteUid));
                if (caps.GmailExt)
                {
                    var ids = fresh
                        .Where(i => !string.IsNullOrEmpty(i.GmMsgId))
                        .Select(i => (i.Summary.Id, i.GmMsgId, i.GmThrId))
                        .ToList();
                    merged = await _db.ApplyGmailIdentityAsync(accountId, ids, ct).ConfigureAwait(false);
                }
            }
        }

        // ---- 5. 保存状态（任何一步抛异常就不会走到这里：下次从旧状态重来） ----
        await _db.SaveFolderSyncStateAsync(new FolderSyncState(
                folder.Id,
                cursor > 0 ? cursor.ToString() : state.SyncCursor,
                open.UidValidity > 0 ? open.UidValidity : state.UidValidity,
                open.UidNext > 0 ? open.UidNext : state.UidNext,
                hasModSeq ? open.HighestModSeq : state.HighestModSeq,
                reconciledAt),
            ct).ConfigureAwait(false);

        var report = new FolderSyncReport(folder.Id)
        {
            Strategy = strategy,
            NewMessages = newCount,
            FlagUpdates = flagUpdates,
            Removed = removed,
            GmailDuplicatesMerged = merged,
            UidValidityReset = reset
        };
        if (report.Changed)
        {
            _log.Info(report.ToString());
        }

        return report;
    }

    private async Task<int> RemoveDeletedInWindowAsync(
        IImapSyncSource source, FolderEntity folder, string path, ImapFolderOpen open, DateTimeOffset now, CancellationToken ct)
    {
        var rows = await _db.ListFolderUidRowsAsync(folder.Id, ct).ConfigureAwait(false);
        var window = ImapDiff.SelectWindow(rows, now, _options.WindowMaxMessages, _options.WindowMaxAge);
        if (window.Count == 0)
        {
            return 0;
        }

        var minUid = window[0].Uid;
        var server = (await source.SearchUidsAsync(path, minUid, ct).ConfigureAwait(false)).Where(u => u >= minUid).ToHashSet();
        if (ImapDiff.LooksLikeBogusEmptyListing(server.Count, open.Count, window.Count))
        {
            _log.Warn($"{folder.Id}: 服务器返回空列表但 EXISTS={open.Count}，跳过本轮删除检测。");
            return 0;
        }

        var missing = ImapDiff.FindMissing(window, server);
        return missing.Count == 0
            ? 0
            : (await _db.RemoveServerDeletedAsync(folder.Id, missing, ct).ConfigureAwait(false)).Count;
    }
}
