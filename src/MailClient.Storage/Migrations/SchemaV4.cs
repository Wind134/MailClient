namespace MailClient.Storage.Migrations;

/// <summary>
/// V4：双向同步（把服务器上的变更拉回本地）。
/// folders：UidNext / HighestModSeq（IMAP CONDSTORE / QRESYNC 的同步基线）、LastReconcileAt（上次完整对账时间，用来节流）。
/// messages：GmMsgId / GmThrId（Gmail X-GM-MSGID / X-GM-THRID，用来识别“同一封邮件在不同标签里的副本”）；
/// ReadChangedAt / FlagChangedAt / LocationChangedAt（本地各字段最近一次改动 / 写回确认的时间，防止旧快照把值翻回去）。
/// （folders.UidValidity 在 V1 已存在，这里不重复添加。）
/// </summary>
internal static class SchemaV4
{
    public const int Version = 4;

    public const string Sql = """
        ALTER TABLE folders ADD COLUMN UidNext INTEGER NULL;
        ALTER TABLE folders ADD COLUMN HighestModSeq INTEGER NULL;
        ALTER TABLE folders ADD COLUMN LastReconcileAt TEXT NULL;

        ALTER TABLE messages ADD COLUMN GmMsgId TEXT NULL;
        ALTER TABLE messages ADD COLUMN GmThrId TEXT NULL;
        ALTER TABLE messages ADD COLUMN ReadChangedAt TEXT NULL;
        ALTER TABLE messages ADD COLUMN FlagChangedAt TEXT NULL;
        ALTER TABLE messages ADD COLUMN LocationChangedAt TEXT NULL;

        CREATE INDEX IF NOT EXISTS ix_messages_gmmsgid ON messages(AccountId, GmMsgId) WHERE GmMsgId IS NOT NULL;
        """;
}
