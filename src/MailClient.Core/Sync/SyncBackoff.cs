namespace MailClient.Core.Sync;

/// <summary>指数退避：连续失败越多等得越久（上限封顶），成功一次清零。</summary>
public sealed class SyncBackoff
{
    private readonly TimeSpan _initial;
    private readonly TimeSpan _max;
    private readonly double _factor;

    public SyncBackoff(TimeSpan? initial = null, TimeSpan? max = null, double factor = 2.0)
    {
        _initial = initial ?? TimeSpan.FromSeconds(5);
        _max = max ?? TimeSpan.FromMinutes(15);
        _factor = factor < 1 ? 1 : factor;
    }

    public int Failures { get; private set; }

    /// <summary>记一次失败，返回这次应等待的时间。</summary>
    public TimeSpan Failure()
    {
        Failures++;
        return DelayFor(Failures, _initial, _max, _factor);
    }

    public void Success() => Failures = 0;

    /// <summary>第 <paramref name="failures"/> 次连续失败后的等待：initial × factor^(n-1)，不超过 max。</summary>
    public static TimeSpan DelayFor(int failures, TimeSpan initial, TimeSpan max, double factor = 2.0)
    {
        if (failures <= 0)
        {
            return TimeSpan.Zero;
        }

        var seconds = initial.TotalSeconds * Math.Pow(factor, failures - 1);
        return seconds >= max.TotalSeconds || double.IsInfinity(seconds) ? max : TimeSpan.FromSeconds(seconds);
    }
}
