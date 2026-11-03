using MailClient.Core;

namespace MailClient.Providers.Outbox;

public enum SentLookup
{
    /// <summary>服务器“已发送”里能找到这封邮件（已经发出）。</summary>
    Found,

    /// <summary>找不到。</summary>
    NotFound,

    /// <summary>无法判断（没有已发送文件夹、查询失败……）。</summary>
    Unknown
}

/// <summary>SMTP 在 DATA 阶段（邮件正文传输中）发生了网络类错误：邮件可能已经投递，也可能没有。</summary>
public sealed class SendOutcomeUnknownException : Exception
{
    public SendOutcomeUnknownException(Exception inner)
        : base("发送过程中连接中断，无法确认服务器是否已收到邮件。", inner)
    {
    }
}

/// <summary>一个账号的“发信通道”：SMTP（IMAP / Gmail）或 Graph sendMail。每次投递创建一个，用完释放。</summary>
public interface IOutgoingTransport : IAsyncDisposable
{
    /// <summary>
    /// 发件箱用 Message-ID 在服务器上查重时，“找不到”是否可信：
    /// Gmail / Graph 服务器会保存已发送邮件，找不到就说明没发出，可以放心重发；普通 IMAP（QQ/163…）服务器不会保存，找不到不代表没发。
    /// </summary>
    bool SentLookupIsAuthoritative { get; }

    Task SendAsync(OutgoingMailMessage message, string fromAddress, string? fromName, DateTimeOffset date, CancellationToken cancellationToken);

    /// <summary>把副本追加到服务器“已发送”文件夹（仅 IMAP）。返回新邮件的 UID（服务器不返回则为 null）。</summary>
    Task<long?> AppendToSentAsync(
        OutgoingMailMessage message, string fromAddress, string? fromName, DateTimeOffset date, string? sentFolderPath, CancellationToken cancellationToken);

    Task<SentLookup> FindInSentAsync(
        OutgoingMailMessage message, DateTimeOffset notBefore, string? sentFolderPath, CancellationToken cancellationToken);
}

public interface IOutgoingTransportFactory
{
    Task<IOutgoingTransport> CreateAsync(AccountEntity account, CancellationToken cancellationToken);

    /// <summary>发送成功后，是否需要再把副本 APPEND 到服务器的已发送文件夹。</summary>
    bool ShouldAppendSentCopy(AccountEntity account);
}
