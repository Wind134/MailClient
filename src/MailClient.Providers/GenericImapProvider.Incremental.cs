using MailClient.Core;
using MailClient.Core.Sync;
using MailClient.Providers.Imap;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Search;

namespace MailClient.Providers;

/// <summary>双向增量同步所需的 IMAP 操作（<see cref="IImapSyncSource"/>）：CONDSTORE / QRESYNC / 窗口对账。</summary>
public sealed partial class GenericImapProvider : IImapSyncSource
{
    private bool _quickResyncPending;

    /// <summary>
    /// 登录后启用 QRESYNC（RFC 7162）。必须在打开任何文件夹之前；启用后 MailKit 用 VANISHED 取代 EXPUNGE 通知。
    /// 服务器不支持（QQ / 163 多半不支持）或失败时静默跳过，同步引擎会按能力降级。
    /// </summary>
    private async Task EnableQuickResyncAsync(ImapClient client, CancellationToken cancellationToken)
    {
        _quickResyncPending = false;
        if (!client.Capabilities.HasFlag(ImapCapabilities.QuickResync))
        {
            return;
        }

        try
        {
            await client.EnableQuickResyncAsync(cancellationToken).ConfigureAwait(false);
            _quickResyncPending = true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // best effort：退回 CONDSTORE
        }
    }

    public async Task<ImapServerCaps> GetCapabilitiesAsync(CancellationToken ct)
    {
        await EnsureConnectedAsync(ct).ConfigureAwait(false);
        var caps = (_imap ?? throw new InvalidOperationException("IMAP 未连接。")).Capabilities;
        return new ImapServerCaps(
            CondStore: caps.HasFlag(ImapCapabilities.CondStore) || caps.HasFlag(ImapCapabilities.QuickResync),
            QResync: _quickResyncEnabled,
            Idle: caps.HasFlag(ImapCapabilities.Idle),
            GmailExt: caps.HasFlag(ImapCapabilities.GMailExt1));
    }

