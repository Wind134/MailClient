namespace MailClient.Storage.Migrations;

/// <summary>
/// V5：正文懒加载缓存标记 + 附件远端 Id。
/// BodyFetched=1 表示已经向服务器取过完整正文（即使正文为空也不再重复拉取）。
/// attachments.RemoteId：Graph 附件 Id；IMAP 可空（下载时按文件名在 MIME 里匹配）。
/// </summary>
internal static class SchemaV5
{
    public const int Version = 5;

    public const string Sql = """
        ALTER TABLE messages ADD COLUMN BodyFetched INTEGER NOT NULL DEFAULT 0;
        ALTER TABLE attachments ADD COLUMN RemoteId TEXT NULL;

        -- 演示数据 / 旧库里已经有正文的，视为已缓存
        UPDATE messages SET BodyFetched = 1
        WHERE BodyText IS NOT NULL OR BodyHtml IS NOT NULL;
        """;
}
