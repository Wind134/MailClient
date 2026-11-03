using MailClient.Core;
using MailClient.Core.Tray;

namespace MailClient.Tests;

public class CloseToTrayPolicyTests
{
    [Theory]
    [InlineData(true, false, true)]   // 开后台 + 普通关窗 → 藏托盘
    [InlineData(true, true, false)]   // 开后台 + 强制退出 → 真正关
    [InlineData(false, false, false)] // 关后台 + 普通关窗 → 真正关
    [InlineData(false, true, false)]  // 关后台 + 强制退出 → 真正关
    public void ShouldHide_Matches_Matrix(bool keep, bool forceQuit, bool expectedHide)
        => Assert.Equal(expectedHide, CloseToTrayPolicy.ShouldHideInsteadOfClose(keep, forceQuit));

    [Fact]
    public void ResolveKeepRunning_Null_Is_Default_On()
        => Assert.True(CloseToTrayPolicy.ResolveKeepRunning(null));

    [Fact]
    public void ResolveKeepRunning_Explicit()
    {
        Assert.True(CloseToTrayPolicy.ResolveKeepRunning(true));
        Assert.False(CloseToTrayPolicy.ResolveKeepRunning(false));
    }

    [Fact]
    public void Settings_Default_And_Roundtrip_KeepRunningInBackground()
    {
        var dir = Path.Combine(Path.GetTempPath(), "mc-tray-settings-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var path = Path.Combine(dir, "settings.json");
            var store = new AppSettingsStore(path);
            Assert.True(store.GetKeepRunningInBackground());
            Assert.False(store.Snapshot().TrayHintShown);

            store.Update(s =>
            {
                s.KeepRunningInBackground = false;
                s.TrayHintShown = true;
            });

            var reloaded = new AppSettingsStore(path);
            Assert.False(reloaded.GetKeepRunningInBackground());
            Assert.True(reloaded.Snapshot().TrayHintShown);

            // 关掉再打开：应真正退出
            Assert.False(CloseToTrayPolicy.ShouldHideInsteadOfClose(
                reloaded.GetKeepRunningInBackground(), forceQuit: false));

            // 托盘「退出」路径：即使开着后台也真正退出
            store.Update(s => s.KeepRunningInBackground = true);
            Assert.False(CloseToTrayPolicy.ShouldHideInsteadOfClose(
                store.GetKeepRunningInBackground(), forceQuit: true));
            Assert.True(CloseToTrayPolicy.ShouldHideInsteadOfClose(
                store.GetKeepRunningInBackground(), forceQuit: false));
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { /* ignore */ }
        }
    }
}
