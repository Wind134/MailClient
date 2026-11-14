using MailClient.Core;
using MailClient.Core.Tray;

namespace MailClient.Tests;

public class StartupLaunchTests
{
    [Theory]
    [InlineData("--silent", true)]
    [InlineData("--Silent", true)]
    [InlineData("--quit", false)]
    [InlineData("--quit --silent", true)]
    [InlineData("", false)]
    public void HasSilentArgument_Tokens(string args, bool expected)
        => Assert.Equal(expected, StartupLaunch.HasSilentArgument(args.Split(' ', StringSplitOptions.RemoveEmptyEntries)));

    [Theory]
    [InlineData("--silent", true)]
    [InlineData("  --SILENT  ", true)]
    [InlineData("\"C:\\Apps\\MailClient.App.exe\" --silent", true)]
    [InlineData("--quit", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void HasSilentArgument_CommandLine(string? commandLine, bool expected)
        => Assert.Equal(expected, StartupLaunch.HasSilentArgument(commandLine));

    [Fact]
    public void BuildCommand_QuotesPath_And_Matches()
    {
        var command = StartupLaunch.BuildCommand(@"C:\Program Files\MailClient\MailClient.App.exe");
        Assert.Equal("\"C:\\Program Files\\MailClient\\MailClient.App.exe\" --silent", command);
        Assert.True(StartupLaunch.CommandMatches(command, @"C:\Program Files\MailClient\MailClient.App.exe"));
        Assert.True(StartupLaunch.CommandMatches("  " + command + "  ", @"C:\Program Files\MailClient\MailClient.App.exe"));
        Assert.False(StartupLaunch.CommandMatches(command, @"D:\MailClient.App.exe"));
    }

    [Fact]
    public void BuildCommand_Rejects_Blank_Or_Quotes()
    {
        Assert.Throws<ArgumentException>(() => StartupLaunch.BuildCommand("  "));
        Assert.Throws<ArgumentException>(() => StartupLaunch.BuildCommand("C:\\a\\\"b.exe"));
        Assert.False(StartupLaunch.CommandMatches("whatever", "C:\\a\\\"b.exe"));
    }

    [Fact]
    public void ApprovedBlob_Disabled_Is_First_Byte_3()
    {
        Assert.False(StartupLaunch.IsApprovedDisabled(null));
        Assert.False(StartupLaunch.IsApprovedDisabled(Array.Empty<byte>()));
        Assert.False(StartupLaunch.IsApprovedDisabled(StartupLaunch.ApprovedEnabledBlob()));
        Assert.True(StartupLaunch.IsApprovedDisabled(new byte[] { StartupLaunch.ApprovedDisabled, 0 }));
        Assert.Equal(12, StartupLaunch.ApprovedEnabledBlob().Length);
    }

    [Fact]
    public void Settings_Roundtrip_LaunchAtStartup_Defaults_Off()
    {
        var dir = Path.Combine(Path.GetTempPath(), "mc-startup-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var path = Path.Combine(dir, "settings.json");
            var store = new AppSettingsStore(path);
            Assert.False(store.Snapshot().LaunchAtStartup);

            store.Update(s => s.LaunchAtStartup = true);
            var reloaded = new AppSettingsStore(path);
            Assert.True(reloaded.Snapshot().LaunchAtStartup);

            reloaded.Update(s => s.LaunchAtStartup = false);
            Assert.False(new AppSettingsStore(path).Snapshot().LaunchAtStartup);
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { /* ignore */ }
        }
    }
}
