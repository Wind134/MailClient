namespace MailClient.Core;

/// <summary>指数退避的通用计算：第 n 次连续失败后等 initial × factor^(n-1)，不超过 max。</summary>
public static class Backoff
{
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
