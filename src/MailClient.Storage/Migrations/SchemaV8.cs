namespace MailClient.Storage.Migrations;

/// <summary>
/// V8：记录正文缓存的解析器版本（BodyParserVersion），用于未来升级解码/重写逻辑时按需自愈重拉。
/// 已有缓存默认回填为 1（与当前版本一致），避免升级时对正常邮件产生无意义的全量重拉；
/// 乱码与 cid 残留仍由 BodyCacheHealth 精准识别并触发单次自愈。
/// </summary>
internal static class SchemaV8
{
    public const int Version = 8;

    public const string Sql = """
        ALTER TABLE messages ADD COLUMN BodyParserVersion INTEGER NOT NULL DEFAULT 1;
        """;
}
