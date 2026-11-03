using MailClient.Core;
using MailClient.Core.Sending;
using MailClient.Core.Services;
using MailClient.Core.Sync;

namespace MailClient.Storage.Services;

/// <summary>
/// 草稿存储：完整状态写 drafts 表，列表里的那一行写成账号“草稿”文件夹里 id 为 "draft:{id}" 的邮件
/// （ReceivedAt = 最后编辑时间，所以列表按最近编辑排序）；附件复制到应用自己的目录。
/// </summary>
public sealed class DatabaseDraftStore : IDraftStore
{
    private const string Prefix = MailDatabase.DraftMessagePrefix;
    private const string NoSubject = "(无主题)";
    private readonly MailDatabase _database;
    private readonly string? _attachmentRoot;
    private readonly Func<DateTimeOffset> _clock;

    public DatabaseDraftStore(MailDatabase database, Func<DateTimeOffset>? clock = null)
        : this(database, null, clock)
    {
    }

    /// <param name="attachmentRoot">草稿附件目录（%LocalAppData%\MailClient\drafts）；null = 不复制附件（只记路径）。</param>
    public DatabaseDraftStore(MailDatabase database, string? attachmentRoot, Func<DateTimeOffset>? clock = null)
    {
        _database = database;
        _attachmentRoot = attachmentRoot;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    public async Task SaveAsync(DraftContent draft, CancellationToken cancellationToken = default)
    {
        var folders = await _database.ListFoldersAsync(draft.AccountId, cancellationToken).ConfigureAwait(false);
        var drafts = folders.FirstOrDefault(f => f.Special == SpecialFolder.Drafts);
        if (drafts is null)
        {
            return; // 该账号还没有“草稿”文件夹（尚未同步过）：先不保存
        }

        var accounts = await _database.ListAccountsAsync(cancellationToken).ConfigureAwait(false);
        var account = accounts.FirstOrDefault(a => a.Id == draft.AccountId);
        var now = _clock();
        var id = Prefix + draft.Id;

        IReadOnlyList<string> attachmentPaths = draft.Attachments ?? Array.Empty<string>();
        if (_attachmentRoot is not null)
        {
            attachmentPaths = DraftAttachmentStore.Sync(_attachmentRoot, draft.Id, attachmentPaths);
        }

        var body = draft.Body ?? string.Empty;
        var preview = body.Length > 120 ? body[..120] : body;
        var entity = new MessageEntity(
            id, draft.AccountId, drafts.Id, null, null, null,
            string.IsNullOrWhiteSpace(draft.Subject) ? NoSubject : draft.Subject,
            account?.EmailAddress ?? string.Empty, account?.DisplayName,
            preview.Replace('\r', ' ').Replace('\n', ' '),
            body, null, now, now, true, false, true, attachmentPaths.Count > 0, null, body.Length);

        var recipients = new List<RecipientEntity>();
        recipients.AddRange(Split(draft.To).Select(a => new RecipientEntity(0, id, RecipientKind.To, a, null)));
        recipients.AddRange(Split(draft.Cc).Select(a => new RecipientEntity(0, id, RecipientKind.Cc, a, null)));
        recipients.AddRange(Split(draft.Bcc).Select(a => new RecipientEntity(0, id, RecipientKind.Bcc, a, null)));

        var attachments = attachmentPaths.Select((path, i) => new AttachmentEntity(
            $"{id}:att:{i}", id, Path.GetFileName(path), null, SafeLength(path), null, false, path)).ToList();

        await _database.SaveDraftAsync(draft with { Attachments = attachmentPaths.ToList(), UpdatedAt = now },
            entity, recipients, attachments, now, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>删除整个草稿：状态行、列表行、附件副本，以及它由之而来的服务器草稿（若有，排队写回服务器删除）。</summary>
    public async Task DeleteAsync(string draftId, CancellationToken cancellationToken = default)
    {
        var source = await _database.DeleteDraftAsync(draftId, cancellationToken).ConfigureAwait(false);
        DraftAttachmentStore.Delete(_attachmentRoot, draftId);
        if (source is not null)
        {
            await _database.ApplyMessageChangeAsync(source, PendingOpKind.Delete, null, cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task<DraftContent?> GetAsync(string draftId, CancellationToken cancellationToken = default)
    {
        var row = await _database.GetDraftAsync(draftId, cancellationToken).ConfigureAwait(false);
        if (row is not null)
        {
            return row;
        }

        // 升级前保存的旧草稿：只有列表行（没有 drafts 状态行），尽量还原基本字段
        var m = await _database.GetMessageAsync(Prefix + draftId, cancellationToken).ConfigureAwait(false);
        if (m is null)
        {
            return null;
        }

        var r = await _database.ListRecipientsAsync(m.Id, cancellationToken).ConfigureAwait(false);
        string Join(RecipientKind k) => string.Join("; ", r.Where(x => x.Kind == k).Select(x => x.Address));
        return new DraftContent(draftId, m.AccountId, Join(RecipientKind.To), Join(RecipientKind.Cc), Join(RecipientKind.Bcc),
            m.Subject == NoSubject ? string.Empty : m.Subject, m.BodyText ?? string.Empty, UpdatedAt: m.ReceivedAt);
    }

    /// <summary>清理草稿附件目录里没有对应草稿的残留（崩溃 / 账号被删）。启动时调用。</summary>
    public async Task<int> CleanOrphanAttachmentsAsync(CancellationToken cancellationToken = default)
    {
        if (_attachmentRoot is null)
        {
            return 0;
        }

        var ids = await _database.ListDraftIdsAsync(cancellationToken).ConfigureAwait(false);
        return DraftAttachmentStore.DeleteOrphans(_attachmentRoot, ids.ToHashSet(StringComparer.OrdinalIgnoreCase));
    }

    private static long SafeLength(string path)
    {
        try
        {
            return new FileInfo(path).Length;
        }
        catch (IOException)
        {
            return 0;
        }
    }

    private static IEnumerable<string> Split(string text)
        => (text ?? string.Empty).Split([';', ',', '，', '；', ' ', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
