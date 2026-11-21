namespace MailClient.Core;

/// <summary>邮件 Id 的构造规则（本地草稿用固定前缀与服务器消息区分）。</summary>
public static class MailIds
{
    /// <summary>本地草稿的消息 Id 前缀，形如 <c>draft:{草稿Id}</c>。</summary>
    public const string DraftMessagePrefix = "draft:";
}
