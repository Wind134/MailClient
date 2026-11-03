using MailClient.Core;
using MailClient.Core.Retention;

namespace MailClient.Tests;

public class StorageRetentionTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 1, 12, 0, 0, TimeSpan.Zero);
    private const long Mb = 1024L * 1024L;

    private static BodyCacheEntry E(string id, long mb, int daysAgo)
        => new(id, mb * Mb, Now.AddDays(-daysAgo));

    [Fact]
    public void Defaults_Are_90_Days_And_200_Mb()
    {
        Assert.Equal(90, StorageOptions.Default.MaxAgeDays);
        Assert.Equal(200, StorageOptions.Default.MaxCacheMb);
        Assert.Equal(200 * Mb, StorageOptions.Default.MaxCacheBytes);
        Assert.Equal(TimeSpan.FromDays(90), StorageOptions.Default.MaxAge);
        Assert.Equal(StorageOptions.Default, StorageOptions.FromSettings(null, null));
    }

    [Theory]
    [InlineData(0, 0, 0, 0)]          // 0 = 不限
    [InlineData(-5, -1, 0, 0)]
    [InlineData(1, 1, 7, 20)]         // 夹到下限
    [InlineData(100000, 9999999, 3650, 102400)]
    [InlineData(30, 500, 30, 500)]
    public void FromSettings_Clamps(int days, int mb, int expectedDays, int expectedMb)
    {
        var options = StorageOptions.FromSettings(days, mb);
        Assert.Equal(expectedDays, options.MaxAgeDays);
        Assert.Equal(expectedMb, options.MaxCacheMb);
    }

    [Fact]
    public void Unlimited_Means_No_Age_And_No_Size_Limit()
    {
        var options = new StorageOptions(0, 0);
        Assert.Null(options.MaxAge);
        Assert.Equal(long.MaxValue, options.MaxCacheBytes);
        var plan = BodyCachePlanner.Plan(new[] { E("a", 500, 4000), E("b", 900, 1) }, Now, options);
        Assert.Equal(0, plan.Count);
    }

    [Fact]
    public void Age_Evicts_Only_Older_Than_Limit()
    {
        var entries = new[] { E("old", 1, 91), E("edge", 1, 90), E("new", 1, 3) };
        var plan = BodyCachePlanner.Plan(entries, Now, new StorageOptions(90, 0));
        Assert.Equal(new[] { "old" }, plan.MessageIds);
        Assert.Equal(1, plan.EvictedByAge);
        Assert.Equal(0, plan.EvictedBySize);
        Assert.Equal(1 * Mb, plan.FreedBytes);
        Assert.Equal(2 * Mb, plan.RemainingBytes);
    }

    [Fact]
    public void Size_Evicts_Oldest_First_Until_Under_Limit()
    {
        var entries = new[] { E("a", 100, 10), E("b", 100, 30), E("c", 100, 20), E("d", 100, 1) };
        var plan = BodyCachePlanner.Plan(entries, Now, new StorageOptions(0, 200));
        // 400 MB → 200 MB：清最久没打开的两封（b=30天、c=20天）
        Assert.Equal(new[] { "b", "c" }, plan.MessageIds);
        Assert.Equal(2, plan.EvictedBySize);
        Assert.Equal(200 * Mb, plan.RemainingBytes);
    }

    [Fact]
    public void Size_Limit_Counts_After_Age_Eviction()
    {
        var entries = new[] { E("ancient", 150, 400), E("a", 100, 5), E("b", 90, 2) };
        var plan = BodyCachePlanner.Plan(entries, Now, new StorageOptions(90, 200));
        // 年龄先清掉 ancient；剩 190 MB 已在 200 MB 以内，不再按大小清
        Assert.Equal(new[] { "ancient" }, plan.MessageIds);
        Assert.Equal(1, plan.EvictedByAge);
        Assert.Equal(0, plan.EvictedBySize);
    }

    [Fact]
    public void Ties_Evict_Larger_First_Then_By_Id()
    {
        var entries = new[] { E("b", 10, 5), E("a", 10, 5), E("big", 50, 5) };
        var plan = BodyCachePlanner.Plan(entries, Now, new StorageOptions(0, 20));
        // 70 MB → ≤20 MB：先清 big(50) → 20，停
        Assert.Equal(new[] { "big" }, plan.MessageIds);

        var plan2 = BodyCachePlanner.Plan(new[] { E("b", 10, 5), E("a", 10, 5) }, Now, new StorageOptions(0, 20) with { MaxCacheMb = 20 });
        Assert.Equal(0, plan2.Count); // 刚好 20 MB，不超
    }

    [Fact]
    public void Plan_Is_Deterministic_And_Empty_Input_Is_Empty()
    {
        Assert.Equal(0, BodyCachePlanner.Plan(Array.Empty<BodyCacheEntry>(), Now, StorageOptions.Default).Count);
        var entries = new[] { E("x", 60, 1), E("y", 60, 1), E("z", 60, 1) };
        var first = BodyCachePlanner.Plan(entries, Now, new StorageOptions(0, 100)).MessageIds;
        var second = BodyCachePlanner.Plan(entries.Reverse().ToArray(), Now, new StorageOptions(0, 100)).MessageIds;
        Assert.Equal(first, second);
        Assert.Equal(new[] { "x", "y" }, first);
    }

    [Fact]
    public void PlanAll_Takes_Everything()
    {
        var plan = BodyCachePlanner.PlanAll(new[] { E("a", 1, 1), E("b", 2, 1) });
        Assert.Equal(2, plan.Count);
        Assert.Equal(3 * Mb, plan.FreedBytes);
        Assert.Equal(0, plan.RemainingBytes);
    }

    [Fact]
    public void Settings_Roundtrip_Storage_And_LinkMode()
    {
        var dir = Path.Combine(Path.GetTempPath(), "mc-settings-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var path = Path.Combine(dir, "settings.json");
            var store = new AppSettingsStore(path);
            Assert.Equal(StorageOptions.Default, store.GetStorageOptions());
            Assert.Equal(MailClient.Core.Links.LinkOpenMode.System, store.GetLinkOpenMode());

            store.Update(s =>
            {
                s.BodyCacheMaxAgeDays = 30;
                s.BodyCacheMaxMb = 500;
                s.LinkOpenMode = "Internal";
            });

            var reloaded = new AppSettingsStore(path);
            Assert.Equal(new StorageOptions(30, 500), reloaded.GetStorageOptions());
            Assert.Equal(MailClient.Core.Links.LinkOpenMode.Internal, reloaded.GetLinkOpenMode());
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { /* ignore */ }
        }
    }
}
