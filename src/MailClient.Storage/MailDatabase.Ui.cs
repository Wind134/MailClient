using MailClient.Core;
using MailClient.Core.Search;
using MailClient.Storage.Search;
using Microsoft.Data.Sqlite;

namespace MailClient.Storage;

/// <summary>邮件列表过滤条件（UI 三栏用）。</summary>
public sealed record MessageListFilter(
    IReadOnlyList<string>? FolderIds = null,
    MailCategory? Category = null,
    bool UnreadOnly = false,
    bool FlaggedOnly = false,
    string? SearchText = null,
    int Limit = 200,
    Guid? AccountId = null,
    bool ExcludeTrashAndJunk = false);

/// <summary>面向 UI 的查询 / 变更（与核心 CRUD 分文件，便于阅读）。</summary>
public sealed partial class MailDatabase
{
    private const string MessageColumns =
        "m.Id, m.AccountId, m.FolderId, m.RemoteUid, m.RfcMessageId, m.ThreadKey, m.Subject, m.FromAddress, m.FromName, m.Preview, " +
        "NULL AS BodyText, NULL AS BodyHtml, m.ReceivedAt, m.SentAt, m.IsRead, m.IsFlagged, m.IsDraft, m.HasAttachments, m.Category, m.SizeBytes";

    /// <summary>按条件查询邮件（不含正文，轻量），按时间倒序。</summary>
    public Task<IReadOnlyList<MessageEntity>> QueryMessagesAsync(MessageListFilter filter, CancellationToken cancellationToken = default)
        => QueryAsync(async (connection, ct) =>
        {
            await using var command = connection.CreateCommand();
            var where = new List<string>();

            if (filter.FolderIds is not null)
            {
                if (filter.FolderIds.Count == 0)
                {
                    return (IReadOnlyList<MessageEntity>)Array.Empty<MessageEntity>();
                }

                var names = new List<string>();
                for (var i = 0; i < filter.FolderIds.Count; i++)
                {
                    var name = $"$f{i}";
                    names.Add(name);
                    command.Parameters.AddParam(name, filter.FolderIds[i]);
                }

                where.Add($"m.FolderId IN ({string.Join(", ", names)})");
            }

            if (filter.Category is { } category)
            {
                // 旧数据 / 未分类（NULL）按“主要”显示，否则这些邮件在所有标签页里都看不到。
                where.Add(category == MailCategory.Primary
                    ? "(m.Category = $category OR m.Category IS NULL)"
                    : "m.Category = $category");
                command.Parameters.AddParam("$category", SqliteHelpers.EnumName(category));
            }

            if (filter.UnreadOnly)
            {
                where.Add("m.IsRead = 0");
            }

            if (filter.FlaggedOnly)
            {
                where.Add("m.IsFlagged = 1");
            }

            if (filter.AccountId is { } accountId)
            {
                where.Add("m.AccountId = $scopeAccount");
                command.Parameters.AddParam("$scopeAccount", accountId.ToString("D"));
            }

            if (filter.ExcludeTrashAndJunk)
            {
                where.Add("m.FolderId NOT IN (SELECT Id FROM folders WHERE Special IN ('Trash', 'Junk'))");
            }

            // 由服务器草稿重新打开、已有本地草稿接管的那封：列表里只显示本地草稿
            where.Add("m.Id NOT IN (SELECT SourceMessageId FROM drafts WHERE SourceMessageId IS NOT NULL)");

            if (!string.IsNullOrWhiteSpace(filter.SearchText))
            {
                var sql = SearchSqlBuilder.Build(SearchQueryParser.Parse(filter.SearchText), SupportsTrigram);
                where.AddRange(sql.Clauses);
                foreach (var parameter in sql.Parameters)
                {
                    command.Parameters.AddParam(parameter.Key, parameter.Value);
                }
            }

            command.CommandText =
                $"SELECT {MessageColumns} FROM messages AS m " +
                (where.Count > 0 ? "WHERE " + string.Join(" AND ", where) + " " : string.Empty) +
                "ORDER BY m.ReceivedAt DESC LIMIT $limit;";
            command.Parameters.AddParam("$limit", filter.Limit);

            var list = new List<MessageEntity>();
            await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                list.Add(ReadMessage(reader));
            }

            return (IReadOnlyList<MessageEntity>)list;
        }, cancellationToken);

    public Task<IReadOnlyList<FolderEntity>> ListAllFoldersAsync(CancellationToken cancellationToken = default)
        => QueryAsync(async (connection, ct) =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT Id, AccountId, Name, Special, RemotePath, UnreadCount, TotalCount, SortOrder, SyncCursor, UidValidity, UidNext, HighestModSeq, LastReconcileAt
                FROM folders
                ORDER BY AccountId, SortOrder, Name;
                """;
            var list = new List<FolderEntity>();
            await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                list.Add(ReadFolder(reader));
            }

            return (IReadOnlyList<FolderEntity>)list;
        }, cancellationToken);

    public Task<IReadOnlyList<RecipientEntity>> ListRecipientsAsync(string messageId, CancellationToken cancellationToken = default)
        => QueryAsync(async (connection, ct) =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT Id, MessageId, Kind, Address, Name FROM recipients WHERE MessageId = $id ORDER BY Id;";
            command.Parameters.AddParam("$id", messageId);
            var list = new List<RecipientEntity>();
            await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                list.Add(new RecipientEntity(
                    reader.GetInt64(0),
                    reader.GetString(1),
                    SqliteHelpers.ParseEnum<RecipientKind>(reader.GetString(2)),
                    reader.GetString(3),
                    reader.IsDBNull(4) ? null : reader.GetString(4)));
            }

            return (IReadOnlyList<RecipientEntity>)list;
        }, cancellationToken);

    public Task<IReadOnlyList<AttachmentEntity>> ListAttachmentsAsync(string messageId, CancellationToken cancellationToken = default)
        => QueryAsync(async (connection, ct) =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT Id, MessageId, FileName, ContentType, SizeBytes, ContentId, IsInline, LocalPath, RemoteId
                FROM attachments WHERE MessageId = $id ORDER BY FileName;
                """;
            command.Parameters.AddParam("$id", messageId);
            var list = new List<AttachmentEntity>();
            await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                list.Add(new AttachmentEntity(
                    reader.GetString(0),
                    reader.GetString(1),
                    reader.GetString(2),
                    reader.IsDBNull(3) ? null : reader.GetString(3),
                    reader.GetInt64(4),
                    reader.IsDBNull(5) ? null : reader.GetString(5),
                    reader.GetInt32(6) != 0,
                    reader.IsDBNull(7) ? null : reader.GetString(7),
                    reader.FieldCount > 8 && !reader.IsDBNull(8) ? reader.GetString(8) : null));
            }

            return (IReadOnlyList<AttachmentEntity>)list;
        }, cancellationToken);

    public Task SetMessageReadAsync(string messageId, bool isRead, CancellationToken cancellationToken = default)
        => ExecuteAsync(async (connection, ct) =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "UPDATE messages SET IsRead = $v WHERE Id = $id;";
            command.Parameters.AddParam("$v", isRead ? 1 : 0);
            command.Parameters.AddParam("$id", messageId);
            await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            await RecalculateFolderCountsCoreAsync(connection, ct).ConfigureAwait(false);
        }, cancellationToken);

    public Task SetMessageFlaggedAsync(string messageId, bool isFlagged, CancellationToken cancellationToken = default)
        => ExecuteAsync(async (connection, ct) =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "UPDATE messages SET IsFlagged = $v WHERE Id = $id;";
            command.Parameters.AddParam("$v", isFlagged ? 1 : 0);
            command.Parameters.AddParam("$id", messageId);
            await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }, cancellationToken);

    public Task MoveMessageAsync(string messageId, string destinationFolderId, CancellationToken cancellationToken = default)
        => ExecuteAsync(async (connection, ct) =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "UPDATE messages SET FolderId = $folder WHERE Id = $id;";
            command.Parameters.AddParam("$folder", destinationFolderId);
            command.Parameters.AddParam("$id", messageId);
            await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            await RecalculateFolderCountsCoreAsync(connection, ct).ConfigureAwait(false);
        }, cancellationToken);

    public Task DeleteMessageAsync(string messageId, CancellationToken cancellationToken = default)
        => ExecuteAsync(async (connection, ct) =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM messages WHERE Id = $id;";
            command.Parameters.AddParam("$id", messageId);
            await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            await RecalculateFolderCountsCoreAsync(connection, ct).ConfigureAwait(false);
        }, cancellationToken);

    public Task RecalculateFolderCountsAsync(CancellationToken cancellationToken = default)
        => ExecuteAsync(RecalculateFolderCountsCoreAsync, cancellationToken);

    private static async Task RecalculateFolderCountsCoreAsync(SqliteConnection connection, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE folders SET
                UnreadCount = (SELECT COUNT(*) FROM messages m WHERE m.FolderId = folders.Id AND m.IsRead = 0 AND m.Id NOT IN (SELECT SourceMessageId FROM drafts WHERE SourceMessageId IS NOT NULL)),
                TotalCount  = (SELECT COUNT(*) FROM messages m WHERE m.FolderId = folders.Id AND m.Id NOT IN (SELECT SourceMessageId FROM drafts WHERE SourceMessageId IS NOT NULL));
            """;
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    /// <summary>删除账号及其全部本地数据（文件夹 / 邮件 / 收件人 / 附件 / 发件箱 / 凭据元数据由外键级联删除）。</summary>
    public Task DeleteAccountAsync(Guid accountId, CancellationToken cancellationToken = default)
        => ExecuteAsync(async (connection, ct) =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM accounts WHERE Id = $id;";
            command.Parameters.AddParam("$id", accountId.ToString("D"));
            await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }, cancellationToken);

    /// <summary>取一批邮件的正文纯文本（列表查询不带正文；搜索结果的“正文摘要”用）。</summary>
    public Task<IReadOnlyDictionary<string, string>> GetBodyTextsAsync(IReadOnlyList<string> messageIds, CancellationToken cancellationToken = default)
        => QueryAsync(async (connection, ct) =>
        {
            var result = new Dictionary<string, string>();
            foreach (var chunk in messageIds.Chunk(500))
            {
                await using var command = connection.CreateCommand();
                var names = new List<string>();
                for (var i = 0; i < chunk.Length; i++)
                {
                    names.Add($"$i{i}");
                    command.Parameters.AddParam($"$i{i}", chunk[i]);
                }

                command.CommandText = $"SELECT Id, BodyText FROM messages WHERE BodyText IS NOT NULL AND Id IN ({string.Join(", ", names)});";
                await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
                while (await reader.ReadAsync(ct).ConfigureAwait(false))
                {
                    result[reader.GetString(0)] = reader.GetString(1);
                }
            }

            return (IReadOnlyDictionary<string, string>)result;
        }, cancellationToken);

    public Task<string> GetSqliteVersionAsync(CancellationToken cancellationToken = default)
        => QueryAsync(async (connection, ct) =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT sqlite_version();";
            return Convert.ToString(await command.ExecuteScalarAsync(ct).ConfigureAwait(false)) ?? string.Empty;
        }, cancellationToken);
}
