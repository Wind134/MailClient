using MailClient.Core;
using MailClient.Core.Notifications;
using Microsoft.Data.Sqlite;

namespace MailClient.Storage;

/// <summary>同步前对某账号收件箱拍的快照：同步后据此判断哪些邮件是“新到的”。</summary>
public sealed record InboxSnapshot(IReadOnlySet<string> KnownIds, bool IsInitialSync);

public sealed partial class MailDatabase
{
    /// <summary>某账号所有收件箱文件夹里的邮件 Id（很轻：只取 Id）。</summary>
    public Task<IReadOnlySet<string>> ListInboxMessageIdsAsync(Guid accountId, CancellationToken cancellationToken = default)
        => QueryAsync(async (connection, ct) =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText =
                "SELECT m.Id FROM messages AS m JOIN folders AS f ON f.Id = m.FolderId " +
                "WHERE f.AccountId = $a AND f.Special = 'Inbox';";
            command.Parameters.AddParam("$a", accountId.ToString("D"));
            var set = new HashSet<string>(StringComparer.Ordinal);
            await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                set.Add(reader.GetString(0));
            }

            return (IReadOnlySet<string>)set;
        }, cancellationToken);

    /// <summary>
    /// 同步前拍快照。“首次完整同步”＝账号还没有收件箱文件夹，或收件箱还没有同步游标
    /// （首次同步成功后才会写游标）——这时拉下来的旧邮件不该刷一屏通知。
    /// </summary>
    public async Task<InboxSnapshot> SnapshotInboxAsync(Guid accountId, CancellationToken cancellationToken = default)
    {
        var folders = await ListFoldersAsync(accountId, cancellationToken).ConfigureAwait(false);
        var inbox = folders.FirstOrDefault(f => f.Special == SpecialFolder.Inbox);
        var initial = inbox is null || string.IsNullOrEmpty(inbox.SyncCursor);
        var ids = await ListInboxMessageIdsAsync(accountId, cancellationToken).ConfigureAwait(false);
        return new InboxSnapshot(ids, initial);
    }

    /// <summary>
    /// 同步后：找出收件箱里不在快照中的未读邮件，并套用 <see cref="NewMailPolicy"/>。
    /// </summary>
    public async Task<IReadOnlyList<NewMailCandidate>> FindNewMailAsync(
        AccountEntity account, InboxSnapshot before, bool notificationsEnabled, DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        if (!notificationsEnabled || before.IsInitialSync)
        {
            return Array.Empty<NewMailCandidate>();
        }

        var folders = await ListFoldersAsync(account.Id, cancellationToken).ConfigureAwait(false);
        var inboxIds = folders.Where(f => f.Special == SpecialFolder.Inbox).Select(f => f.Id).ToList();
        if (inboxIds.Count == 0)
        {
            return Array.Empty<NewMailCandidate>();
        }

        var unread = await QueryMessagesAsync(new MessageListFilter(FolderIds: inboxIds, UnreadOnly: true, Limit: 300, AccountId: account.Id), cancellationToken)
            .ConfigureAwait(false);
        var candidates = unread
            .Where(m => !before.KnownIds.Contains(m.Id))
            .Select(m => new NewMailCandidate(
                m.Id, account.Id, account.DisplayName, SpecialFolder.Inbox, m.FromAddress, m.FromName,
                m.Subject, m.Preview, m.IsRead, m.IsDraft, m.ReceivedAt ?? m.SentAt))
            .ToList();
        if (candidates.Count == 0)
        {
            return candidates;
        }

        var own = NewMailPolicy.NormalizeAddresses((await ListAccountsAsync(cancellationToken).ConfigureAwait(false)).Select(a => a.EmailAddress));
        return NewMailPolicy.Select(candidates, before.KnownIds, before.IsInitialSync, notificationsEnabled, own, now);
    }
}