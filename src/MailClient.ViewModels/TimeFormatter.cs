using System.Globalization;

namespace MailClient.ViewModels;

/// <summary>邮件列表 / 阅读窗格的时间文案（本地时区，纯函数便于单测）。</summary>
public static class TimeFormatter
{
    private static readonly string[] WeekdayNames = ["周日", "周一", "周二", "周三", "周四", "周五", "周六"];

    public static string FormatShort(DateTimeOffset value, DateTimeOffset now)
    {
        var local = value.ToLocalTime();
        var today = now.ToLocalTime().Date;
        var date = local.Date;

        if (date == today)
        {
            return FormatClock(local);
        }

        if (date == today.AddDays(-1))
        {
            return UiStrings.Yesterday;
        }

        if (date > today.AddDays(-7) && date < today)
        {
            return WeekdayNames[(int)date.DayOfWeek];
        }

        return date.Year == today.Year
            ? $"{date.Month}月{date.Day}日"
            : date.ToString("yyyy/M/d", CultureInfo.InvariantCulture);
    }

    /// <summary>上午 10:24 / 下午 3:05</summary>
    public static string FormatClock(DateTimeOffset local)
    {
        var hour = local.Hour;
        var period = hour < 12 ? UiStrings.Morning : UiStrings.Afternoon;
        var h12 = hour % 12 == 0 ? 12 : hour % 12;
        return $"{period} {h12}:{local.Minute:D2}";
    }

    public static string FormatFull(DateTimeOffset value)
    {
        var local = value.ToLocalTime();
        return $"{local.Year}年{local.Month}月{local.Day}日 {FormatClock(local)}";
    }

    public static string FormatSize(long bytes)
    {
        if (bytes < 1024)
        {
            return $"{bytes} B";
        }

        if (bytes < 1024 * 1024)
        {
            return $"{bytes / 1024.0:0.#} KB";
        }

        return $"{bytes / 1024.0 / 1024.0:0.#} MB";
    }
}
