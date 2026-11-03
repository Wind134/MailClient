using MailClient.Core;
using MailClient.Core.Services;
using MailClient.Core.Sync;
using MailClient.Storage;

namespace MailClient.Providers.Imap;

/// <summary>
/// 将 <see cref="GenericImapProvider"/> 的文件夹/邮件同步结果写入 <see cref="MailDatabase"/>。
/// Providers → Storage 单向依赖，避免环。
/// </summary>
public sealed class ImapSyncPipeline
{
    private readonly MailDatabase _db;
    private readonly SyncOptions _options;
    private readonly ISyncLog _log;

    public ImapSyncPipeline(MailDatabase database, SyncOptions? options = null, ISyncLog? log = null)
    {
        _db = database ?? throw new ArgumentNullException(nameof(database));
        _options = options ?? SyncOptions.Default;
        _log = log ?? NullSyncLog.Instance;
    }

    public async Task<IReadOnlyList<MailFolder>> SyncFoldersAsync(
        GenericImapProvider provider,
        CancellationToken cancellationToken = default)
    {
        var folders = await provider.GetFoldersAsync(cancellationToken).ConfigureAwait(false);
        var accountId = provider.AccountId;
        var order = 0;
        // 重新列文件夹不应丢掉已有的同步状态（游标 / UIDVALIDITY / UIDNEXT / MODSEQ / 对账时间），否则每次刷新都会重拉整个文件夹。
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
                    SortOrder = FolderSpecialMapper.DefaultSortOrder(folder.Special) + order
                };
            await _db.UpsertFolderAsync(entity, cancellationToken).ConfigureAwait(false);
            order++;
        }

        // 服务器上已经删掉 / 改名的文件夹：本地也清掉（列表里至少要有收件箱，避免半截列表误删）
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
    /// 同步一个文件夹：新邮件 + 服务器端已读 / 星标变化 + 服务器端删除 / 移动（按服务器能力选策略）。
    /// </summary>
    public async Task<FolderSyncReport> SyncFolderMessagesAsync(
        IImapSyncSource source,
        Guid accountId,
        string remoteFolderId,
        int newLimit,
        bool force = false,
        CancellationToken cancellationToken = default)
    {
        var localFolderId = BuildFolderId(accountId, remoteFolderId);
        var folder = await _db.GetFolderAsync(localFolderId, cancellationToken).ConfigureAwait(false);
        if (folder is null)
        {
            var special = FolderSpecialMapper.MapByName(remoteFolderId);
            folder = new FolderEntity(
                Id: localFolderId,
                AccountId: accountId,
                Name: remoteFolderId,
                Special: special,
                RemotePath: remoteFolderId,
                UnreadCount: 0,
                TotalCount: 0,
                SortOrder: FolderSpecialMapper.DefaultSortOrder(special));
            await _db.UpsertFolderAsync(folder, cancellationToken).ConfigureAwait(false);
        }

        return await new ImapFolderSyncEngine(_db, _options, _log)
            .SyncFolderAsync(source, folder, newLimit, force, cancellationToken)
            .ConfigureAwait(false);
    }

    public static string BuildFolderId(Guid accountId, string remoteFolderId)
        => $"{accountId:D}:{remoteFolderId}";

    public static MessageEntity ToMessageEntity(Guid accountId, string localFolderId, MailMessageSyncItem item)
    {
        var summary = item.Summary;
        return new MessageEntity(
            Id: summary.Id,
            AccountId: accountId,
            FolderId: localFolderId,
            RemoteUid: item.RemoteUid,
            RfcMessageId: item.RfcMessageId,
            ThreadKey: item.RfcMessageId,
            Subject: summary.Subject,
            FromAddress: item.FromAddress,
            FromName: item.FromName,
            Preview: summary.Preview,
            BodyText: item.BodyFetched ? item.BodyText : null,
            BodyHtml: item.BodyFetched ? item.BodyHtml : null,
            ReceivedAt: summary.ReceivedAt,
            SentAt: item.SentAt,
            IsRead: summary.IsRead,
            IsFlagged: summary.IsFlagged,
            IsDraft: item.IsDraft,
            HasAttachments: item.HasAttachments,
            // 仅保留服务器分类（Gmail IMAP 经 GmailCategoryMapper）。
            // Outlook / QQ / 163 / 通用 IMAP 不再跑本地 MailCategoryClassifier：这些账号不显示分类标签，列表也不按 Category 过滤。
            Category: item.Category,
            SizeBytes: item.SizeBytes,
            BodyFetched: item.BodyFetched);
    }

    /// <summary>把同步时已解析的正文 / 附件元数据写入缓存（UpsertMessages 之后调用）。</summary>
    public static async Task PersistFetchedBodiesAsync(
        MailDatabase db,
        IEnumerable<MailMessageSyncItem> items,
        CancellationToken cancellationToken = default)
    {
        foreach (var item in items)
        {
            if (!item.BodyFetched)
            {
                continue;
            }

            await db.SaveMessageBodyAsync(
                item.Summary.Id,
                new MessageBodyContent(
                    item.BodyText ?? string.Empty,
                    item.BodyHtml,
                    item.Attachments ?? Array.Empty<MessageAttachmentInfo>()),
                cancellationToken).ConfigureAwait(false);
        }
    }
}
