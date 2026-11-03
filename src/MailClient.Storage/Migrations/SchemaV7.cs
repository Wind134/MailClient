namespace MailClient.Storage.Migrations;

/// <summary>
/// V7：正文缓存的“最后访问时间”（BodyAccessedAt），用于按“最久没打开”清理已缓存的正文。
/// 已有缓存的正文回填成迁移时刻——升级后至少还有一个完整的保留周期，不会一升级就被清掉。
/// 打开 / 拉取正文时更新；NULL 表示没有缓存正文（清理时按收信时间兜底）。
/// </summary>
internal static class SchemaV7
{
    public const int Version = 7;

    public const string Sql = """
        ALTER TABLE messages ADD COLUMN BodyAccessedAt TEXT NULL;

        CREATE INDEX IF NOT EXISTS ix_messages_body_cache ON messages(BodyAccessedAt) WHERE BodyFetched = 1;

        UPDATE messages
        SET BodyAccessedAt = strftime('%Y-%m-%dT%H:%M:%SZ', 'now')
        WHERE BodyFetched = 1 AND BodyAccessedAt IS NULL;
        """;
}
