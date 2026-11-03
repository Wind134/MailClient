using System.Globalization;
using MailClient.Core.Logging;
using MailClient.Core.Retention;

namespace MailClient.Storage.Services;

/// <summary>一次完整维护的结果。</summary>
public sealed record StorageMaintenanceResult(BodyEvictionResult Eviction, DatabaseMaintenanceResult Database, int LogsRotated, long LogBytesFreed);

/// <summary>设置页“存储”区要显示的数据。</summary>
public sealed record StorageSnapshot(StorageUsage Usage, long LogBytes, StorageOptions Options);

/// <summary>
/// 存储维护：按设置的上限清理正文缓存、整理数据库（auto_vacuum 转换 / incremental_vacuum / 偶尔 VACUUM / wal_checkpoint）、轮换日志。
/// 启动后延迟一会儿在后台线程跑一次，之后每 6 小时一次；所有耗时操作都放在线程池，不占 UI 线程。
/// </summary>
public sealed class StorageMaintenanceService : IDisposable
{
    public static readonly string[] LogFileNames = { "list-debug.log", "sync.log", "startup.log" };

    private static readonly TimeSpan FullVacuumInterval = TimeSpan.FromDays(60);

    private readonly MailDatabase _database;
    private readonly string _logDirectory;
    private readonly Func<StorageOptions> _options;
    private readonly Action<string>? _log;
    private readonly Func<DateTimeOffset> _clock;
    private readonly SemaphoreSlim _runGate = new(1, 1);
    private readonly CancellationTokenSource _cts = new();
    private Task? _loop;

    public StorageMaintenanceService(
        MailDatabase database,
        string logDirectory,
        Func<StorageOptions> options,
        Action<string>? log = null,
        Func<DateTimeOffset>? clock = null)
    {
        _database = database ?? throw new ArgumentNullException(nameof(database));
        _logDirectory = logDirectory ?? throw new ArgumentNullException(nameof(logDirectory));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _log = log;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    /// <summary>启动后台维护循环（只会启动一次）。</summary>
    public void Start(TimeSpan? initialDelay = null, TimeSpan? interval = null)
    {
        if (_loop is not null)
        {
            return;
        }

        var delay = initialDelay ?? TimeSpan.FromSeconds(20);
        var every = interval ?? TimeSpan.FromHours(6);
        _loop = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(delay, _cts.Token).ConfigureAwait(false);
                while (!_cts.IsCancellationRequested)
                {
                    try
                    {
                        await RunAsync(_cts.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        _log?.Invoke($"存储维护失败：{ex.Message}");
                    }

                    await Task.Delay(every, _cts.Token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                // 退出
            }
        });
    }

    /// <summary>按设置的上限维护一次：轮换日志 → 清理超限正文 → 整理数据库。</summary>
    public Task<StorageMaintenanceResult> RunAsync(CancellationToken cancellationToken = default)
        => Task.Run(async () =>
        {
            await _runGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var rotated = RotateLogs();
                var eviction = await _database.EvictBodyCacheAsync(_options(), _clock(), all: false, cancellationToken).ConfigureAwait(false);
                var forceFull = ShouldRunFullVacuum();
                var db = await _database.RunDatabaseMaintenanceAsync(forceFull, cancellationToken: cancellationToken).ConfigureAwait(false);
                if (db.Vacuumed)
                {
                    MarkVacuumed();
                }

                Report("维护完成", eviction, db);
                return new StorageMaintenanceResult(eviction, db, rotated, 0);
            }
            finally
            {
                _runGate.Release();
            }
        }, cancellationToken);

