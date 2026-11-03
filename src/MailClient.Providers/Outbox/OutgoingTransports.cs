using MailClient.Core;
using MailClient.Providers.Setup;

namespace MailClient.Providers.Outbox;

/// <summary>IMAP / Gmail 账号：SMTP 发信 + IMAP APPEND 已发送 + 按 Message-ID 查重。</summary>
public sealed class ImapOutgoingTransport : IOutgoingTransport
{
    private readonly ProviderHandle _handle;
    private readonly GenericImapProvider _imap;

    public ImapOutgoingTransport(ProviderHandle handle, bool sentLookupIsAuthoritative)
    {
        _handle = handle;
        _imap = handle.Imap ?? throw new ArgumentException("缺少 IMAP 提供商。", nameof(handle));
        SentLookupIsAuthoritative = sentLookupIsAuthoritative;
    }

    public bool SentLookupIsAuthoritative { get; }

    public async Task SendAsync(OutgoingMailMessage message, string fromAddress, string? fromName, DateTimeOffset date, CancellationToken cancellationToken)
    {
        using var mime = OutgoingMimeBuilder.Build(message, fromAddress, fromName, date);
        await _imap.SendMimeAsync(mime, cancellationToken).ConfigureAwait(false);
    }

    public async Task<long?> AppendToSentAsync(OutgoingMailMessage message, string fromAddress, string? fromName, DateTimeOffset date, string? sentFolderPath, CancellationToken cancellationToken)
    {
        using var mime = OutgoingMimeBuilder.Build(message, fromAddress, fromName, date);
        return await _imap.AppendToSentAsync(mime, sentFolderPath, date, cancellationToken).ConfigureAwait(false);
    }

    public Task<SentLookup> FindInSentAsync(OutgoingMailMessage message, DateTimeOffset notBefore, string? sentFolderPath, CancellationToken cancellationToken)
        => message.MessageId is null
            ? Task.FromResult(SentLookup.Unknown)
            : _imap.FindInSentByMessageIdAsync(message.MessageId, sentFolderPath, cancellationToken);

    public ValueTask DisposeAsync() => _handle.DisposeAsync();
}

/// <summary>Outlook / Microsoft 365：Graph sendMail（saveToSentItems=true，服务器自己保存已发送，所以没有 APPEND）。</summary>
public sealed class GraphOutgoingTransport : IOutgoingTransport
{
    private readonly ProviderHandle _handle;
    private readonly MicrosoftGraphProvider _graph;

    public GraphOutgoingTransport(ProviderHandle handle)
    {
        _handle = handle;
        _graph = handle.Graph ?? throw new ArgumentException("缺少 Graph 提供商。", nameof(handle));
    }

    public bool SentLookupIsAuthoritative => true;

    public Task SendAsync(OutgoingMailMessage message, string fromAddress, string? fromName, DateTimeOffset date, CancellationToken cancellationToken)
        => _graph.SendInBackgroundAsync(message, cancellationToken);

    public Task<long?> AppendToSentAsync(OutgoingMailMessage message, string fromAddress, string? fromName, DateTimeOffset date, string? sentFolderPath, CancellationToken cancellationToken)
        => Task.FromResult<long?>(null);

    public async Task<SentLookup> FindInSentAsync(OutgoingMailMessage message, DateTimeOffset notBefore, string? sentFolderPath, CancellationToken cancellationToken)
        => await _graph.SentItemExistsAsync(message.Subject, notBefore, cancellationToken).ConfigureAwait(false)
            ? SentLookup.Found
            : SentLookup.NotFound;

    public ValueTask DisposeAsync() => _handle.DisposeAsync();
}

/// <summary>真实的发信通道工厂。</summary>
public sealed class OutgoingTransportFactory : IOutgoingTransportFactory
{
    private readonly AccountProviderFactory _providers;

    public OutgoingTransportFactory(AccountProviderFactory providers) => _providers = providers;

    public async Task<IOutgoingTransport> CreateAsync(AccountEntity account, CancellationToken cancellationToken)
    {
        var handle = await _providers.OpenAsync(account, cancellationToken).ConfigureAwait(false);
        if (handle.Graph is not null)
        {
            return new GraphOutgoingTransport(handle);
        }

        // Gmail 服务器会保存已发送邮件并保留客户端的 Message-ID，查不到就是没发出；普通 IMAP 服务器不会保存，查不到不可信
        return new ImapOutgoingTransport(handle, sentLookupIsAuthoritative: account.ProviderId == SentCopyPolicy.GmailProviderId);
    }

    public bool ShouldAppendSentCopy(AccountEntity account)
    {
        var kind = ProviderDetector.Detect(account.EmailAddress)?.Kind ?? SetupProviderKind.OtherImap;
        return SentCopyPolicy.ShouldAppend(account.ProviderId, kind, _providers.Settings.GetSentCopyOverride(SentCopyPolicy.PresetKey(kind)));
    }
}