    public async Task<ImapFolderOpen> SelectAsync(string remotePath, CancellationToken ct)
    {
        await EnsureConnectedAsync(ct).ConfigureAwait(false);
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var client = _imap ?? throw new InvalidOperationException("IMAP 未连接。");
            var folder = await GetFolderObjectAsync(client, remotePath, ct).ConfigureAwait(false);
            if (folder.IsOpen)
            {
                // 重新 SELECT 才能拿到最新的 UIDNEXT / HIGHESTMODSEQ / EXISTS
                await folder.CloseAsync(false, ct).ConfigureAwait(false);
            }

            await folder.OpenAsync(FolderAccess.ReadOnly, ct).ConfigureAwait(false);
            return ToOpen(folder);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<ImapQResyncOpen> SelectQResyncAsync(
        string remotePath, long uidValidity, long highestModSeq, IReadOnlyList<long> knownUids, CancellationToken ct)
    {
        await EnsureConnectedAsync(ct).ConfigureAwait(false);
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var client = _imap ?? throw new InvalidOperationException("IMAP 未连接。");
            if (!_quickResyncEnabled)
            {
                throw new NotSupportedException("连接上没有启用 QRESYNC。");
            }

            var folder = await GetFolderObjectAsync(client, remotePath, ct).ConfigureAwait(false);
            if (folder.IsOpen)
            {
                await folder.CloseAsync(false, ct).ConfigureAwait(false);
            }

            var flags = new List<ServerFlagState>();
            var vanished = new List<long>();
            void OnFlags(object? sender, MessageFlagsChangedEventArgs e)
            {
                if (e.UniqueId is { } uid)
                {
                    flags.Add(new ServerFlagState(uid.Id, e.Flags.HasFlag(MessageFlags.Seen), e.Flags.HasFlag(MessageFlags.Flagged), e.ModSeq ?? 0));
                }
            }

            void OnVanished(object? sender, MessagesVanishedEventArgs e)
                => vanished.AddRange(e.UniqueIds.Select(u => (long)u.Id));

            folder.MessageFlagsChanged += OnFlags;
            folder.MessagesVanished += OnVanished;
            try
            {
                var known = knownUids.Select(u => new UniqueId((uint)u)).ToList();
                await folder.OpenAsync(FolderAccess.ReadOnly, (uint)uidValidity, (ulong)highestModSeq, known, ct).ConfigureAwait(false);
            }
            finally
            {
                folder.MessageFlagsChanged -= OnFlags;
                folder.MessagesVanished -= OnVanished;
            }

            return new ImapQResyncOpen(ToOpen(folder), flags, vanished);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<ServerFlagState>> FetchFlagsChangedSinceAsync(string remotePath, long modSeq, CancellationToken ct)
    {
        await EnsureConnectedAsync(ct).ConfigureAwait(false);
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var client = _imap ?? throw new InvalidOperationException("IMAP 未连接。");
            var folder = await OpenFolderAsync(client, remotePath, ct).ConfigureAwait(false);
            var request = new FetchRequest(MessageSummaryItems.UniqueId | MessageSummaryItems.Flags | MessageSummaryItems.ModSeq)
            {
                ChangedSince = (ulong)modSeq
            };
            var summaries = await folder.FetchAsync(UniqueIdRange.All, request, ct).ConfigureAwait(false);
            return ToFlagStates(summaries);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<ServerFlagState>> FetchUidFlagsAsync(string remotePath, long minUid, CancellationToken ct)
    {
        await EnsureConnectedAsync(ct).ConfigureAwait(false);
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var client = _imap ?? throw new InvalidOperationException("IMAP 未连接。");
            var folder = await OpenFolderAsync(client, remotePath, ct).ConfigureAwait(false);
            var range = new UniqueIdRange(new UniqueId((uint)minUid), UniqueId.MaxValue);
            var summaries = await folder.FetchAsync(range, MessageSummaryItems.UniqueId | MessageSummaryItems.Flags, ct).ConfigureAwait(false);
            return ToFlagStates(summaries);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<long>> SearchUidsAsync(string remotePath, long minUid, CancellationToken ct)
    {
        await EnsureConnectedAsync(ct).ConfigureAwait(false);
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var client = _imap ?? throw new InvalidOperationException("IMAP 未连接。");
            var folder = await OpenFolderAsync(client, remotePath, ct).ConfigureAwait(false);
            var range = new UniqueIdRange(new UniqueId((uint)minUid), UniqueId.MaxValue);
            var uids = await folder.SearchAsync(SearchQuery.Uids(range), ct).ConfigureAwait(false);
            return uids.Select(u => (long)u.Id).ToList();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<MailMessageSyncItem>> FetchNewAsync(string remotePath, long? afterUid, int limit, CancellationToken ct)
        => await SyncFolderDetailedAsync(remotePath, limit, afterUid, ct).ConfigureAwait(false);

    private static IReadOnlyList<ServerFlagState> ToFlagStates(IList<IMessageSummary> summaries)
        => summaries
            .Where(s => s.Flags is not null)
            .Select(s => new ServerFlagState(
                s.UniqueId.Id,
                s.Flags!.Value.HasFlag(MessageFlags.Seen),
                s.Flags!.Value.HasFlag(MessageFlags.Flagged),
                s.ModSeq ?? 0))
            .ToList();

    private static ImapFolderOpen ToOpen(IMailFolder folder)
        => new(folder.UidValidity, folder.UidNext?.Id ?? 0, (long)folder.HighestModSeq, folder.Count);

    private static async Task<IMailFolder> GetFolderObjectAsync(ImapClient client, string path, CancellationToken ct)
        => string.IsNullOrWhiteSpace(path) || string.Equals(path, "INBOX", StringComparison.OrdinalIgnoreCase)
            ? client.Inbox
            : await client.GetFolderAsync(path, ct).ConfigureAwait(false);
}
