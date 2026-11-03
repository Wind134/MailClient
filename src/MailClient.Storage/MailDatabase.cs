using MailClient.Core;
using MailClient.Storage.Migrations;
using Microsoft.Data.Sqlite;

namespace MailClient.Storage;

/// <summary>SQLite 本地库：迁移、账号/文件夹/邮件/发件箱与 FTS 搜索。</summary>
public sealed partial class MailDatabase : IAsyncDisposable
{
    private readonly string _connectionString;
    private SqliteConnection? _connection;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public string DatabasePath { get; }

    public MailDatabase(string databasePath)
    {
        DatabasePath = databasePath;
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            ForeignKeys = true
        }.ToString();
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        var directory = Path.GetDirectoryName(DatabasePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = SchemaV1.Sql;
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await MigrateAsync(connection, cancellationToken).ConfigureAwait(false);

        await using var probe = connection.CreateCommand();
        probe.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE name = 'messages_trgm';";
        SupportsTrigram = Convert.ToInt32(await probe.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false)) > 0;
    }

    /// <summary>trigram 全文索引（messages_trgm）是否可用；不可用时搜索自动降级为 LIKE。</summary>
    public bool SupportsTrigram { get; private set; }

    /// <summary>按版本号依次应用迁移；每个迁移在事务里执行，成功后写入 schema_version。</summary>
    private static async Task MigrateAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        int current;
        await using (var query = connection.CreateCommand())
        {
            query.CommandText = "SELECT COALESCE(MAX(version), 0) FROM schema_version;";
            current = Convert.ToInt32(await query.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false));
        }

        if (current < SchemaV2.Version)
        {
            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = SchemaV2.Sql + $"\nINSERT OR IGNORE INTO schema_version (version) VALUES ({SchemaV2.Version});";
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }

        if (current < SchemaV3.Version)
        {
            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

            async Task RunAsync(string sql)
            {
                await using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = sql;
                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await RunAsync(SchemaV3.DraftsSql).ConfigureAwait(false);

            var trigram = true;
            try
            {
                await RunAsync(SchemaV3.TrigramSql).ConfigureAwait(false);
            }
            catch (SqliteException)
            {
                trigram = false; // 内置 SQLite 太旧 / 没有 trigram 分词器：只用 unicode61 + LIKE
            }

            await RunAsync(trigram ? SchemaV3.TriggersWithTrigramSql : SchemaV3.TriggersWithoutTrigramSql).ConfigureAwait(false);
            if (trigram)
            {
                await RunAsync(SchemaV3.TrigramRebuildSql).ConfigureAwait(false);
            }

            await RunAsync($"INSERT OR IGNORE INTO schema_version (version) VALUES ({SchemaV3.Version});").ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }

        if (current < SchemaV4.Version)
        {
            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = SchemaV4.Sql + $"\nINSERT OR IGNORE INTO schema_version (version) VALUES ({SchemaV4.Version});";
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }

                if (current < SchemaV5.Version)
        {
            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = SchemaV5.Sql + $"\nINSERT OR IGNORE INTO schema_version (version) VALUES ({SchemaV5.Version});";
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }

        if (current < SchemaV6.Version)
        {
            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = SchemaV6.Sql + $"\nINSERT OR IGNORE INTO schema_version (version) VALUES ({SchemaV6.Version});";
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }

        if (current < SchemaV7.Version)
        {
            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = SchemaV7.Sql + $"\nINSERT OR IGNORE INTO schema_version (version) VALUES ({SchemaV7.Version});";
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task<int> GetSchemaVersionAsync(CancellationToken cancellationToken = default)
    {
        var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COALESCE(MAX(version), 0) FROM schema_version;";
        var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return Convert.ToInt32(result);
    }

    public Task UpsertAccountAsync(AccountEntity account, CancellationToken cancellationToken = default)
        => ExecuteAsync(async (connection, ct) =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO accounts (Id, DisplayName, EmailAddress, ProviderId, AuthKind, CreatedAt, UpdatedAt, LastSyncAt, IsEnabled)
                VALUES ($id, $displayName, $email, $providerId, $authKind, $createdAt, $updatedAt, $lastSyncAt, $isEnabled)
                ON CONFLICT(Id) DO UPDATE SET
                    DisplayName = excluded.DisplayName,
                    EmailAddress = excluded.EmailAddress,
                    ProviderId = excluded.ProviderId,
                    AuthKind = excluded.AuthKind,
                    UpdatedAt = excluded.UpdatedAt,
                    LastSyncAt = excluded.LastSyncAt,
                    IsEnabled = excluded.IsEnabled;
                """;
            command.Parameters.AddParam("$id", account.Id.ToString("D"));
            command.Parameters.AddParam("$displayName", account.DisplayName);
            command.Parameters.AddParam("$email", account.EmailAddress);
            command.Parameters.AddParam("$providerId", account.ProviderId);
            command.Parameters.AddParam("$authKind", SqliteHelpers.EnumName(account.AuthKind));
            command.Parameters.AddParam("$createdAt", SqliteHelpers.Fmt(account.CreatedAt));
            command.Parameters.AddParam("$updatedAt", SqliteHelpers.Fmt(account.UpdatedAt));
            command.Parameters.AddParam("$lastSyncAt", account.LastSyncAt is null ? null : SqliteHelpers.Fmt(account.LastSyncAt.Value));
            command.Parameters.AddParam("$isEnabled", account.IsEnabled ? 1 : 0);
            await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }, cancellationToken);

    public Task<AccountEntity?> GetAccountAsync(Guid accountId, CancellationToken cancellationToken = default)
        => QueryAsync(async (connection, ct) =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT Id, DisplayName, EmailAddress, ProviderId, AuthKind, CreatedAt, UpdatedAt, LastSyncAt, IsEnabled
                FROM accounts WHERE Id = $id;
                """;
            command.Parameters.AddParam("$id", accountId.ToString("D"));
            await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
            if (!await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                return null;
            }

            return ReadAccount(reader);
        }, cancellationToken);

    public Task<IReadOnlyList<AccountEntity>> ListAccountsAsync(CancellationToken cancellationToken = default)
        => QueryAsync(async (connection, ct) =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT Id, DisplayName, EmailAddress, ProviderId, AuthKind, CreatedAt, UpdatedAt, LastSyncAt, IsEnabled
                FROM accounts
                ORDER BY CreatedAt;
                """;
            var list = new List<AccountEntity>();
            await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                list.Add(ReadAccount(reader));
            }

            return (IReadOnlyList<AccountEntity>)list;
        }, cancellationToken);

    public Task UpsertFolderAsync(FolderEntity folder, CancellationToken cancellationToken = default)
        => ExecuteAsync(async (connection, ct) =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO folders (Id, AccountId, Name, Special, RemotePath, UnreadCount, TotalCount, SortOrder, SyncCursor, UidValidity, UidNext, HighestModSeq, LastReconcileAt)
                VALUES ($id, $accountId, $name, $special, $remotePath, $unread, $total, $sort, $cursor, $uidValidity, $uidNext, $modSeq, $reconcileAt)
                ON CONFLICT(Id) DO UPDATE SET
                    Name = excluded.Name,
                    Special = excluded.Special,
                    RemotePath = excluded.RemotePath,
                    UnreadCount = excluded.UnreadCount,
                    TotalCount = excluded.TotalCount,
                    SortOrder = excluded.SortOrder,
                    SyncCursor = excluded.SyncCursor,
                    UidValidity = excluded.UidValidity,
                    UidNext = excluded.UidNext,
                    HighestModSeq = excluded.HighestModSeq,
                    LastReconcileAt = excluded.LastReconcileAt;
                """;
            command.Parameters.AddParam("$id", folder.Id);
            command.Parameters.AddParam("$accountId", folder.AccountId.ToString("D"));
            command.Parameters.AddParam("$name", folder.Name);
            command.Parameters.AddParam("$special", SqliteHelpers.EnumName(folder.Special));
            command.Parameters.AddParam("$remotePath", folder.RemotePath);
            command.Parameters.AddParam("$unread", folder.UnreadCount);
            command.Parameters.AddParam("$total", folder.TotalCount);
            command.Parameters.AddParam("$sort", folder.SortOrder);
            command.Parameters.AddParam("$cursor", folder.SyncCursor);
            command.Parameters.AddParam("$uidValidity", folder.UidValidity);
            command.Parameters.AddParam("$uidNext", folder.UidNext);
            command.Parameters.AddParam("$modSeq", folder.HighestModSeq);
            command.Parameters.AddParam("$reconcileAt", folder.LastReconcileAt is null ? null : SqliteHelpers.Fmt(folder.LastReconcileAt.Value));
            await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }, cancellationToken);

    public Task<IReadOnlyList<FolderEntity>> ListFoldersAsync(Guid accountId, CancellationToken cancellationToken = default)
        => QueryAsync(async (connection, ct) =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT Id, AccountId, Name, Special, RemotePath, UnreadCount, TotalCount, SortOrder, SyncCursor, UidValidity, UidNext, HighestModSeq, LastReconcileAt
                FROM folders
                WHERE AccountId = $accountId
                ORDER BY SortOrder, Name;
                """;
            command.Parameters.AddParam("$accountId", accountId.ToString("D"));
            var list = new List<FolderEntity>();
            await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                list.Add(ReadFolder(reader));
            }

            return (IReadOnlyList<FolderEntity>)list;
        }, cancellationToken);

    public Task UpsertMessageAsync(MessageEntity message, CancellationToken cancellationToken = default)
        => ExecuteAsync(async (connection, ct) =>
        {
            await UpsertMessageCoreAsync(connection, null, message, ct).ConfigureAwait(false);
        }, cancellationToken);

    private static async Task UpsertMessageCoreAsync(SqliteConnection connection, SqliteTransaction? transaction, MessageEntity message, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
                INSERT INTO messages (
                    Id, AccountId, FolderId, RemoteUid, RfcMessageId, ThreadKey,
                    Subject, FromAddress, FromName, Preview, BodyText, BodyHtml,
                    ReceivedAt, SentAt, IsRead, IsFlagged, IsDraft, HasAttachments, Category, SizeBytes, BodyFetched, BodyAccessedAt)
                VALUES (
                    $id, $accountId, $folderId, $remoteUid, $rfc, $thread,
                    $subject, $fromAddress, $fromName, $preview, $bodyText, $bodyHtml,
                    $receivedAt, $sentAt, $isRead, $isFlagged, $isDraft, $hasAttachments, $category, $size, $bodyFetched,
                    CASE WHEN $bodyFetched = 1 THEN strftime('%Y-%m-%dT%H:%M:%SZ', 'now') ELSE NULL END)
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
                    BodyAccessedAt = CASE WHEN excluded.BodyFetched = 1 THEN COALESCE(messages.BodyAccessedAt, excluded.BodyAccessedAt) ELSE messages.BodyAccessedAt END,
                    ReceivedAt = excluded.ReceivedAt,
                    SentAt = excluded.SentAt,
                    IsRead = excluded.IsRead,
                    IsFlagged = excluded.IsFlagged,
                    IsDraft = excluded.IsDraft,
                    HasAttachments = excluded.HasAttachments,
                    Category = excluded.Category,
                    SizeBytes = excluded.SizeBytes;
                """;
        BindMessage(command, message);
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public Task<IReadOnlyList<MailMessageSummary>> ListMessagesAsync(
        string folderId,
        int limit = 50,
        CancellationToken cancellationToken = default)
        => QueryAsync(async (connection, ct) =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT Id, FolderId, Subject, FromName, FromAddress, ReceivedAt, IsRead, IsFlagged, Preview
                FROM messages
                WHERE FolderId = $folderId
                ORDER BY ReceivedAt DESC
                LIMIT $limit;
                """;
            command.Parameters.AddParam("$folderId", folderId);
            command.Parameters.AddParam("$limit", limit);
            return await ReadSummariesAsync(command, ct).ConfigureAwait(false);
        }, cancellationToken);

    public Task<MessageEntity?> GetMessageAsync(string messageId, CancellationToken cancellationToken = default)
        => QueryAsync(async (connection, ct) =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT Id, AccountId, FolderId, RemoteUid, RfcMessageId, ThreadKey,
                       Subject, FromAddress, FromName, Preview, BodyText, BodyHtml,
                       ReceivedAt, SentAt, IsRead, IsFlagged, IsDraft, HasAttachments, Category, SizeBytes, BodyFetched
                FROM messages
                WHERE Id = $id;
                """;
            command.Parameters.AddParam("$id", messageId);
            await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
            if (!await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                return null;
            }

            return ReadMessage(reader);
        }, cancellationToken);

    public Task<IReadOnlyList<MailMessageSummary>> SearchMessagesAsync(
        string query,
        int limit = 50,
        CancellationToken cancellationToken = default)
        => QueryAsync(async (connection, ct) =>
        {
            var trimmed = query.Trim();
            if (string.IsNullOrEmpty(trimmed))
            {
                return Array.Empty<MailMessageSummary>();
            }

            await using var command = connection.CreateCommand();
            // unicode61 常把连续 CJK 当成单一 token，子串 MATCH 无效；含 CJK 时改用 LIKE。
            if (trimmed.Any(IsCjk))
            {
                command.CommandText = """
                    SELECT Id, FolderId, Subject, FromName, FromAddress, ReceivedAt, IsRead, IsFlagged, Preview
                    FROM messages
                    WHERE Subject LIKE '%' || $query || '%'
                       OR IFNULL(FromName, '') LIKE '%' || $query || '%'
                       OR FromAddress LIKE '%' || $query || '%'
                       OR Preview LIKE '%' || $query || '%'
                       OR IFNULL(BodyText, '') LIKE '%' || $query || '%'
                    ORDER BY ReceivedAt DESC
                    LIMIT $limit;
                    """;
                command.Parameters.AddParam("$query", trimmed);
            }
            else
            {
                command.CommandText = """
                    SELECT m.Id, m.FolderId, m.Subject, m.FromName, m.FromAddress, m.ReceivedAt, m.IsRead, m.IsFlagged, m.Preview
                    FROM messages_fts f
                    INNER JOIN messages m ON m.rowid = f.rowid
                    WHERE messages_fts MATCH $query
                    ORDER BY m.ReceivedAt DESC
                    LIMIT $limit;
                    """;
                command.Parameters.AddParam("$query", EscapeFtsQuery(trimmed));
            }

            command.Parameters.AddParam("$limit", limit);
            return await ReadSummariesAsync(command, ct).ConfigureAwait(false);
        }, cancellationToken);

    public Task UpsertRecipientAsync(RecipientEntity recipient, CancellationToken cancellationToken = default)
        => ExecuteAsync(async (connection, ct) =>
        {
            await using var command = connection.CreateCommand();
            if (recipient.Id > 0)
            {
                command.CommandText = """
                    UPDATE recipients SET MessageId = $messageId, Kind = $kind, Address = $address, Name = $name
                    WHERE Id = $id;
                    """;
                command.Parameters.AddParam("$id", recipient.Id);
            }
            else
            {
                command.CommandText = """
                    INSERT INTO recipients (MessageId, Kind, Address, Name)
                    VALUES ($messageId, $kind, $address, $name);
                    """;
            }

            command.Parameters.AddParam("$messageId", recipient.MessageId);
            command.Parameters.AddParam("$kind", SqliteHelpers.EnumName(recipient.Kind));
            command.Parameters.AddParam("$address", recipient.Address);
            command.Parameters.AddParam("$name", recipient.Name);
            await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }, cancellationToken);

    public Task UpsertAttachmentAsync(AttachmentEntity attachment, CancellationToken cancellationToken = default)
        => ExecuteAsync(async (connection, ct) =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO attachments (Id, MessageId, FileName, ContentType, SizeBytes, ContentId, IsInline, LocalPath, RemoteId)
                VALUES ($id, $messageId, $fileName, $contentType, $size, $contentId, $isInline, $localPath, $remoteId)
                ON CONFLICT(Id) DO UPDATE SET
                    FileName = excluded.FileName,
                    ContentType = excluded.ContentType,
                    SizeBytes = excluded.SizeBytes,
                    ContentId = excluded.ContentId,
                    IsInline = excluded.IsInline,
                    LocalPath = COALESCE(excluded.LocalPath, attachments.LocalPath),
                    RemoteId = COALESCE(excluded.RemoteId, attachments.RemoteId);
                """;
            command.Parameters.AddParam("$id", attachment.Id);
            command.Parameters.AddParam("$messageId", attachment.MessageId);
            command.Parameters.AddParam("$fileName", attachment.FileName);
            command.Parameters.AddParam("$contentType", attachment.ContentType);
            command.Parameters.AddParam("$size", attachment.SizeBytes);
            command.Parameters.AddParam("$contentId", attachment.ContentId);
            command.Parameters.AddParam("$isInline", attachment.IsInline ? 1 : 0);
            command.Parameters.AddParam("$localPath", attachment.LocalPath);
            command.Parameters.AddParam("$remoteId", attachment.RemoteId);
            await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }, cancellationToken);

    public Task UpsertCredentialsMetaAsync(CredentialsMetaEntity meta, CancellationToken cancellationToken = default)
        => ExecuteAsync(async (connection, ct) =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO credentials_meta (AccountId, VaultKey)
                VALUES ($accountId, $vaultKey)
                ON CONFLICT(AccountId) DO UPDATE SET VaultKey = excluded.VaultKey;
                """;
            command.Parameters.AddParam("$accountId", meta.AccountId.ToString("D"));
            command.Parameters.AddParam("$vaultKey", meta.VaultKey);
            await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }, cancellationToken);

    public Task<CredentialsMetaEntity?> GetCredentialsMetaAsync(Guid accountId, CancellationToken cancellationToken = default)
        => QueryAsync(async (connection, ct) =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT AccountId, VaultKey FROM credentials_meta WHERE AccountId = $accountId;";
            command.Parameters.AddParam("$accountId", accountId.ToString("D"));
            await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
            if (!await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                return null;
            }

            return new CredentialsMetaEntity(Guid.Parse(reader.GetString(0)), reader.GetString(1));
        }, cancellationToken);

    public async ValueTask DisposeAsync()
    {
        if (_connection is not null)
        {
            await _connection.DisposeAsync().ConfigureAwait(false);
            _connection = null;
        }
    }

    private async Task<SqliteConnection> OpenConnectionAsync(CancellationToken cancellationToken)
    {
        if (_connection is { State: System.Data.ConnectionState.Open })
        {
            return _connection;
        }

        _connection?.Dispose();
        _connection = new SqliteConnection(_connectionString);
        await _connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var pragma = _connection.CreateCommand();
        // auto_vacuum=INCREMENTAL：新库建表前生效；老库要等一次 VACUUM（后台维护里做）才会转换，之后删数据腾出的页可以用 incremental_vacuum 还给磁盘。
        pragma.CommandText = "PRAGMA foreign_keys = ON; PRAGMA auto_vacuum = INCREMENTAL;";
        await pragma.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        return _connection;
    }

    private async Task ExecuteAsync(Func<SqliteConnection, CancellationToken, Task> action, CancellationToken cancellationToken)
    {
        // 共享单连接不是线程安全的：UI 会并发读写（列表刷新 / 标记已读 / 搜索），这里串行化。
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            await action(connection, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<T> QueryAsync<T>(Func<SqliteConnection, CancellationToken, Task<T>> action, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            return await action(connection, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }
    private static void BindMessage(SqliteCommand command, MessageEntity message)
    {
        command.Parameters.AddParam("$id", message.Id);
        command.Parameters.AddParam("$accountId", message.AccountId.ToString("D"));
        command.Parameters.AddParam("$folderId", message.FolderId);
        command.Parameters.AddParam("$remoteUid", message.RemoteUid);
        command.Parameters.AddParam("$rfc", message.RfcMessageId);
        command.Parameters.AddParam("$thread", message.ThreadKey);
        command.Parameters.AddParam("$subject", message.Subject);
        command.Parameters.AddParam("$fromAddress", message.FromAddress);
        command.Parameters.AddParam("$fromName", message.FromName);
        command.Parameters.AddParam("$preview", message.Preview);
        command.Parameters.AddParam("$bodyText", message.BodyText);
        command.Parameters.AddParam("$bodyHtml", message.BodyHtml);
        command.Parameters.AddParam("$receivedAt", message.ReceivedAt is null ? null : SqliteHelpers.Fmt(message.ReceivedAt.Value));
        command.Parameters.AddParam("$sentAt", message.SentAt is null ? null : SqliteHelpers.Fmt(message.SentAt.Value));
        command.Parameters.AddParam("$isRead", message.IsRead ? 1 : 0);
        command.Parameters.AddParam("$isFlagged", message.IsFlagged ? 1 : 0);
        command.Parameters.AddParam("$isDraft", message.IsDraft ? 1 : 0);
        command.Parameters.AddParam("$hasAttachments", message.HasAttachments ? 1 : 0);
        command.Parameters.AddParam("$category", message.Category is null ? null : SqliteHelpers.EnumName(message.Category.Value));
        command.Parameters.AddParam("$size", message.SizeBytes);
        var fetched = message.BodyFetched || message.BodyText is not null || message.BodyHtml is not null;
        command.Parameters.AddParam("$bodyFetched", fetched ? 1 : 0);
    }

    private static AccountEntity ReadAccount(SqliteDataReader reader)
        => new(
            Guid.Parse(reader.GetString(0)),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            SqliteHelpers.ParseEnum<AuthKind>(reader.GetString(4)),
            SqliteHelpers.ParseDate(reader.GetString(5))!.Value,
            SqliteHelpers.ParseDate(reader.GetString(6))!.Value,
            reader.IsDBNull(7) ? null : SqliteHelpers.ParseDate(reader.GetString(7)),
            reader.GetInt32(8) != 0);

    private static FolderEntity ReadFolder(SqliteDataReader reader)
        => new(
            reader.GetString(0),
            Guid.Parse(reader.GetString(1)),
            reader.GetString(2),
            SqliteHelpers.ParseEnum<SpecialFolder>(reader.GetString(3)),
            reader.IsDBNull(4) ? null : reader.GetString(4),
            reader.GetInt32(5),
            reader.GetInt32(6),
            reader.GetInt32(7),
            reader.IsDBNull(8) ? null : reader.GetString(8),
            reader.IsDBNull(9) ? null : reader.GetInt64(9),
            reader.FieldCount > 10 && !reader.IsDBNull(10) ? reader.GetInt64(10) : null,
            reader.FieldCount > 11 && !reader.IsDBNull(11) ? reader.GetInt64(11) : null,
            reader.FieldCount > 12 && !reader.IsDBNull(12) ? SqliteHelpers.ParseDate(reader.GetString(12)) : null);

    private static MessageEntity ReadMessage(SqliteDataReader reader)
        => new(
            reader.GetString(0),
            Guid.Parse(reader.GetString(1)),
            reader.GetString(2),
            reader.IsDBNull(3) ? null : reader.GetInt64(3),
            reader.IsDBNull(4) ? null : reader.GetString(4),
            reader.IsDBNull(5) ? null : reader.GetString(5),
            reader.GetString(6),
            reader.GetString(7),
            reader.IsDBNull(8) ? null : reader.GetString(8),
            reader.GetString(9),
            reader.IsDBNull(10) ? null : reader.GetString(10),
            reader.IsDBNull(11) ? null : reader.GetString(11),
            reader.IsDBNull(12) ? null : SqliteHelpers.ParseDate(reader.GetString(12)),
            reader.IsDBNull(13) ? null : SqliteHelpers.ParseDate(reader.GetString(13)),
            reader.GetInt32(14) != 0,
            reader.GetInt32(15) != 0,
            reader.GetInt32(16) != 0,
            reader.GetInt32(17) != 0,
            SqliteHelpers.ParseEnumOrNull<MailCategory>(reader.IsDBNull(18) ? null : reader.GetString(18)),
            reader.GetInt64(19),
            reader.FieldCount > 20 && !reader.IsDBNull(20) && reader.GetInt32(20) != 0);

    private static async Task<IReadOnlyList<MailMessageSummary>> ReadSummariesAsync(
        SqliteCommand command,
        CancellationToken cancellationToken)
    {
        var list = new List<MailMessageSummary>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var fromName = reader.IsDBNull(3) ? null : reader.GetString(3);
            var fromAddress = reader.GetString(4);
            var from = string.IsNullOrWhiteSpace(fromName) ? fromAddress : $"{fromName} <{fromAddress}>";
            list.Add(new MailMessageSummary(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                from,
                SqliteHelpers.ParseDate(reader.IsDBNull(5) ? null : reader.GetString(5)) ?? DateTimeOffset.MinValue,
                reader.GetInt32(6) != 0,
                reader.GetInt32(7) != 0,
                reader.GetString(8)));
        }

        return list;
    }

    /// <summary>
    /// FTS 查询转义：空白分词后 AND。
    /// 拉丁词加引号防特殊字符；CJK 不加引号，交给 unicode61 按字分词（"周报" → 周 AND 报）。
    /// </summary>
    private static string EscapeFtsQuery(string query)
    {
        var tokens = query.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (tokens.Length == 0)
        {
            return "\"\"";
        }

        return string.Join(" AND ", tokens.Select(EscapeToken));
    }

    private static string EscapeToken(string token)
    {
        if (token.Any(IsCjk))
        {
            return token;
        }

        return $"\"{token.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";
    }

    private static bool IsCjk(char ch)
        => ch is >= '\u3040' and <= '\u30FF'
            or >= '\u3400' and <= '\u4DBF'
            or >= '\u4E00' and <= '\u9FFF'
            or >= '\uF900' and <= '\uFAFF';
}

