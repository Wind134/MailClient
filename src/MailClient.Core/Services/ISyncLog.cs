namespace MailClient.Core.Services;

/// <summary>同步过程的诊断日志（UIDVALIDITY 重置、丢弃的待写回操作、策略选择等）。</summary>
public interface ISyncLog
{
    void Info(string message);

    void Warn(string message);
}

public sealed class NullSyncLog : ISyncLog
{
    public static readonly NullSyncLog Instance = new();

    public void Info(string message)
    {
    }

    public void Warn(string message)
    {
    }
}

/// <summary>把日志写进内存（测试用）并可选转发给另一个日志。</summary>
public sealed class MemorySyncLog : ISyncLog
{
    private readonly List<string> _lines = new();
    private readonly object _lock = new();

    public IReadOnlyList<string> Lines
    {
        get
        {
            lock (_lock)
            {
                return _lines.ToList();
            }
        }
    }

    public void Info(string message) => Add("INFO " + message);

    public void Warn(string message) => Add("WARN " + message);

    private void Add(string line)
    {
        lock (_lock)
        {
            _lines.Add(line);
        }
    }
}