    /// <summary>“清理缓存”：清掉所有可清理的正文缓存（之后打开邮件会重新从服务器拉取）+ 旧日志，并把腾出的空间还给磁盘。</summary>
    public Task<StorageMaintenanceResult> ClearCacheAsync(CancellationToken cancellationToken = default)
        => Task.Run(async () =>
        {
            await _runGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var eviction = await _database.EvictBodyCacheAsync(_options(), _clock(), all: true, cancellationToken).ConfigureAwait(false);
                var db = await _database.RunDatabaseMaintenanceAsync(false, minFreeBytesForIncremental: 0, cancellationToken).ConfigureAwait(false);
                if (db.Vacuumed)
                {
                    MarkVacuumed();
                }

                var logFreed = DeleteOldLogs();
                Report("清理缓存", eviction, db);
                return new StorageMaintenanceResult(eviction, db, 0, logFreed);
            }
            finally
            {
                _runGate.Release();
            }
        }, cancellationToken);

    /// <summary>“压缩数据库”：完整 VACUUM（并确保 auto_vacuum=INCREMENTAL）。</summary>
    public Task<DatabaseMaintenanceResult> CompactDatabaseAsync(CancellationToken cancellationToken = default)
        => Task.Run(async () =>
        {
            await _runGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var db = await _database.RunDatabaseMaintenanceAsync(true, cancellationToken: cancellationToken).ConfigureAwait(false);
                if (db.Vacuumed)
                {
                    MarkVacuumed();
                }

                Report("压缩数据库", BodyEvictionResult.None, db);
                return db;
            }
            finally
            {
                _runGate.Release();
            }
        }, cancellationToken);

    public Task<StorageSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default)
        => Task.Run(async () =>
        {
            var usage = await _database.GetStorageUsageAsync(cancellationToken).ConfigureAwait(false);
            return new StorageSnapshot(usage, GetLogBytes(), _options());
        }, cancellationToken);

    /// <summary>三个日志（含旧文件）总共占多少字节。</summary>
    public long GetLogBytes() => LogFileNames.Sum(n => RotatingLog.TotalBytes(Path.Combine(_logDirectory, n)));

    /// <summary>对历史遗留的超大日志做一次轮换（启动 / 维护时）。返回轮换的文件数。</summary>
    public int RotateLogs()
        => LogFileNames.Count(n => RotatingLog.RotateIfNeeded(Path.Combine(_logDirectory, n)));

    private long DeleteOldLogs()
    {
        long freed = 0;
        foreach (var name in LogFileNames)
        {
            var path = Path.Combine(_logDirectory, name);
            for (var i = 1; i <= RotatingLog.DefaultKeep; i++)
            {
                var old = $"{path}.{i}";
                try
                {
                    if (File.Exists(old))
                    {
                        freed += new FileInfo(old).Length;
                        File.Delete(old);
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

    private string StatePath => Path.Combine(_logDirectory, "last-vacuum.txt");

    private bool ShouldRunFullVacuum()
    {
        try
        {
            if (!File.Exists(StatePath))
            {
                MarkVacuumed(); // 第一次：从现在开始计时，不立刻 VACUUM（转换 auto_vacuum 时自己会 VACUUM）
                return false;
            }

            return DateTimeOffset.TryParse(File.ReadAllText(StatePath).Trim(), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var last)
                   && _clock() - last > FullVacuumInterval;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private void MarkVacuumed()
    {
        try
        {
            Directory.CreateDirectory(_logDirectory);
            File.WriteAllText(StatePath, _clock().ToString("O", CultureInfo.InvariantCulture));
        }
        catch (Exception)
        {
            // 记不下来就下次再说
        }
    }

    private void Report(string what, BodyEvictionResult eviction, DatabaseMaintenanceResult db)
        => _log?.Invoke(
            $"存储维护 [{what}]：清理正文 {eviction.Evicted} 封（按时间 {eviction.ByAge} / 按大小 {eviction.BySize}，约 {eviction.FreedBytes / 1024} KB）；"
            + $"转换 auto_vacuum={db.ConvertedAutoVacuum}，VACUUM={db.Vacuumed}，回收 {db.ReclaimedBytes / 1024} KB"
            + (db.SearchIndexRebuilt ? "，已重建搜索索引" : string.Empty)
            + (db.Skipped is null ? string.Empty : $"，{db.Skipped}"));

    public void Dispose()
    {
        _cts.Cancel();
    }
}
