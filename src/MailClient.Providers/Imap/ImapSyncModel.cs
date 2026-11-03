using MailClient.Core;
using MailClient.Core.Sync;

namespace MailClient.Providers.Imap;

/// <summary>某个文件夹这一次采用的同步策略（由服务器能力 + 本地已存的状态决定）。</summary>
public enum ImapSyncStrategy
{
    /// <summary>还没有游标：只拉最近一批邮件，建立基线。</summary>
    Initial,

    /// <summary>QRESYNC：SELECT 时带上上次的 UIDVALIDITY / MODSEQ，服务器直接给出标志变化 + VANISHED（被删除的 UID）。</summary>
    QResync,

    /// <summary>CONDSTORE：FETCH CHANGEDSINCE 取标志变化；删除靠“窗口内 UID 差集”。</summary>
    CondStore,

    /// <summary>无 CONDSTORE：窗口内 FETCH (UID FLAGS) 全量对账，同时作为删除检测。有节流。</summary>
    Reconcile
}

/// <param name="CondStore">CONDSTORE（RFC 7162）：有 HIGHESTMODSEQ / CHANGEDSINCE。</param>
/// <param name="QResync">QRESYNC：已在连接上 ENABLE，SELECT 可以带 QRESYNC 参数。</param>
/// <param name="Idle">IDLE（RFC 2177）。</param>
/// <param name="GmailExt">X-GM-EXT-1（Gmail 扩展：X-GM-MSGID / X-GM-THRID / X-GM-LABELS）。</param>
public sealed record ImapServerCaps(bool CondStore, bool QResync, bool Idle, bool GmailExt)
{
    public static ImapServerCaps None { get; } = new(false, false, false, false);
}

/// <summary>SELECT 之后拿到的文件夹状态。0 = 服务器没有给出。</summary>
public sealed record ImapFolderOpen(long UidValidity, long UidNext, long HighestModSeq, int Count);

/// <summary>带 QRESYNC 参数 SELECT 的结果：打开时服务器顺带给出的标志变化和被删除的 UID。</summary>
public sealed record ImapQResyncOpen(ImapFolderOpen Open, IReadOnlyList<ServerFlagState> Flags, IReadOnlyList<long> Vanished);

/// <summary>
/// 同步引擎需要的 IMAP 操作（<see cref="GenericImapProvider"/> 实现；测试用假实现）。
/// 所有方法针对“当前选中的文件夹”按 <paramref name="remotePath"/> 打开；不涉及网络之外的状态。
/// </summary>
public interface IImapSyncSource
{
    Task<ImapServerCaps> GetCapabilitiesAsync(CancellationToken ct);

    /// <summary>（重新）SELECT 文件夹，返回最新的 UIDVALIDITY / UIDNEXT / HIGHESTMODSEQ / EXISTS。</summary>
    Task<ImapFolderOpen> SelectAsync(string remotePath, CancellationToken ct);

    /// <summary>SELECT … (QRESYNC (uidvalidity modseq known-uids))。失败时抛异常，引擎会降级到 CONDSTORE。</summary>
    Task<ImapQResyncOpen> SelectQResyncAsync(string remotePath, long uidValidity, long highestModSeq, IReadOnlyList<long> knownUids, CancellationToken ct);

    /// <summary>UID FETCH 1:* (FLAGS) (CHANGEDSINCE modseq)。</summary>
    Task<IReadOnlyList<ServerFlagState>> FetchFlagsChangedSinceAsync(string remotePath, long modSeq, CancellationToken ct);

    /// <summary>UID FETCH minUid:* (UID FLAGS)：窗口内全量标志（也用来判断哪些 UID 还存在）。</summary>
    Task<IReadOnlyList<ServerFlagState>> FetchUidFlagsAsync(string remotePath, long minUid, CancellationToken ct);

    /// <summary>UID SEARCH UID minUid:*：窗口内仍存在的 UID。</summary>
    Task<IReadOnlyList<long>> SearchUidsAsync(string remotePath, long minUid, CancellationToken ct);

    /// <summary>UID 大于 <paramref name="afterUid"/> 的新邮件（最多最新的 <paramref name="limit"/> 封）。</summary>
    Task<IReadOnlyList<MailMessageSyncItem>> FetchNewAsync(string remotePath, long? afterUid, int limit, CancellationToken ct);
}

