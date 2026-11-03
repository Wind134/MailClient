namespace MailClient.Storage.Migrations;

/// <summary>
/// V3：草稿重新打开 + 本地搜索。
/// drafts = 草稿的完整状态（收件人 / 附件 / 回复上下文），对应的列表行仍是 messages 里 id 为 "draft:{id}" 的一行；
/// messages_trgm = FTS5 trigram 索引（≥3 个字符的任意子串，含中文），与 messages_fts（unicode61，词前缀）并存；
/// 触发器改为“只有被索引的列真的变了才重建索引”，已读 / 星标 / 移动 / 同步重写同样内容都不再触发重建。
/// trigram 需要 SQLite ≥ 3.34；不支持时建表会失败，<see cref="TrigramSql"/> 单独执行、失败即忽略，搜索自动降级为 LIKE。
/// </summary>
internal static class SchemaV3
{
    public const int Version = 3;

    public const string DraftsSql = """
        CREATE TABLE IF NOT EXISTS drafts (
            Id TEXT NOT NULL PRIMARY KEY,
            AccountId TEXT NOT NULL,
            Mode TEXT NOT NULL DEFAULT 'New',
            ToText TEXT NOT NULL DEFAULT '',
            CcText TEXT NOT NULL DEFAULT '',
            BccText TEXT NOT NULL DEFAULT '',
            Subject TEXT NOT NULL DEFAULT '',
            Body TEXT NOT NULL DEFAULT '',
            InReplyTo TEXT NULL,
            ReferencesJson TEXT NULL,
            QuotedHtml TEXT NULL,
            QuotedText TEXT NULL,
            AttachmentsJson TEXT NULL,
            SourceMessageId TEXT NULL,
            CreatedAt TEXT NOT NULL,
            UpdatedAt TEXT NOT NULL,
            FOREIGN KEY (AccountId) REFERENCES accounts(Id) ON DELETE CASCADE
        );

        CREATE INDEX IF NOT EXISTS ix_drafts_source ON drafts(SourceMessageId) WHERE SourceMessageId IS NOT NULL;
        CREATE INDEX IF NOT EXISTS ix_drafts_account ON drafts(AccountId, UpdatedAt);
        """;

    /// <summary>只在支持 trigram 时成功。</summary>
    public const string TrigramSql = """
        CREATE VIRTUAL TABLE IF NOT EXISTS messages_trgm USING fts5(
            Subject, FromName, FromAddress, Preview, BodyText,
            content='messages', content_rowid='rowid',
            tokenize='trigram'
        );
        """;

    public const string TrigramRebuildSql = "INSERT INTO messages_trgm(messages_trgm) VALUES('rebuild');";

    /// <summary>不带 trigram 的触发器（只维护 messages_fts）。</summary>
    public const string TriggersWithoutTrigramSql = """
        DROP TRIGGER IF EXISTS messages_ai;
        DROP TRIGGER IF EXISTS messages_ad;
        DROP TRIGGER IF EXISTS messages_au;

        CREATE TRIGGER messages_ai AFTER INSERT ON messages BEGIN
            INSERT INTO messages_fts(rowid, Subject, FromName, FromAddress, Preview, BodyText)
            VALUES (new.rowid, new.Subject, new.FromName, new.FromAddress, new.Preview, new.BodyText);
        END;

        CREATE TRIGGER messages_ad AFTER DELETE ON messages BEGIN
            INSERT INTO messages_fts(messages_fts, rowid, Subject, FromName, FromAddress, Preview, BodyText)
            VALUES ('delete', old.rowid, old.Subject, old.FromName, old.FromAddress, old.Preview, old.BodyText);
        END;

        CREATE TRIGGER messages_au AFTER UPDATE OF Subject, FromName, FromAddress, Preview, BodyText ON messages
        WHEN old.Subject IS NOT new.Subject OR old.FromName IS NOT new.FromName OR old.FromAddress IS NOT new.FromAddress
             OR old.Preview IS NOT new.Preview OR old.BodyText IS NOT new.BodyText
        BEGIN
            INSERT INTO messages_fts(messages_fts, rowid, Subject, FromName, FromAddress, Preview, BodyText)
            VALUES ('delete', old.rowid, old.Subject, old.FromName, old.FromAddress, old.Preview, old.BodyText);
            INSERT INTO messages_fts(rowid, Subject, FromName, FromAddress, Preview, BodyText)
            VALUES (new.rowid, new.Subject, new.FromName, new.FromAddress, new.Preview, new.BodyText);
        END;
        """;

    /// <summary>同时维护 messages_fts 与 messages_trgm 的触发器。</summary>
    public const string TriggersWithTrigramSql = """
        DROP TRIGGER IF EXISTS messages_ai;
        DROP TRIGGER IF EXISTS messages_ad;
        DROP TRIGGER IF EXISTS messages_au;

        CREATE TRIGGER messages_ai AFTER INSERT ON messages BEGIN
            INSERT INTO messages_fts(rowid, Subject, FromName, FromAddress, Preview, BodyText)
            VALUES (new.rowid, new.Subject, new.FromName, new.FromAddress, new.Preview, new.BodyText);
            INSERT INTO messages_trgm(rowid, Subject, FromName, FromAddress, Preview, BodyText)
            VALUES (new.rowid, new.Subject, new.FromName, new.FromAddress, new.Preview, new.BodyText);
        END;

        CREATE TRIGGER messages_ad AFTER DELETE ON messages BEGIN
            INSERT INTO messages_fts(messages_fts, rowid, Subject, FromName, FromAddress, Preview, BodyText)
            VALUES ('delete', old.rowid, old.Subject, old.FromName, old.FromAddress, old.Preview, old.BodyText);
            INSERT INTO messages_trgm(messages_trgm, rowid, Subject, FromName, FromAddress, Preview, BodyText)
            VALUES ('delete', old.rowid, old.Subject, old.FromName, old.FromAddress, old.Preview, old.BodyText);
        END;

        CREATE TRIGGER messages_au AFTER UPDATE OF Subject, FromName, FromAddress, Preview, BodyText ON messages
        WHEN old.Subject IS NOT new.Subject OR old.FromName IS NOT new.FromName OR old.FromAddress IS NOT new.FromAddress
             OR old.Preview IS NOT new.Preview OR old.BodyText IS NOT new.BodyText
        BEGIN
            INSERT INTO messages_fts(messages_fts, rowid, Subject, FromName, FromAddress, Preview, BodyText)
            VALUES ('delete', old.rowid, old.Subject, old.FromName, old.FromAddress, old.Preview, old.BodyText);
            INSERT INTO messages_fts(rowid, Subject, FromName, FromAddress, Preview, BodyText)
            VALUES (new.rowid, new.Subject, new.FromName, new.FromAddress, new.Preview, new.BodyText);
            INSERT INTO messages_trgm(messages_trgm, rowid, Subject, FromName, FromAddress, Preview, BodyText)
            VALUES ('delete', old.rowid, old.Subject, old.FromName, old.FromAddress, old.Preview, old.BodyText);
            INSERT INTO messages_trgm(rowid, Subject, FromName, FromAddress, Preview, BodyText)
            VALUES (new.rowid, new.Subject, new.FromName, new.FromAddress, new.Preview, new.BodyText);
        END;
        """;
}
