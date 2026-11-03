namespace MailClient.Core.Retention;

/// <summary>
/// 正文缓存的上限。0 表示“不限”。
/// 超过任一上限时，最久没打开的邮件正文（含内嵌图）会被清出本地库，下次打开时按需从服务器重新拉取；
/// 列表行（发件人 / 主题 / 预览 / 标志）、草稿、发件箱以及还有待写回操作的邮件不受影响。
/// </summary>
/// <param name="MaxAgeDays">正文自上次打开 / 拉取起的最长保留天数（0 = 不按时间清理）。</param>
/// <param name="MaxCacheMb">正文缓存总大小上限（MB，0 = 不限制大小）。</param>
public sealed record StorageOptions(int MaxAgeDays, int MaxCacheMb)
{
    public const int DefaultMaxAgeDays = 90;
    public const int DefaultMaxCacheMb = 200;

    public static StorageOptions Default { get; } = new(DefaultMaxAgeDays, DefaultMaxCacheMb);

    /// <summary>字节上限；<see cref="long.MaxValue"/> 表示不限。</summary>
    public long MaxCacheBytes => MaxCacheMb <= 0 ? long.MaxValue : MaxCacheMb * 1024L * 1024L;

    /// <summary>最长保留时间；null 表示不按时间清理。</summary>
    public TimeSpan? MaxAge => MaxAgeDays <= 0 ? null : TimeSpan.FromDays(MaxAgeDays);

    /// <summary>设置里没写的用默认值，并夹在合理范围内（天数 0 或 7–3650；大小 0 或 20–102400 MB）。</summary>
    public static StorageOptions FromSettings(int? maxAgeDays, int? maxCacheMb)
    {
        var days = maxAgeDays ?? DefaultMaxAgeDays;
        days = days <= 0 ? 0 : Math.Clamp(days, 7, 3650);
        var mb = maxCacheMb ?? DefaultMaxCacheMb;
        mb = mb <= 0 ? 0 : Math.Clamp(mb, 20, 102_400);
        return new StorageOptions(days, mb);
    }
}
