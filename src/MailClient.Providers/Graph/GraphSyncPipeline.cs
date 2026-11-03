using MailClient.Core;
using MailClient.Core.Services;
using MailClient.Core.Sync;
using MailClient.Providers.Imap;
using MailClient.Storage;

namespace MailClient.Providers.Graph;

/// <summary>
/// 将 <see cref="MicrosoftGraphProvider"/> 同步结果写入 <see cref="MailDatabase"/>。
/// </summary>
public sealed class GraphSyncPipeline
{
    private readonly MailDatabase _db;
    /// <summary>首次 / 完整同步只看最近这么久收到的邮件（delta 不能只取“最新 N 封”，所以用时间过滤，免得遍历整个邮箱）。</summary>
    private static readonly TimeSpan InitialWindow = TimeSpan.FromDays(90);

    private readonly SyncOptions _options;
    private readonly ISyncLog _log;
    private readonly Func<DateTimeOffset> _clock;

    public GraphSyncPipeline(MailDatabase database, SyncOptions? options = null, ISyncLog? log = null, Func<DateTimeOffset>? clock = null)
    {
        _db = database ?? throw new ArgumentNullException(nameof(database));
        _options = options ?? SyncOptions.Default;
        _log = log ?? NullSyncLog.Instance;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    public async Task<IReadOnlyList<MailFolder>> SyncFoldersAsync(
        MicrosoftGraphProvider provider,
        CancellationToken cancellationToken = default)
    {
        var folders = await provider.GetFoldersAsync(cancellationToken).ConfigureAwait(false);
        var accountId = provider.AccountId;
        var order = 0;
        // 重新列文件夹不应丢掉已有的同步游标 / 总数，否则每次刷新都会重拉整个文件夹。
        var known = (await _db.ListFoldersAsync(accountId, cancellationToken).ConfigureAwait(false))
            .ToDictionary(f => f.Id, StringComparer.Ordinal);
        foreach (var folder in folders)
        {
            known.TryGetValue(BuildFolderId(accountId, folder.Id), out var previous);
            var entity = (previous ?? new FolderEntity(
                    Id: BuildFolderId(accountId, folder.Id),
                    AccountId: accountId,
                    Name: folder.Name,
                    Special: folder.Special,
                    RemotePath: folder.RemotePath ?? folder.Id,
                    UnreadCount: folder.UnreadCount,
                    TotalCount: 0,
                    SortOrder: 0))
                with
                {
                    Name = folder.Name,
                    Special = folder.Special,
                    RemotePath = folder.RemotePath ?? folder.Id,
                    UnreadCount = folder.UnreadCount,
                    SortOrder = GraphFolderMapper.DefaultSortOrder(folder.Special) + order
                };
            await _db.UpsertFolderAsync(entity, cancellationToken).ConfigureAwait(false);
            order++;
        }

        // 服务器上已经删掉的文件夹：本地也清掉（列表里至少要有收件箱，避免半截列表误删）
        if (folders.Any(f => f.Special == SpecialFolder.Inbox))
        {
            var pruned = await _db.PruneMissingFoldersAsync(
                    accountId, folders.Select(f => BuildFolderId(accountId, f.Id)).ToList(), cancellationToken)
                .ConfigureAwait(false);
            foreach (var id in pruned)
            {
                _log.Info($"服务器上已不存在的文件夹，已从本地移除：{id}");
            }
        }

        return folders;
    }

    /// <summary>
    /// 同步一个文件夹（delta 查询）：新邮件、已读 / 星标变化、被删除 / 被移走（@removed）。
    /// token 失效（410）→ 丢掉游标做一次完整同步，并清掉服务器上已不存在的本地邮件。
    /// </summary>
    public async Task<GraphFolderReport> SyncFolderAsync(
        IGraphDeltaSource source,
        Guid accountId,
        string remoteFolderId,
        int initialLimit,
        CancellationToken cancellationToken = default)
    {
        var snapshotAt = _clock();      // 先于任何网络请求取：本地更晚的改动不会被这次快照覆盖
        var localFolderId = BuildFolderId(accountId, remoteFolderId);
        var folder = await _db.GetFolderAsync(localFolderId, cancellationToken).ConfigureAwait(false);
        if (folder is null)
        {
            var special = GraphFolderMapper.Map(remoteFolderId);
            folder = new FolderEntity(
                Id: localFolderId,
                AccountId: accountId,
                Name: remoteFolderId,
                Special: special,
                RemotePath: remoteFolderId,
                UnreadCount: 0,
                TotalCount: 0,
                SortOrder: GraphFolderMapper.DefaultSortOrder(special));
            await _db.UpsertFolderAsync(folder, cancellationToken).ConfigureAwait(false);
        }

        var deltaLink = folder.SyncCursor is { Length: > 0 } c && c.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? c : null;
        var cutoff = snapshotAt - (_options.WindowMaxAge > InitialWindow ? _options.WindowMaxAge : InitialWindow);
        var fullResync = false;
        var initial = deltaLink is null;
        GraphDeltaResult result;
        try
        {
            result = await source.FetchDeltaAsync(remoteFolderId, deltaLink, initialLimit, deltaLink is null ? cutoff : null, cancellationToken).ConfigureAwait(false);
        }
        catch (GraphSyncStateNotFoundException ex)
        {
            _log.Warn($"{localFolderId}: Graph delta 状态失效（410），改为完整重新同步。{ex.Message}");
            fullResync = true;
            initial = true;
            result = await source.FetchDeltaAsync(remoteFolderId, null, initialLimit, cutoff, cancellationToken).ConfigureAwait(false);
        }

        var removedIds = result.Entries.Where(e => e.Removed).Select(e => e.GraphId).Distinct().ToList();
        var removed = removedIds.Count == 0
            ? 0
            : await _db.RemoveGraphMessagesAsync(accountId, localFolderId, removedIds, cancellationToken).ConfigureAwait(false);

        var fullItems = result.Entries.Where(e => !e.Removed && e.Item is not null).Select(e => e.Item!).ToList();
        var allSeen = fullItems.Select(i => i.Summary.Id).ToHashSet(StringComparer.Ordinal);
        var toStore = fullItems;
        if (initial && toStore.Count > initialLimit)
        {
            // 首次 / 完整同步：只留最新的一批，和 IMAP 的首次同步一致
            toStore = toStore.OrderByDescending(i => i.Summary.ReceivedAt).Take(initialLimit).ToList();
        }

        var newCount = 0;
        var fullItemFlagChanges = 0;
        if (toStore.Count > 0)
        {
            var entities = toStore.Select(item => ImapSyncPipeline.ToMessageEntity(accountId, localFolderId, item)).ToList();
            var rowsBefore = await _db.ListFolderUidRowsAsync(localFolderId, cancellationToken).ConfigureAwait(false);
            await _db.UpsertMessagesAsync(entities, snapshotAt, cancellationToken).ConfigureAwait(false);
            await ImapSyncPipeline.PersistFetchedBodiesAsync(_db, toStore, cancellationToken).ConfigureAwait(false);
            var rowsAfter = await _db.ListFolderUidRowsAsync(localFolderId, cancellationToken).ConfigureAwait(false);
            newCount = Math.Max(0, rowsAfter.Count - rowsBefore.Count);
            // delta 通常直接给出完整的邮件对象：已读 / 星标变化也在里面，对比一下好让界面知道有变化
            var beforeById = rowsBefore.ToDictionary(r => r.MessageId, StringComparer.Ordinal);
            fullItemFlagChanges = rowsAfter.Count(r => beforeById.TryGetValue(r.MessageId, out var b) && (b.IsRead != r.IsRead || b.IsFlagged != r.IsFlagged));
        }

        var partial = result.Entries
            .Where(e => !e.Removed && e.Item is null && (e.IsRead is not null || e.IsFlagged is not null))
            .Select(e => (GraphMessageMapper.BuildMessageId(accountId, e.GraphId), e.IsRead, e.IsFlagged))
            .ToList();
        var flagUpdates = fullItemFlagChanges + (partial.Count == 0
            ? 0
            : (await _db.ApplyFlagsByMessageIdAsync(partial, snapshotAt, cancellationToken).ConfigureAwait(false)).Updated);

        if (fullResync)
        {
            removed += await _db.PruneServerMessagesNotInAsync(localFolderId, allSeen, cutoff, cancellationToken).ConfigureAwait(false);
        }

        await _db.SaveFolderSyncStateAsync(new FolderSyncState(
            localFolderId,
            result.DeltaLink ?? (fullResync ? null : folder.SyncCursor),
            folder.UidValidity,
            folder.UidNext,
            folder.HighestModSeq,
            folder.LastReconcileAt), cancellationToken).ConfigureAwait(false);

        var report = new GraphFolderReport(localFolderId, newCount, flagUpdates, removed, fullResync, result.DeltaLink is not null);
        if (report.Changed)
        {
            _log.Info(report.ToString());
        }

        return report;
    }

    public static string BuildFolderId(Guid accountId, string remoteFolderId)
        => $"{accountId:D}:{remoteFolderId}";
}

public sealed record GraphFolderReport(string FolderId, int NewMessages, int FlagUpdates, int Removed, bool FullResync, bool HasDeltaLink)
{
    public bool Changed => NewMessages > 0 || FlagUpdates > 0 || Removed > 0 || FullResync;

    public override string ToString()
        => $"{FolderId}: delta new={NewMessages} flags={FlagUpdates} removed={Removed}{(FullResync ? " FULL-RESYNC" : "")}";
}
