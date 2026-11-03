using MailClient.Storage;

namespace MailClient.Tests;

/// <summary>每个测试一个临时 SQLite 文件，用完删除。</summary>
internal sealed class TempDb : IAsyncDisposable
{
    public TempDb()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"mailclient-ui-{Guid.NewGuid():N}.db");
        Database = new MailDatabase(Path);
    }

    public string Path { get; }
    public MailDatabase Database { get; }

    public static async Task<TempDb> CreateAsync()
    {
        var db = new TempDb();
        await db.Database.InitializeAsync();
        return db;
    }

    public static async Task<TempDb> CreateSeededAsync()
    {
        var db = new TempDb();
        await db.Database.InitializeAsync();
        await MailClient.Storage.Demo.DemoDataSeeder.SeedIfEmptyAsync(db.Database, new DateTimeOffset(2026, 10, 31, 10, 30, 0, TimeSpan.FromHours(8)));
        return db;
    }

    public async ValueTask DisposeAsync()
    {
        await Database.DisposeAsync();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        foreach (var f in new[] { Path, Path + "-wal", Path + "-shm" })
        {
            try { File.Delete(f); } catch { /* ignore */ }
        }
    }
}
