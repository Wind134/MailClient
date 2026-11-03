using MailClient.Core;
using MailClient.Core.Services;
using MailClient.Core.Sync;
using MailClient.Providers.Graph;
using MailClient.Providers.Imap;
using MailClient.Storage;

namespace MailClient.Tests;

/// <summary>内存里的假 IMAP 文件夹：可以改标志、删邮件、换 UIDVALIDITY，并记录引擎发了哪些请求。没有任何网络。</summary>
internal sealed class FakeImapSource : IImapSyncSource
{
    public sealed class FakeMsg
    {
        public bool Read { get; set; }
        public bool Flagged { get; set; }
        public ulong ModSeq { get; set; }
        public string? GmMsgId { get; set; }
        public DateTimeOffset Received { get; set; } = SendFixture.T0;
    }

    private readonly string _folderId;

    public FakeImapSource(string folderId, ImapServerCaps? caps = null)
    {
        _folderId = folderId;
        Caps = caps ?? ImapServerCaps.None;
    }

    public ImapServerCaps Caps { get; set; }
    public long UidValidity { get; set; } = 777;
    public long HighestModSeq { get; set; }
    public long NextUid { get; set; } = 1;
    public Dictionary<long, FakeMsg> Messages { get; } = new();
    public List<string> Calls { get; } = new();
    public bool QResyncThrows { get; set; }
    public bool ForceEmptyListing { get; set; }
    private readonly List<(long Uid, long ModSeq)> _expunged = new();

    private ulong Bump() => (ulong)(++HighestModSeq);

    public FakeMsg Add(long uid, bool read = false, bool flagged = false, string? gmMsgId = null, DateTimeOffset? received = null)
    {
        var m = new FakeMsg { Read = read, Flagged = flagged, GmMsgId = gmMsgId, Received = received ?? SendFixture.T0 };
        if (Caps.CondStore)
        {
            m.ModSeq = Bump();
        }

        Messages[uid] = m;
        NextUid = Math.Max(NextUid, uid + 1);
        return m;
    }

    public void SetFlags(long uid, bool read, bool flagged)
    {
        var m = Messages[uid];
        m.Read = read;
        m.Flagged = flagged;
        if (Caps.CondStore)
        {
            m.ModSeq = Bump();
        }
    }

    /// <summary>服务器上删除 / 移走。<paramref name="bumpModSeq"/> 为 false 模拟“EXPUNGE 不让 MODSEQ 前进”的服务器。</summary>
    public void Expunge(long uid, bool bumpModSeq = true)
    {
        Messages.Remove(uid);
        var seq = Caps.CondStore && bumpModSeq ? (long)Bump() : HighestModSeq;
        _expunged.Add((uid, seq));
    }

    private ImapFolderOpen Open() => new(UidValidity, NextUid, Caps.CondStore ? HighestModSeq : 0, Messages.Count);

    public Task<ImapServerCaps> GetCapabilitiesAsync(CancellationToken ct) => Task.FromResult(Caps);

    public Task<ImapFolderOpen> SelectAsync(string remotePath, CancellationToken ct)
    {
        Calls.Add("select");
        return Task.FromResult(Open());
    }

    public Task<ImapQResyncOpen> SelectQResyncAsync(string remotePath, long uidValidity, long highestModSeq, IReadOnlyList<long> knownUids, CancellationToken ct)
    {
        Calls.Add($"qresync:{uidValidity}:{highestModSeq}");
        if (QResyncThrows)
        {
            throw new InvalidOperationException("QRESYNC not accepted");
        }

        var flags = Messages.Where(kv => (long)kv.Value.ModSeq > highestModSeq)
            .Select(kv => new ServerFlagState(kv.Key, kv.Value.Read, kv.Value.Flagged, kv.Value.ModSeq)).ToList();
        var vanished = knownUids.Where(u => !Messages.ContainsKey(u)).ToList();
        return Task.FromResult(new ImapQResyncOpen(Open(), flags, vanished));
    }

    public Task<IReadOnlyList<ServerFlagState>> FetchFlagsChangedSinceAsync(string remotePath, long modSeq, CancellationToken ct)
    {
        Calls.Add($"changedsince:{modSeq}");
        IReadOnlyList<ServerFlagState> list = Messages.Where(kv => (long)kv.Value.ModSeq > modSeq)
            .Select(kv => new ServerFlagState(kv.Key, kv.Value.Read, kv.Value.Flagged, kv.Value.ModSeq)).ToList();
        return Task.FromResult(list);
    }

    public Task<IReadOnlyList<ServerFlagState>> FetchUidFlagsAsync(string remotePath, long minUid, CancellationToken ct)
    {
        Calls.Add($"uidflags:{minUid}");
        IReadOnlyList<ServerFlagState> list = ForceEmptyListing
            ? []
            : Messages.Where(kv => kv.Key >= minUid).Select(kv => new ServerFlagState(kv.Key, kv.Value.Read, kv.Value.Flagged)).ToList();
        return Task.FromResult(list);
    }

