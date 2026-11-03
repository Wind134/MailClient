namespace MailClient.Core;

/// <summary>从服务器取回的正文与附件元数据（尚未落库的中间态）。</summary>
public sealed record MessageBodyContent(
    string? BodyText,
    string? BodyHtml,
    IReadOnlyList<MessageAttachmentInfo> Attachments);

/// <summary>附件元数据（不含本地路径；下载后再写入 LocalPath）。
/// ContentBytes 仅在拉取正文时短暂带上，用于把 cid: 内嵌图改写成 data URI；不会写入 SQLite。</summary>
public sealed record MessageAttachmentInfo(
    string FileName,
    string? ContentType,
    long SizeBytes,
    string? ContentId,
    bool IsInline,
    string? RemoteId = null,
    byte[]? ContentBytes = null);
