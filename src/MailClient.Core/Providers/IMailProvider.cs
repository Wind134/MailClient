namespace MailClient.Core.Providers;

/// <summary>邮箱提供商统一入口：同步、发送、文件夹映射。</summary>
public interface IMailProvider
{
    string ProviderId { get; }
    Task ConnectAsync(CancellationToken cancellationToken = default);
    Task DisconnectAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<MailFolder>> GetFoldersAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<MailMessageSummary>> SyncFolderAsync(string folderId, CancellationToken cancellationToken = default);
    Task SendAsync(OutgoingMailMessage message, CancellationToken cancellationToken = default);
}
