namespace MailClient.ViewModels;

/// <summary>设置界面的容量 / 时长展示。纯函数，便于单测（此前埋在 UI 代码里无法覆盖）。</summary>
public static class SettingsFormat
{
    /// <summary>保留天数：0 或负数表示不限制，满整年按年展示。</summary>
    public static string Days(int days)
        => days <= 0
            ? "不限制"
            : days % 365 == 0 && days >= 365
                ? $"{days / 365} 年"
                : $"{days} 天";

    /// <summary>缓存上限（MB）：0 或负数表示不限制，满整 GB 按 GB 展示。</summary>
    public static string Megabytes(int megabytes)
        => megabytes <= 0
            ? "不限制"
            : megabytes >= 1024 && megabytes % 1024 == 0
                ? $"{megabytes / 1024} GB"
                : $"{megabytes} MB";

    /// <summary>字节数：负数按 0 处理，按 B / KB / MB / GB 自适应。</summary>
    public static string Bytes(long bytes)
    {
        if (bytes < 0)
        {
            bytes = 0;
        }

        if (bytes < 1024)
        {
            return $"{bytes} B";
        }

        var value = bytes / 1024.0;
        if (value < 1024)
        {
            return $"{value:0.#} KB";
        }

        value /= 1024;
        return value < 1024 ? $"{value:0.#} MB" : $"{value / 1024:0.##} GB";
    }
}
