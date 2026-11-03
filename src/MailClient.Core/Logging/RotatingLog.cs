using System.Text;

namespace MailClient.Core.Logging;

/// <summary>
/// 带轮换的文本日志：单个文件超过 <see cref="DefaultMaxBytes"/>（约 1 MB）就改名为 .1（原 .1 → .2 …，最多保留 <see cref="DefaultKeep"/> 份旧文件），
/// 再从空文件写起。永远不抛异常——日志写不进去不能影响业务。
/// </summary>
public static class RotatingLog
{
    public const long DefaultMaxBytes = 1_000_000;
    public const int DefaultKeep = 2;

    private static readonly object Gate = new();

    /// <summary>追加一段文本（调用方自己带换行）；必要时先轮换。</summary>
    public static void Append(string path, string text, long maxBytes = DefaultMaxBytes, int keep = DefaultKeep)
    {
        try
        {
            lock (Gate)
            {
                var dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir))
                {
                    Directory.CreateDirectory(dir);
                }

                var incoming = Encoding.UTF8.GetByteCount(text);
                if (File.Exists(path) && new FileInfo(path).Length + incoming > maxBytes)
                {
                    RotateCore(path, keep);
                }

                File.AppendAllText(path, text);
            }
        }
        catch (Exception)
        {
            // 日志写不进去不影响业务
        }
    }

    /// <summary>文件已经超过上限就轮换一次（启动时对历史遗留的超大日志用）。返回是否发生了轮换。</summary>
    public static bool RotateIfNeeded(string path, long maxBytes = DefaultMaxBytes, int keep = DefaultKeep)
    {
        try
        {
            lock (Gate)
            {
                if (!File.Exists(path) || new FileInfo(path).Length <= maxBytes)
                {
                    return false;
                }

                RotateCore(path, keep);
                return true;
            }
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>当前文件 + 所有旧文件占用的字节数。</summary>
    public static long TotalBytes(string path, int keep = DefaultKeep)
    {
        long total = 0;
        try
        {
            for (var i = 0; i <= keep; i++)
            {
                var file = i == 0 ? path : $"{path}.{i}";
                if (File.Exists(file))
                {
                    total += new FileInfo(file).Length;
                }
            }
        }
        catch (Exception)
        {
            // 统计失败按已累计的算
        }

        return total;
    }

    /// <summary>删除当前文件和所有旧文件（清理缓存时用）。返回释放的字节数。</summary>
    public static long DeleteAll(string path, int keep = DefaultKeep)
    {
        long freed = 0;
        lock (Gate)
        {
            for (var i = 0; i <= keep; i++)
            {
                var file = i == 0 ? path : $"{path}.{i}";
                try
                {
                    if (File.Exists(file))
                    {
                        freed += new FileInfo(file).Length;
                        File.Delete(file);
                    }
                }
                catch (Exception)
                {
                    // 被占用就留着
                }
            }
        }

        return freed;
    }

    private static void RotateCore(string path, int keep)
    {
        keep = Math.Max(0, keep);
        if (keep == 0)
        {
            File.Delete(path);
            return;
        }

        var oldest = $"{path}.{keep}";
        if (File.Exists(oldest))
        {
            File.Delete(oldest);
        }

        for (var i = keep - 1; i >= 1; i--)
        {
            var from = $"{path}.{i}";
            if (File.Exists(from))
            {
                File.Move(from, $"{path}.{i + 1}", overwrite: true);
            }
        }

        File.Move(path, $"{path}.1", overwrite: true);
    }
}
