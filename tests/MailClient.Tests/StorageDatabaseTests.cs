using MailClient.Core;
using MailClient.Core.Retention;
using MailClient.Storage;
using MailClient.Storage.Services;
using Microsoft.Data.Sqlite;

namespace MailClient.Tests;

public sealed class StorageDatabaseTests : IAsyncDisposable
{
    private readonly List<string> _paths = new();
    private readonly List<MailDatabase> _dbs = new();
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "mc-storage-" + Guid.NewGuid().ToString("N"));

    public StorageDatabaseTests() => Directory.CreateDirectory(_dir);

    public async ValueTask DisposeAsync()
    {
        foreach (var db in _dbs)
        {
            try { await db.DisposeAsync(); } catch { /* ignore */ }
        }

        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, true); } catch { /* ignore */ }
    }

    private string NewPath() => Path.Combine(_dir, $"t-{Guid.NewGuid():N}.db");

    private async Task<MailDatabase> OpenAsync(string path)
    {
        var db = new MailDatabase(path);
        await db.InitializeAsync();
        _dbs.Add(db);
        return db;
    }

    private async Task CloseAsync(MailDatabase db)
    {
        await db.DisposeAsync();
        _dbs.Remove(db);
        SqliteConnection.ClearAllPools();
    }

    private static void Raw(string path, string sql, params (string, object?)[] args)
    {
        using var c = new SqliteConnection($"Data Source={path}");
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (k, v) in args)
        {
            cmd.Parameters.AddWithValue(k, v ?? DBNull.Value);
        }

        cmd.ExecuteNonQuery();
    }

    private static long RawScalar(string path, string sql)
    {
        using var c = new SqliteConnection($"Data Source={path}");
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        return Convert.ToInt64(cmd.ExecuteScalar());
    }

    private static string Stamp(DateTimeOffset t) => t.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'");

    private sealed record Seed(Guid Account, string Folder);

    private static async Task<Seed> SeedAccountAsync(MailDatabase db)
    {
        var accountId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        await db.UpsertAccountAsync(new AccountEntity(accountId, "测", "a@b.com", "imap", AuthKind.Password, now, now, null, true));
        var folderId = $"{accountId:D}:inbox";
        await db.UpsertFolderAsync(new FolderEntity(folderId, accountId, "收件箱", SpecialFolder.Inbox, "INBOX", 0, 0, 0, null, null));
        return new Seed(accountId, folderId);
    }

    /// <summary>写一封带已缓存正文的邮件；daysAgo = 上次打开距今天数。</summary>
    private async Task AddCachedAsync(MailDatabase db, string path, Seed seed, string id, string body, int daysAgo,
        bool draft = false, int localState = 0, bool pendingOp = false, string? subject = null)
    {
        var now = DateTimeOffset.UtcNow;
        await db.UpsertMessageAsync(new MessageEntity(
            id, seed.Account, seed.Folder, RemoteUid: draft ? null : Math.Abs(id.GetHashCode()), RfcMessageId: $"<{id}@x>",
            ThreadKey: null, Subject: subject ?? ("主题 " + id), FromAddress: "f@x.com", FromName: "F",
            Preview: "预览 " + id, BodyText: null, BodyHtml: null, ReceivedAt: now.AddDays(-daysAgo), SentAt: now.AddDays(-daysAgo),
            IsRead: true, IsFlagged: false, IsDraft: draft, HasAttachments: false,
            Category: MailCategory.Updates, SizeBytes: body.Length, BodyFetched: false));
        await db.SaveMessageBodyAsync(id, new MessageBodyContent(body, "<p>" + body + "</p>", []));
        Raw(path, "UPDATE messages SET BodyAccessedAt = $t, LocalState = $s WHERE Id = $id;",
            ("$t", Stamp(now.AddDays(-daysAgo))), ("$s", localState), ("$id", id));
        if (pendingOp)
        {
            var ts = Stamp(now);
            Raw(path, "INSERT INTO pending_ops (AccountId, MessageId, Field, Kind, CreatedAt, UpdatedAt) VALUES ($a, $m, 'IsRead', 'set', $t, $t);",
                ("$a", seed.Account.ToString("D")), ("$m", id), ("$t", ts));
        }
    }

    private static async Task<bool> IsCachedAsync(MailDatabase db, string id)
        => (await db.GetMessageAsync(id))!.BodyFetched;

    // ───────── 迁移 ─────────

    [Fact]
    public async Task New_Database_Is_V7_With_Incremental_AutoVacuum()
    {
        var path = NewPath();
        var db = await OpenAsync(path);
        Assert.Equal(7, await db.GetSchemaVersionAsync());
        Assert.Equal(2, await db.GetAutoVacuumModeAsync());
        Assert.Equal(1, RawScalar(path, "SELECT COUNT(*) FROM pragma_table_info('messages') WHERE name = 'BodyAccessedAt';"));
    }

    [Fact]
    public async Task V7_Migration_Backfills_Existing_Cached_Bodies_And_Keeps_Data()
    {
        var path = NewPath();
        var db = await OpenAsync(path);
        var seed = await SeedAccountAsync(db);
        await AddCachedAsync(db, path, seed, "m1", "正文一", daysAgo: 400);
        await CloseAsync(db);

        // 还原成 V6：去掉 V7 的索引 / 列 / 版本号
        Raw(path, "DROP INDEX ix_messages_body_cache; ALTER TABLE messages DROP COLUMN BodyAccessedAt; DELETE FROM schema_version WHERE version = 7;");
        Assert.Equal(6, RawScalar(path, "SELECT MAX(version) FROM schema_version;"));

        var upgraded = await OpenAsync(path);
        Assert.Equal(7, await upgraded.GetSchemaVersionAsync());
        var msg = await upgraded.GetMessageAsync("m1");
        Assert.True(msg!.BodyFetched);
        Assert.Equal("正文一", msg.BodyText);
        // 回填成迁移时刻：不会一升级就被按“老邮件”清掉
        var entries = await upgraded.ListBodyCacheEntriesAsync();
        var entry = Assert.Single(entries);
        Assert.True(DateTimeOffset.UtcNow - entry.LastAccess < TimeSpan.FromMinutes(5));
        var plan = await upgraded.EvictBodyCacheAsync(StorageOptions.Default, DateTimeOffset.UtcNow);
        Assert.Equal(0, plan.Evicted);
    }

    [Fact]
    public async Task Existing_Database_Without_AutoVacuum_Is_Converted_By_Maintenance()
    {
        var path = NewPath();
        var db = await OpenAsync(path);
        var seed = await SeedAccountAsync(db);
        await AddCachedAsync(db, path, seed, "m1", "body", daysAgo: 1);
        await CloseAsync(db);

        Raw(path, "PRAGMA auto_vacuum = NONE; VACUUM;");
        Assert.Equal(0, RawScalar(path, "PRAGMA auto_vacuum;"));

        var reopened = await OpenAsync(path);
        var result = await reopened.RunDatabaseMaintenanceAsync();
        Assert.True(result.ConvertedAutoVacuum);
        Assert.Equal(2, await reopened.GetAutoVacuumModeAsync());
        Assert.Equal("body", (await reopened.GetMessageAsync("m1"))!.BodyText);
        Assert.Equal("ok", await reopened.IntegrityCheckAsync());

        // 已经是 INCREMENTAL：再跑不会再转换
        var again = await reopened.RunDatabaseMaintenanceAsync();
        Assert.False(again.ConvertedAutoVacuum);
    }

    // ───────── 访问时间 ─────────

    [Fact]
    public async Task SaveBody_Sets_Access_Time_And_Touch_Refreshes_It()
    {
        var path = NewPath();
        var db = await OpenAsync(path);
        var seed = await SeedAccountAsync(db);
        await AddCachedAsync(db, path, seed, "m1", "body", daysAgo: 120);
        var before = Assert.Single(await db.ListBodyCacheEntriesAsync());
        Assert.True(DateTimeOffset.UtcNow - before.LastAccess > TimeSpan.FromDays(100));

        await db.TouchBodyAccessAsync("m1");
        var after = Assert.Single(await db.ListBodyCacheEntriesAsync());
        Assert.True(DateTimeOffset.UtcNow - after.LastAccess < TimeSpan.FromMinutes(5));
    }

    [Fact]
    public async Task Sync_Upsert_Does_Not_Refresh_Access_Time()
    {
        var path = NewPath();
        var db = await OpenAsync(path);
        var seed = await SeedAccountAsync(db);
        await AddCachedAsync(db, path, seed, "m1", "body", daysAgo: 120);
        var now = DateTimeOffset.UtcNow;
        // 同步再次 upsert（无正文）
        await db.UpsertMessageAsync(new MessageEntity(
            "m1", seed.Account, seed.Folder, 1, "<m1@x>", null, "主题 m1", "f@x.com", "F", "预览 m1", null, null,
            now, now, IsRead: false, IsFlagged: false, IsDraft: false, HasAttachments: false, MailCategory.Updates, 4, BodyFetched: false));
        var entry = Assert.Single(await db.ListBodyCacheEntriesAsync());
        Assert.True(DateTimeOffset.UtcNow - entry.LastAccess > TimeSpan.FromDays(100));
        Assert.True(await IsCachedAsync(db, "m1"));
    }

    // ───────── 清理 ─────────

    [Fact]
    public async Task Age_Eviction_Clears_Bodies_But_Keeps_List_Rows()
    {
        var path = NewPath();
        var db = await OpenAsync(path);
        var seed = await SeedAccountAsync(db);
        await AddCachedAsync(db, path, seed, "old", "旧正文", daysAgo: 200);
        await AddCachedAsync(db, path, seed, "new", "新正文", daysAgo: 5);

        var result = await db.EvictBodyCacheAsync(new StorageOptions(90, 0), DateTimeOffset.UtcNow);
        Assert.Equal(1, result.Evicted);
        Assert.Equal(1, result.ByAge);
        Assert.True(result.FreedBytes > 0);

        var old = (await db.GetMessageAsync("old"))!;
        Assert.False(old.BodyFetched);
        Assert.Null(old.BodyText);
        Assert.Null(old.BodyHtml);
        Assert.Equal("主题 old", old.Subject);
        Assert.False(string.IsNullOrEmpty(old.Preview)); // 列表预览保留
        var fresh = (await db.GetMessageAsync("new"))!;
        Assert.True(fresh.BodyFetched);
        Assert.Equal("新正文", fresh.BodyText);
    }

    [Fact]
    public async Task Protected_Messages_Are_Never_Evicted()
    {
        var path = NewPath();
        var db = await OpenAsync(path);
        var seed = await SeedAccountAsync(db);
        await AddCachedAsync(db, path, seed, "plain", "x", daysAgo: 300);
        await AddCachedAsync(db, path, seed, "draft:abc", "草稿正文", daysAgo: 300, draft: true);
        await AddCachedAsync(db, path, seed, "flagdraft", "草稿2", daysAgo: 300, draft: true);
        await AddCachedAsync(db, path, seed, "sentcopy", "本地已发送", daysAgo: 300, localState: 1);
        await AddCachedAsync(db, path, seed, "moved", "本地移动", daysAgo: 300, localState: 2);
        await AddCachedAsync(db, path, seed, "pending", "待写回", daysAgo: 300, pendingOp: true);

        var result = await db.EvictBodyCacheAsync(new StorageOptions(7, 20), DateTimeOffset.UtcNow, all: true);
        Assert.Equal(1, result.Evicted);
        Assert.False(await IsCachedAsync(db, "plain"));
        foreach (var id in new[] { "draft:abc", "flagdraft", "sentcopy", "moved", "pending" })
        {
            Assert.True(await IsCachedAsync(db, id), id);
        }

        Assert.Equal("草稿正文", (await db.GetMessageAsync("draft:abc"))!.BodyText);
    }

    [Fact]
    public async Task Size_Eviction_Removes_Least_Recently_Opened_First()
    {
        var path = NewPath();
        var db = await OpenAsync(path);
        var seed = await SeedAccountAsync(db);
        var big = new string('x', 600_000); // 约 0.6 MB 文本 + 同量 HTML ≈ 1.2 MB / 封
        await AddCachedAsync(db, path, seed, "a", big, daysAgo: 10);
        await AddCachedAsync(db, path, seed, "b", big, daysAgo: 40);
        await AddCachedAsync(db, path, seed, "c", big, daysAgo: 20);

        var usage = await db.GetStorageUsageAsync();
        Assert.Equal(3, usage.BodyCount);
        Assert.True(usage.BodyCacheBytes > 3L * 1024 * 1024);
        Assert.Equal(usage.BodyCacheBytes, usage.EvictableBytes);

        // 上限 2 MB：总量约 3.6 MB → 先清最久没打开的 b，再清 c，留下 a
        var result = await db.EvictBodyCacheAsync(new StorageOptions(0, 2), DateTimeOffset.UtcNow);
        Assert.Equal(2, result.BySize);
        Assert.True(await IsCachedAsync(db, "a"));
        Assert.False(await IsCachedAsync(db, "b"));
        Assert.False(await IsCachedAsync(db, "c"));
    }

    [Fact]
    public async Task Evicted_Body_Can_Be_Cached_Again()
    {
        var path = NewPath();
        var db = await OpenAsync(path);
        var seed = await SeedAccountAsync(db);
        await AddCachedAsync(db, path, seed, "m", "第一次", daysAgo: 500);
        await db.EvictBodyCacheAsync(StorageOptions.Default, DateTimeOffset.UtcNow);
        Assert.False(await IsCachedAsync(db, "m"));

        await db.SaveMessageBodyAsync("m", new MessageBodyContent("重新拉取", "<p>重新拉取</p>", []));
        var m = (await db.GetMessageAsync("m"))!;
        Assert.True(m.BodyFetched);
        Assert.Equal("重新拉取", m.BodyText);
        var entry = Assert.Single(await db.ListBodyCacheEntriesAsync());
        Assert.True(DateTimeOffset.UtcNow - entry.LastAccess < TimeSpan.FromMinutes(5));
        Assert.Equal(0, (await db.EvictBodyCacheAsync(StorageOptions.Default, DateTimeOffset.UtcNow)).Evicted);
    }

    [Fact]
    public async Task Demo_Data_Is_Not_Evictable()
    {
        await using var tmp = await TempDb.CreateSeededAsync();
        Assert.Empty(await tmp.Database.ListBodyCacheEntriesAsync());
        var result = await tmp.Database.EvictBodyCacheAsync(StorageOptions.Default, DateTimeOffset.UtcNow, all: true);
        Assert.Equal(0, result.Evicted);
    }

    // ───────── 搜索索引 / VACUUM ─────────

    private static async Task<IReadOnlyList<MessageEntity>> SearchAsync(MailDatabase db, string text)
        => await db.QueryMessagesAsync(new MessageListFilter(null, null, false, false, text, 100, null, false));

    [Fact]
    public async Task Search_Index_Follows_Eviction_Refetch_And_Vacuum()
    {
        var path = NewPath();
        var db = await OpenAsync(path);
        var seed = await SeedAccountAsync(db);
        var filler = string.Concat(Enumerable.Repeat("填充文字 ", 600)); // 把关键词推到列表预览（很短）之外
        const string m1Body = "这里有独特词汇 zebraquartz 出现";
        await AddCachedAsync(db, path, seed, "m1", filler + m1Body, daysAgo: 300, subject: "普通主题");
        await AddCachedAsync(db, path, seed, "m2", "另一封 giraffemarble 的内容", daysAgo: 1, subject: "另一个主题");
        Assert.Contains(await SearchAsync(db, "zebraquartz"), m => m.Id == "m1");

        await db.EvictBodyCacheAsync(StorageOptions.Default, DateTimeOffset.UtcNow);
        Assert.DoesNotContain(await SearchAsync(db, "zebraquartz"), m => m.Id == "m1");
        Assert.Contains(await SearchAsync(db, "giraffemarble"), m => m.Id == "m2");

        await db.SaveMessageBodyAsync("m1", new MessageBodyContent(filler + m1Body, "<p>x</p>", []));
        Assert.Contains(await SearchAsync(db, "zebraquartz"), m => m.Id == "m1");

        var vacuum = await db.RunDatabaseMaintenanceAsync(forceFullVacuum: true);
        Assert.True(vacuum.Vacuumed);
        Assert.Contains(await SearchAsync(db, "zebraquartz"), m => m.Id == "m1");
        Assert.Contains(await SearchAsync(db, "giraffemarble"), m => m.Id == "m2");
        Assert.Equal("ok", await db.IntegrityCheckAsync());
    }

    [Fact]
    public async Task RebuildSearchIndex_Restores_Body_Search()
    {
        var path = NewPath();
        var db = await OpenAsync(path);
        var seed = await SeedAccountAsync(db);
        await AddCachedAsync(db, path, seed, "m1", "needle-in-haystack", daysAgo: 1, subject: "s");
        await db.RebuildSearchIndexAsync();
        Assert.Contains(await SearchAsync(db, "haystack"), m => m.Id == "m1");
    }

    // ───────── 服务：清理缓存 / 压缩 / 日志 ─────────

    [Fact]
    public async Task ClearCache_Shrinks_File_And_Keeps_Protected_Data()
    {
        var path = NewPath();
        var db = await OpenAsync(path);
        var seed = await SeedAccountAsync(db);
        var body = new string('y', 40_000);
        for (var i = 0; i < 100; i++)
        {
            await AddCachedAsync(db, path, seed, "m" + i, body, daysAgo: i % 7);
        }

        await AddCachedAsync(db, path, seed, "draft:keep", "draft body", daysAgo: 1, draft: true);
        var logDir = Path.Combine(_dir, "logs");
        Directory.CreateDirectory(logDir);
        using var service = new StorageMaintenanceService(db, logDir, () => StorageOptions.Default, null, null);
        var sizeBefore = new FileInfo(path).Length;
        Assert.True(sizeBefore > 7_000_000);

        var result = await service.ClearCacheAsync();
        Assert.Equal(100, result.Eviction.Evicted);
        var sizeAfter = new FileInfo(path).Length;
        Assert.True(sizeAfter < sizeBefore / 2, $"before={sizeBefore} after={sizeAfter}");
        Assert.True(await IsCachedAsync(db, "draft:keep"));
        var all = await db.ListMessagesAsync(seed.Folder, 1000);
        Assert.True(all.Count >= 100);
        Assert.Equal("ok", await db.IntegrityCheckAsync());
    }

    [Fact]
    public async Task Compact_Runs_Full_Vacuum()
    {
        var path = NewPath();
        var db = await OpenAsync(path);
        using var service = new StorageMaintenanceService(db, _dir, () => StorageOptions.Default, null, null);
        var result = await service.CompactDatabaseAsync();
        Assert.True(result.Vacuumed);
        Assert.Equal(2, await db.GetAutoVacuumModeAsync());
    }

    [Fact]
    public async Task Run_Applies_Limits_From_Options()
    {
        var path = NewPath();
        var db = await OpenAsync(path);
        var seed = await SeedAccountAsync(db);
        await AddCachedAsync(db, path, seed, "old", "o", daysAgo: 100);
        await AddCachedAsync(db, path, seed, "new", "n", daysAgo: 1);
        var options = new StorageOptions(30, 0);
        using var service = new StorageMaintenanceService(db, _dir, () => options, null, null);
        var result = await service.RunAsync();
        Assert.Equal(1, result.Eviction.Evicted);
        Assert.False(await IsCachedAsync(db, "old"));
        Assert.True(await IsCachedAsync(db, "new"));
    }

    [Fact]
    public async Task Snapshot_Reports_Db_Body_And_Log_Usage()
    {
        var path = NewPath();
        var db = await OpenAsync(path);
        var seed = await SeedAccountAsync(db);
        await AddCachedAsync(db, path, seed, "m", new string('z', 5000), daysAgo: 1);
        var logDir = Path.Combine(_dir, "logs2");
        Directory.CreateDirectory(logDir);
        File.WriteAllText(Path.Combine(logDir, "sync.log"), new string('l', 1234));
        File.WriteAllText(Path.Combine(logDir, "sync.log.1"), new string('l', 100));
        using var service = new StorageMaintenanceService(db, logDir, () => StorageOptions.Default, null, null);
        var snap = await service.GetSnapshotAsync();
        Assert.True(snap.Usage.DatabaseBytes > 0);
        Assert.True(snap.Usage.BodyCacheBytes >= 10_000);
        Assert.Equal(1, snap.Usage.BodyCount);
        Assert.Equal(1334, snap.LogBytes);
        Assert.Equal(StorageOptions.Default, snap.Options);
    }

    [Fact]
    public async Task RotateLogs_Caps_Oversized_Legacy_Logs()
    {
        var path = NewPath();
        var db = await OpenAsync(path);
        var logDir = Path.Combine(_dir, "logs3");
        Directory.CreateDirectory(logDir);
        File.WriteAllText(Path.Combine(logDir, "list-debug.log"), new string('a', 1_500_000));
        File.WriteAllText(Path.Combine(logDir, "sync.log"), "small");
        using var service = new StorageMaintenanceService(db, logDir, () => StorageOptions.Default, null, null);
        Assert.Equal(1, service.RotateLogs());
        Assert.True(File.Exists(Path.Combine(logDir, "list-debug.log.1")));
        Assert.False(File.Exists(Path.Combine(logDir, "sync.log.1")));
        Assert.Equal(0, service.RotateLogs());
    }
}
