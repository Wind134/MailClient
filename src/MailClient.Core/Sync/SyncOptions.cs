namespace MailClient.Core.Sync;

/// <summary>双向同步的可配置参数。</summary>
/// <param name="PollInterval">后台轮询间隔（其余文件夹 / 无 IDLE 的账号 / Graph）。</param>
/// <param name="ReconcileInterval">无 CONDSTORE 的文件夹，两次“全窗口对账”之间的最短间隔（强制刷新 / IDLE 通知例外）。</param>
/// <param name="WindowMaxMessages">对账窗口：最新 N 封。</param>
/// <param name="WindowMaxAge">对账窗口：最近 D 天（与上一条取并集）。</param>
/// <param name="IdleEnabled">是否使用 IMAP IDLE。</param>
public sealed record SyncOptions(
    TimeSpan PollInterval,
    TimeSpan ReconcileInterval,
    int WindowMaxMessages,
    TimeSpan WindowMaxAge,
    bool IdleEnabled)
{
    public static SyncOptions Default { get; } = new(
        TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(5), 2000, TimeSpan.FromDays(30), IdleEnabled: true);

    public static SyncOptions FromSettings(int? pollMinutes, int? windowMessages, int? windowDays, bool? idle)
    {
        var poll = TimeSpan.FromMinutes(Math.Clamp(pollMinutes ?? 5, 1, 120));
        return new SyncOptions(
            poll,
            poll,
            Math.Clamp(windowMessages ?? 2000, 50, 50_000),
            TimeSpan.FromDays(Math.Clamp(windowDays ?? 30, 1, 3650)),
            idle ?? true);
    }
}
