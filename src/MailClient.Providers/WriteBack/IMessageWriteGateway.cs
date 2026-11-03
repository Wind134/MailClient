using MailClient.Core;

namespace MailClient.Providers.WriteBack;

/// <summary>服务器上的文件夹：IMAP = 路径（+ UIDVALIDITY 用于校验）；Graph = 文件夹 id。</summary>
public sealed record RemoteFolder(string Path, long? UidValidity);

/// <summary>服务器上的邮件：IMAP 的 Key = UID；Graph 的 Key = 消息 id。</summary>
public sealed record RemoteMessage(RemoteFolder Folder, string Key);

/// <summary>把本地的已读 / 星标 / 移动 / 删除写回服务器。每个账号一个实例（worker 内用完释放）。</summary>
public interface IMessageWriteGateway : IAsyncDisposable
{
    Task SetReadAsync(RemoteMessage message, bool read, CancellationToken cancellationToken);

    Task SetFlaggedAsync(RemoteMessage message, bool flagged, CancellationToken cancellationToken);

    /// <summary>移动邮件，返回在目标文件夹里的新 UID（IMAP 且服务器告知时）；Graph / 未知返回 null。</summary>
    Task<long?> MoveAsync(RemoteMessage message, RemoteFolder destination, CancellationToken cancellationToken);

    /// <summary>永久删除（调用方已决定：邮件在已删除 / 垃圾箱里，或没有垃圾箱）。</summary>
    Task DeleteAsync(RemoteMessage message, CancellationToken cancellationToken);
}

public interface IWriteGatewayFactory
{
    Task<IMessageWriteGateway> CreateAsync(AccountEntity account, CancellationToken cancellationToken);
}
