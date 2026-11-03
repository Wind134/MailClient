namespace MailClient.Storage.Migrations;

/// <summary>
/// V2：发送 / 写回基础设施。
/// pending_ops = 本地先改、后台写回服务器的变更队列；
/// outbox 增加重试 / 幂等 / 已发送副本所需的列；
/// messages.LocalState：0 = 服务器同步而来；1 = 本地生成的已发送副本；2 = 本地已移动、等待服务器重新同步。
/// </summary>
internal static class SchemaV2
{
    public const int Version = 2;

    public const string Sql = """
        CREATE TABLE IF NOT EXISTS pending_ops (
            Id INTEGER PRIMARY KEY AUTOINCREMENT,
            AccountId TEXT NOT NULL,
            MessageId TEXT NOT NULL,
            Field TEXT NOT NULL,
            Kind TEXT NOT NULL,
            SourceFolderId TEXT NULL,
            TargetFolderId TEXT NULL,
            RemoteKey TEXT NULL,
            Attempts INTEGER NOT NULL DEFAULT 0,
            LastError TEXT NULL,
            NextAttemptAt TEXT NULL,
            CreatedAt TEXT NOT NULL,
            UpdatedAt TEXT NOT NULL,
            FOREIGN KEY (AccountId) REFERENCES accounts(Id) ON DELETE CASCADE
        );

        CREATE UNIQUE INDEX IF NOT EXISTS ux_pending_ops_message_field ON pending_ops(MessageId, Field);
        CREATE INDEX IF NOT EXISTS ix_pending_ops_account ON pending_ops(AccountId, CreatedAt);

        ALTER TABLE outbox ADD COLUMN NextAttemptAt TEXT NULL;
        ALTER TABLE outbox ADD COLUMN RfcMessageId TEXT NULL;
        ALTER TABLE outbox ADD COLUMN IsPermanent INTEGER NOT NULL DEFAULT 0;
        ALTER TABLE outbox ADD COLUMN MaybeSent INTEGER NOT NULL DEFAULT 0;
        ALTER TABLE outbox ADD COLUMN SentCopyPending INTEGER NOT NULL DEFAULT 0;
        ALTER TABLE outbox ADD COLUMN CopyAttempts INTEGER NOT NULL DEFAULT 0;

        ALTER TABLE messages ADD COLUMN LocalState INTEGER NOT NULL DEFAULT 0;
        CREATE INDEX IF NOT EXISTS ix_messages_localstate ON messages(FolderId) WHERE LocalState <> 0;
        """;
}
