using System.Globalization;
using MailClient.Core;
using MailClient.Core.Sending;
using MailClient.Providers.Setup;

namespace MailClient.Providers.WriteBack;

public sealed class ImapWriteGateway : IMessageWriteGateway
{
    private readonly ProviderHandle _handle;
    private readonly GenericImapProvider _imap;

    public ImapWriteGateway(ProviderHandle handle)
    {
        _handle = handle;
        _imap = handle.Imap ?? throw new ArgumentException("缺少 IMAP 提供商。", nameof(handle));
    }

    private static long Uid(RemoteMessage m)
        => long.TryParse(m.Key, NumberStyles.Integer, CultureInfo.InvariantCulture, out var uid)
            ? uid
            : throw new ClassifiedFailureException(FailureKind.NotFound, "缺少邮件的服务器 UID。");

    public Task SetReadAsync(RemoteMessage message, bool read, CancellationToken cancellationToken)
        => _imap.SetFlagsAsync(message.Folder.Path, Uid(message), message.Folder.UidValidity, seen: read, flagged: null, cancellationToken);

    public Task SetFlaggedAsync(RemoteMessage message, bool flagged, CancellationToken cancellationToken)
        => _imap.SetFlagsAsync(message.Folder.Path, Uid(message), message.Folder.UidValidity, seen: null, flagged: flagged, cancellationToken);

    public Task<long?> MoveAsync(RemoteMessage message, RemoteFolder destination, CancellationToken cancellationToken)
        => _imap.MoveMessageAsync(message.Folder.Path, Uid(message), message.Folder.UidValidity, destination.Path, cancellationToken);

    public Task DeleteAsync(RemoteMessage message, CancellationToken cancellationToken)
        => _imap.DeleteMessageAsync(message.Folder.Path, Uid(message), message.Folder.UidValidity, cancellationToken);

    public ValueTask DisposeAsync() => _handle.DisposeAsync();
}

public sealed class GraphWriteGateway : IMessageWriteGateway
{
    private readonly ProviderHandle _handle;
    private readonly MicrosoftGraphProvider _graph;

    public GraphWriteGateway(ProviderHandle handle)
    {
        _handle = handle;
        _graph = handle.Graph ?? throw new ArgumentException("缺少 Graph 提供商。", nameof(handle));
    }

    public Task SetReadAsync(RemoteMessage message, bool read, CancellationToken cancellationToken)
        => _graph.SetReadAsync(message.Key, read, cancellationToken);

    public Task SetFlaggedAsync(RemoteMessage message, bool flagged, CancellationToken cancellationToken)
        => _graph.SetFlaggedAsync(message.Key, flagged, cancellationToken);

    public async Task<long?> MoveAsync(RemoteMessage message, RemoteFolder destination, CancellationToken cancellationToken)
    {
        await _graph.MoveMessageAsync(message.Key, destination.Path, cancellationToken).ConfigureAwait(false);
        return null; // Graph 移动后消息 id 会变，本地稍后靠同步对齐
    }

    public Task DeleteAsync(RemoteMessage message, CancellationToken cancellationToken)
        => _graph.DeleteMessageAsync(message.Key, cancellationToken);

    public ValueTask DisposeAsync() => _handle.DisposeAsync();
}

public sealed class WriteGatewayFactory : IWriteGatewayFactory
{
    private readonly AccountProviderFactory _providers;

    public WriteGatewayFactory(AccountProviderFactory providers) => _providers = providers;

    public async Task<IMessageWriteGateway> CreateAsync(AccountEntity account, CancellationToken cancellationToken)
    {
        var handle = await _providers.OpenAsync(account, cancellationToken).ConfigureAwait(false);
        return handle.Graph is not null ? new GraphWriteGateway(handle) : new ImapWriteGateway(handle);
    }
}