public sealed record FolderSyncReport(string FolderId)
{
    public ImapSyncStrategy Strategy { get; init; }

    public int NewMessages { get; init; }

    public int FlagUpdates { get; init; }

    public int Removed { get; init; }

    public int GmailDuplicatesMerged { get; init; }

    public bool UidValidityReset { get; init; }

    /// <summary>这一轮在本地库里改动了什么（用来决定要不要刷新界面）。</summary>
    public bool Changed => NewMessages > 0 || FlagUpdates > 0 || Removed > 0 || UidValidityReset || GmailDuplicatesMerged > 0;

    public override string ToString()
        => $"{FolderId}: {Strategy} new={NewMessages} flags={FlagUpdates} removed={Removed} merged={GmailDuplicatesMerged}{(UidValidityReset ? " UIDVALIDITY-RESET" : "")}";
}

/// <summary>引擎用到的纯函数（可单测）：对账窗口、差集、标志差异、节流判断。</summary>
public static class ImapDiff
{
    /// <summary>
    /// 对账窗口：最新 <paramref name="maxMessages"/> 封 ∪ 最近 <paramref name="maxAge"/> 内收到的邮件；
    /// 返回 UID ≥ 窗口最小 UID 的全部本地行（窗口按 UID 取“后缀”，这样 SEARCH / FETCH 只要一个 <c>minUid:*</c> 区间）。
    /// </summary>
    public static IReadOnlyList<LocalUidRow> SelectWindow(
        IReadOnlyList<LocalUidRow> rows, DateTimeOffset now, int maxMessages, TimeSpan maxAge)
    {
        if (rows.Count == 0)
        {
            return rows;
        }

        var ordered = rows.OrderBy(r => r.Uid).ToList();
        var minByCount = ordered[Math.Max(0, ordered.Count - Math.Max(1, maxMessages))].Uid;
        var cutoff = now - maxAge;
        long? minByAge = ordered.Where(r => r.ReceivedAt is { } at && at >= cutoff).Select(r => (long?)r.Uid).Min();
        var minUid = minByAge is { } a ? Math.Min(minByCount, a) : minByCount;
        return ordered.Where(r => r.Uid >= minUid).ToList();
    }

    /// <summary>本地有、服务器已没有的 UID（服务器上被删除或被移走）。</summary>
    public static IReadOnlyList<long> FindMissing(IReadOnlyList<LocalUidRow> window, IEnumerable<long> serverUids)
    {
        var present = serverUids as ISet<long> ?? serverUids.ToHashSet();
        return window.Where(r => !present.Contains(r.Uid)).Select(r => r.Uid).ToList();
    }

    /// <summary>服务器标志与本地不同的邮件（只返回有差异的，省得逐封查库）。</summary>
    public static IReadOnlyList<ServerFlagState> ChangedFlags(IReadOnlyList<LocalUidRow> window, IEnumerable<ServerFlagState> server)
    {
        var local = window.ToDictionary(r => r.Uid);
        var changed = new List<ServerFlagState>();
        foreach (var s in server)
        {
            if (local.TryGetValue(s.Uid, out var row) && (row.IsRead != s.IsRead || row.IsFlagged != s.IsFlagged))
            {
                changed.Add(s);
            }
        }

        return changed;
    }

    /// <summary>无 CONDSTORE 时，这一轮要不要做全窗口对账：强制 / 从没对账过 / 距上次已过 <paramref name="interval"/>。</summary>
    public static bool ReconcileDue(DateTimeOffset? last, DateTimeOffset now, TimeSpan interval, bool force)
        => force || last is null || now - last.Value >= interval || now < last.Value;

    /// <summary>
    /// 服务器返回“空”但文件夹明明还有邮件（EXISTS &gt; 0）、本地窗口里却有邮件：多半是半截响应 / 异常，不能据此删光本地邮件。
    /// </summary>
    public static bool LooksLikeBogusEmptyListing(int serverUidCount, int serverExists, int windowCount)
        => serverUidCount == 0 && windowCount > 0 && serverExists > 0;
}
