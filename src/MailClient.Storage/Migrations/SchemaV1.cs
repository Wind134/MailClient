namespace MailClient.Storage.Migrations;

internal static class SchemaV1
{
    public const int Version = 1;

    public const string Sql = """
        PRAGMA foreign_keys = ON;

        CREATE TABLE IF NOT EXISTS schema_version (
            version INTEGER PRIMARY KEY
        );

        CREATE TABLE IF NOT EXISTS accounts (
            Id TEXT NOT NULL PRIMARY KEY,
            DisplayName TEXT NOT NULL,
            EmailAddress TEXT NOT NULL,
            ProviderId TEXT NOT NULL,
            AuthKind TEXT NOT NULL,
            CreatedAt TEXT NOT NULL,
            UpdatedAt TEXT NOT NULL,
            LastSyncAt TEXT NULL,
            IsEnabled INTEGER NOT NULL DEFAULT 1
        );

        CREATE TABLE IF NOT EXISTS folders (
            Id TEXT NOT NULL PRIMARY KEY,
            AccountId TEXT NOT NULL,
            Name TEXT NOT NULL,
            Special TEXT NOT NULL,
            RemotePath TEXT NULL,
            UnreadCount INTEGER NOT NULL DEFAULT 0,
            TotalCount INTEGER NOT NULL DEFAULT 0,
            SortOrder INTEGER NOT NULL DEFAULT 0,
            SyncCursor TEXT NULL,
            UidValidity INTEGER NULL,
            FOREIGN KEY (AccountId) REFERENCES accounts(Id) ON DELETE CASCADE
        );

        CREATE INDEX IF NOT EXISTS ix_folders_account ON folders(AccountId);

        CREATE TABLE IF NOT EXISTS messages (
            Id TEXT NOT NULL PRIMARY KEY,
            AccountId TEXT NOT NULL,
            FolderId TEXT NOT NULL,
            RemoteUid INTEGER NULL,
            RfcMessageId TEXT NULL,
            ThreadKey TEXT NULL,
            Subject TEXT NOT NULL DEFAULT '',
            FromAddress TEXT NOT NULL DEFAULT '',
            FromName TEXT NULL,
            Preview TEXT NOT NULL DEFAULT '',
            BodyText TEXT NULL,
            BodyHtml TEXT NULL,
            ReceivedAt TEXT NULL,
            SentAt TEXT NULL,
            IsRead INTEGER NOT NULL DEFAULT 0,
            IsFlagged INTEGER NOT NULL DEFAULT 0,
            IsDraft INTEGER NOT NULL DEFAULT 0,
            HasAttachments INTEGER NOT NULL DEFAULT 0,
            Category TEXT NULL,
            SizeBytes INTEGER NOT NULL DEFAULT 0,
            FOREIGN KEY (AccountId) REFERENCES accounts(Id) ON DELETE CASCADE,
            FOREIGN KEY (FolderId) REFERENCES folders(Id) ON DELETE CASCADE
        );

        CREATE INDEX IF NOT EXISTS ix_messages_folder_received ON messages(FolderId, ReceivedAt DESC);
        CREATE INDEX IF NOT EXISTS ix_messages_account ON messages(AccountId);
        CREATE INDEX IF NOT EXISTS ix_messages_rfc ON messages(RfcMessageId);
        CREATE UNIQUE INDEX IF NOT EXISTS ux_messages_folder_uid
            ON messages(FolderId, RemoteUid) WHERE RemoteUid IS NOT NULL;

        CREATE TABLE IF NOT EXISTS recipients (
            Id INTEGER PRIMARY KEY AUTOINCREMENT,
            MessageId TEXT NOT NULL,
            Kind TEXT NOT NULL,
            Address TEXT NOT NULL,
            Name TEXT NULL,
            FOREIGN KEY (MessageId) REFERENCES messages(Id) ON DELETE CASCADE
        );

        CREATE INDEX IF NOT EXISTS ix_recipients_message ON recipients(MessageId);

        CREATE TABLE IF NOT EXISTS attachments (
            Id TEXT NOT NULL PRIMARY KEY,
            MessageId TEXT NOT NULL,
            FileName TEXT NOT NULL,
            ContentType TEXT NULL,
            SizeBytes INTEGER NOT NULL DEFAULT 0,
            ContentId TEXT NULL,
            IsInline INTEGER NOT NULL DEFAULT 0,
            LocalPath TEXT NULL,
            FOREIGN KEY (MessageId) REFERENCES messages(Id) ON DELETE CASCADE
        );

        CREATE INDEX IF NOT EXISTS ix_attachments_message ON attachments(MessageId);

        CREATE TABLE IF NOT EXISTS outbox (
            Id TEXT NOT NULL PRIMARY KEY,
            AccountId TEXT NOT NULL,
            PayloadJson TEXT NOT NULL,
            Status TEXT NOT NULL,
            Attempts INTEGER NOT NULL DEFAULT 0,
            LastError TEXT NULL,
            CreatedAt TEXT NOT NULL,
            UpdatedAt TEXT NOT NULL,
            FOREIGN KEY (AccountId) REFERENCES accounts(Id) ON DELETE CASCADE
        );

        CREATE INDEX IF NOT EXISTS ix_outbox_status ON outbox(Status, CreatedAt);

        CREATE TABLE IF NOT EXISTS credentials_meta (
            AccountId TEXT NOT NULL PRIMARY KEY,
            VaultKey TEXT NOT NULL,
            FOREIGN KEY (AccountId) REFERENCES accounts(Id) ON DELETE CASCADE
        );

        CREATE VIRTUAL TABLE IF NOT EXISTS messages_fts USING fts5(
            Subject,
            FromName,
            FromAddress,
            Preview,
            BodyText,
            content='messages',
            content_rowid='rowid'
        );

        CREATE TRIGGER IF NOT EXISTS messages_ai AFTER INSERT ON messages BEGIN
            INSERT INTO messages_fts(rowid, Subject, FromName, FromAddress, Preview, BodyText)
            VALUES (new.rowid, new.Subject, new.FromName, new.FromAddress, new.Preview, new.BodyText);
        END;

        CREATE TRIGGER IF NOT EXISTS messages_ad AFTER DELETE ON messages BEGIN
            INSERT INTO messages_fts(messages_fts, rowid, Subject, FromName, FromAddress, Preview, BodyText)
            VALUES ('delete', old.rowid, old.Subject, old.FromName, old.FromAddress, old.Preview, old.BodyText);
        END;

        CREATE TRIGGER IF NOT EXISTS messages_au AFTER UPDATE ON messages BEGIN
            INSERT INTO messages_fts(messages_fts, rowid, Subject, FromName, FromAddress, Preview, BodyText)
            VALUES ('delete', old.rowid, old.Subject, old.FromName, old.FromAddress, old.Preview, old.BodyText);
            INSERT INTO messages_fts(rowid, Subject, FromName, FromAddress, Preview, BodyText)
            VALUES (new.rowid, new.Subject, new.FromName, new.FromAddress, new.Preview, new.BodyText);
        END;

        INSERT OR IGNORE INTO schema_version(version) VALUES (1);
        """;
}
