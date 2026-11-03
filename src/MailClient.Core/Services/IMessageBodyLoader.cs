namespace MailClient.Core.Services;

/// <summary>按需拉取并缓存邮件正文（IMAP/Graph）；已缓存则立即返回。</summary>
public interface IMessageBodyLoader
{
    /// <summary>确保本地已有完整正文；成功返回。本地草稿 / 已缓存会直接返回。</summary>
    Task EnsureLoadedAsync(string messageId, CancellationToken cancellationToken = default);

    /// <summary>把附件下载到 <paramref name="destinationPath"/>（覆盖已有文件）。</summary>
    Task DownloadAttachmentAsync(string attachmentId, string destinationPath, CancellationToken cancellationToken = default);
}
