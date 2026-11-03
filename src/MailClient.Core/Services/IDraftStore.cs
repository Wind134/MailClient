namespace MailClient.Core.Services;

/// <summary>
/// 写信窗口里正在编辑的草稿的完整状态：账号、收件人、主题、正文、附件、回复上下文。
/// 附件路径在保存后指向应用自己的草稿附件目录（见 <see cref="MailClient.Core.Sending.DraftAttachmentStore"/>）。
/// </summary>
public sealed record DraftContent(
    string Id,
    Guid AccountId,
    string To,
    string Cc,
    string Bcc,
    string Subject,
    string Body,
    string Mode = "New",
    string? InReplyTo = null,
    IReadOnlyList<string>? References = null,
    string? QuotedHtml = null,
    string? QuotedText = null,
    IReadOnlyList<string>? Attachments = null,
    string? SourceMessageId = null,
    DateTimeOffset? UpdatedAt = null);

/// <summary>
/// 草稿的本地自动保存（保存到该账号的“草稿”文件夹，发送 / 丢弃 / 从列表删除时清理）。
/// <see cref="DeleteAsync"/> 删除整个草稿：本地行、附件副本，以及它由之而来的服务器草稿副本（若有）。
/// 服务器草稿的“推送 / 同步”（IMAP APPEND / Graph 创建草稿）暂未实现。
/// </summary>
public interface IDraftStore
{
    Task SaveAsync(DraftContent draft, CancellationToken cancellationToken = default);

    Task DeleteAsync(string draftId, CancellationToken cancellationToken = default);

    Task<DraftContent?> GetAsync(string draftId, CancellationToken cancellationToken = default);
}