    public Task<IReadOnlyList<long>> SearchUidsAsync(string remotePath, long minUid, CancellationToken ct)
    {
        Calls.Add($"search:{minUid}");
        IReadOnlyList<long> list = ForceEmptyListing ? [] : Messages.Keys.Where(u => u >= minUid).OrderBy(u => u).ToList();
        return Task.FromResult(list);
    }

    public Task<IReadOnlyList<MailMessageSyncItem>> FetchNewAsync(string remotePath, long? afterUid, int limit, CancellationToken ct)
    {
        Calls.Add($"fetchnew:{afterUid?.ToString() ?? "-"}");
        IReadOnlyList<MailMessageSyncItem> list = Messages
            .Where(kv => kv.Key > (afterUid ?? 0))
            .OrderByDescending(kv => kv.Key).Take(limit)
            .Select(kv =>
            {
                var summary = new MailMessageSummary(
                    $"{_folderId}:{kv.Key}", _folderId, $"主题 {kv.Key}", "alice@example.com", kv.Value.Received,
                    kv.Value.Read, kv.Value.Flagged, "预览");
                return new MailMessageSyncItem(summary, kv.Key, $"m{kv.Key}@remote.example", "alice@example.com", "Alice", false, 100, false, kv.Value.Received,
                    GmMsgId: kv.Value.GmMsgId, GmThrId: kv.Value.GmMsgId is null ? null : "T" + kv.Value.GmMsgId);
            })
            .ToList();
        return Task.FromResult(list);
    }
}

/// <summary>一个账号 + 收件箱的引擎测试夹具。时钟可手动拨动。</summary>
internal sealed class ImapEngineFixture : IAsyncDisposable
{
    private ImapEngineFixture(SendFixture fx, SyncOptions options)
    {
        Fx = fx;
        Options = options;
        Log = new MemorySyncLog();
        Now = SendFixture.T0;
        Engine = new ImapFolderSyncEngine(fx.Db, options, Log, () => Now);
    }

    public SendFixture Fx { get; }
    public SyncOptions Options { get; }
    public MemorySyncLog Log { get; }
    public DateTimeOffset Now { get; set; }
    public ImapFolderSyncEngine Engine { get; }
    public MailDatabase Db => Fx.Db;
    public string InboxId => Fx.FolderId("INBOX");

    public static async Task<ImapEngineFixture> CreateAsync(SyncOptions? options = null, string providerId = "imap.generic")
        => new(await SendFixture.CreateAsync(providerId), options ?? SyncOptions.Default);

    public FakeImapSource Source(string folder = "INBOX", ImapServerCaps? caps = null) => new(Fx.FolderId(folder), caps);

    public async Task<FolderSyncReport> SyncAsync(FakeImapSource source, string folder = "INBOX", bool force = false, int limit = 100)
    {
        var entity = (await Db.GetFolderAsync(Fx.FolderId(folder)))!;
        return await Engine.SyncFolderAsync(source, entity, limit, force, CancellationToken.None);
    }

    public async Task<MessageEntity?> MessageAsync(long uid, string folder = "INBOX") => await Db.GetMessageAsync($"{Fx.FolderId(folder)}:{uid}");

    public ValueTask DisposeAsync() => Fx.DisposeAsync();
}

internal sealed class FakeGraphSource : IGraphDeltaSource
{
    public Queue<object> Responses { get; } = new();    // GraphDeltaResult 或 Exception
    public List<(string? DeltaLink, DateTimeOffset? Since)> Calls { get; } = new();

    public Task<GraphDeltaResult> FetchDeltaAsync(string remoteFolderId, string? deltaLink, int pageSize, DateTimeOffset? receivedSince, CancellationToken ct)
    {
        Calls.Add((deltaLink, receivedSince));
        var next = Responses.Dequeue();
        if (next is Exception ex)
        {
            throw ex;
        }

        return Task.FromResult((GraphDeltaResult)next);
    }

    public static MailMessageSyncItem Item(Guid accountId, string graphId, bool read = false, bool flagged = false, DateTimeOffset? received = null)
    {
        var id = $"{accountId:D}:{graphId}";
        var at = received ?? SendFixture.T0;
        var summary = new MailMessageSummary(id, "f", $"主题 {graphId}", "bob@example.com", at, read, flagged, "预览");
        return new MailMessageSyncItem(summary, Math.Abs(graphId.GetHashCode()) + 1L, $"<{graphId}@x>", "bob@example.com", "Bob", false, 10, false, at);
    }

    public static GraphDeltaEntry Full(Guid accountId, string graphId, bool read = false, bool flagged = false)
        => new(graphId, false, Item(accountId, graphId, read, flagged));

    public static GraphDeltaEntry Removed(string graphId) => new(graphId, true, null);

    public static GraphDeltaEntry Partial(string graphId, bool? read = null, bool? flagged = null) => new(graphId, false, null, read, flagged);
}
