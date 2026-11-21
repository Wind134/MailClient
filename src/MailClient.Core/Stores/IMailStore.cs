using MailClient.Core.Sync;

namespace MailClient.Core;

/// <summary>
/// 本地邮件库对「应用层（视图模型）」暴露的端口。
/// </summary>
/// <remarks>
/// 依赖倒置：接口定义在内层 <c>MailClient.Core</c>，由 <c>MailClient.Storage</c> 实现，
/// 于是视图模型只依赖这个抽象，拿不到迁移、正文缓存清理、FTS 维护等存储内部能力。
/// 刻意保持单一聚合边界（本地邮件库）而不再按读写细拆：这些操作共享同一事务域与
/// 同一套一致性约束，拆开会让调用方为了完成一次交互同时持有多个端口。
/// </remarks>
public interface IMailStore
{
    /// <summary>草稿集合发生变化（保存 / 删除）。</summary>
    event EventHandler? DraftsChanged;

    /// <summary>发件箱发生变化（入队 / 状态推进 / 删除）。</summary>
    event EventHandler? OutboxChanged;

    /// <summary>打开数据库并应用迁移。重复调用应当幂等。</summary>
    Task InitializeAsync(CancellationToken cancellationToken = default);

    /// <summary>空库时写入演示数据（首次启动引导用）。</summary>
    Task SeedDemoDataIfEmptyAsync(CancellationToken cancellationToken = default);

    /// <summary>清除演示数据，保留真实账号。</summary>
    Task ClearDemoDataAsync(CancellationToken cancellationToken = default);

    Task<AccountEntity?> GetAccountAsync(Guid accountId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<AccountEntity>> ListAccountsAsync(CancellationToken cancellationToken = default);

    Task UpsertAccountAsync(AccountEntity account, CancellationToken cancellationToken = default);

    Task DeleteAccountAsync(Guid accountId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<FolderEntity>> ListAllFoldersAsync(CancellationToken cancellationToken = default);

    Task<MessageEntity?> GetMessageAsync(string messageId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<MessageEntity>> QueryMessagesAsync(MessageListFilter filter, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<AttachmentEntity>> ListAttachmentsAsync(string messageId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<RecipientEntity>> ListRecipientsAsync(string messageId, CancellationToken cancellationToken = default);

    Task<CategoryUnreadCounts> CountUnreadByCategoryAsync(IReadOnlyList<string>? folderIds, CancellationToken cancellationToken = default);

    Task<IReadOnlyDictionary<string, string>> GetBodyTextsAsync(IReadOnlyList<string> messageIds, CancellationToken cancellationToken = default);

    Task TouchBodyAccessAsync(string messageId, CancellationToken cancellationToken = default);

    Task<int> RepairBadPreviewsAsync(int maxChars = 200, CancellationToken cancellationToken = default);

    /// <summary>应用一次本地变更（已读 / 星标 / 移动）并排入待写回队列。</summary>
    Task<bool> ApplyMessageChangeAsync(
        string messageId,
        PendingOpKind kind,
        string? targetFolderId = null,
        CancellationToken cancellationToken = default);

    Task<string?> FindDraftIdBySourceAsync(string sourceMessageId, CancellationToken cancellationToken = default);

    Task<int> CountOutboxItemsAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<OutboxEntity>> ListOutboxItemsAsync(CancellationToken cancellationToken = default);

    Task<bool> DeleteOutboxAsync(string id, CancellationToken cancellationToken = default);

    Task<bool> RetryOutboxAsync(string id, DateTimeOffset now, CancellationToken cancellationToken = default);
}
