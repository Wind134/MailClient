using MailClient.Core;

namespace MailClient.Storage;

public sealed partial class MailDatabase
{
    public Task ReplaceRecipientsAsync(string messageId, IEnumerable<RecipientEntity> recipients, CancellationToken cancellationToken = default)
    {
        var list = recipients.ToList();
        return ExecuteAsync(async (connection, ct) =>
        {
            await using var transaction = (Microsoft.Data.Sqlite.SqliteTransaction)await connection.BeginTransactionAsync(ct).ConfigureAwait(false);
            await NonQueryAsync(connection, transaction, "DELETE FROM recipients WHERE MessageId = $id;", ("$id", messageId), ct).ConfigureAwait(false);
            foreach (var r in list)
            {
                await NonQueryAsync(connection, transaction,
                    "INSERT INTO recipients (MessageId, Kind, Address, Name) VALUES ($m, $k, $a, $n);",
                    ("$m", messageId), ("$k", SqliteHelpers.EnumName(r.Kind)), ("$a", r.Address), ("$n", r.Name), ct).ConfigureAwait(false);
            }

            await transaction.CommitAsync(ct).ConfigureAwait(false);
        }, cancellationToken);
    }

    /// <summary>服务器重建了文件夹（UIDVALIDITY 变化）：本地该文件夹的邮件及其待同步操作全部作废。</summary>
    public Task PurgeFolderMessagesAsync(string folderId, CancellationToken cancellationToken = default)
        => ExecuteAsync(async (connection, ct) =>
        {
            await NonQueryAsync(connection, null,
                "DELETE FROM pending_ops WHERE MessageId IN (SELECT Id FROM messages WHERE FolderId = $f);", ("$f", folderId), ct).ConfigureAwait(false);
            await NonQueryAsync(connection, null, "DELETE FROM messages WHERE FolderId = $f;", ("$f", folderId), ct).ConfigureAwait(false);
            await RecalculateFolderCountsCoreAsync(connection, ct).ConfigureAwait(false);
        }, cancellationToken);
}
