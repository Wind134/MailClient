namespace MailClient.Storage.Migrations;

/// <summary>
/// V6：标记版本。启动时会用改进后的 HTML→预览逻辑重算被 CSS 污染的 Preview（不改 BodyHtml）。
/// 演示账号清理在应用层完成，不在此 SQL 里删用户数据。
/// </summary>
internal static class SchemaV6
{
    public const int Version = 6;

    public const string Sql = """
        -- 1.0.8：预览 CSS 修复标记位；实际重算见 MailDatabase.RepairBadPreviewsAsync
        SELECT 1;
        """;
}
