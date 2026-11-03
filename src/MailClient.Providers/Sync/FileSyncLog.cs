using MailClient.Core.Services;

namespace MailClient.Providers.Sync;

/// <summary>把同步日志追加到文本文件（UIDVALIDITY 重置、丢弃的待写回操作、IDLE 重连等）。超过 1 MB 就轮换（sync.log.1 / .2），不会无限增长。</summary>
public sealed class FileSyncLog : ISyncLog
{
    private const long MaxBytes = 1_000_000;
    private readonly string _path;
    private readonly object _lock = new();

    public FileSyncLog(string path)
    {
        _path = path;
    }

    public void Info(string message) => Write("INFO ", message);

    public void Warn(string message) => Write("WARN ", message);

    private void Write(string level, string message)
    {
        try
        {
            lock (_lock)
            {
                // 超过上限就轮换成 sync.log.1 / .2（保留 2 份旧文件），而不是整个删掉
                MailClient.Core.Logging.RotatingLog.Append(
                    _path,
                    $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz} {level}{message}{Environment.NewLine}",
                    MaxBytes);
            }
        }
        catch (Exception)
        {
            // 日志写不进去不影响同步
        }
    }
}
