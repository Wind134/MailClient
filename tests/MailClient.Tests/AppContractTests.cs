using MailClient.Core;
using MailClient.Core.Tray;
using MailClient.Setup.Core;
using MailClient.ViewModels;

namespace MailClient.Tests;

/// <summary>设置界面的数值展示（原实现埋在 WinUI 的 SettingsSections 里，无法覆盖）。</summary>
public class SettingsFormatTests
{
    [Theory]
    [InlineData(0, "不限制")]
    [InlineData(-1, "不限制")]
    [InlineData(1, "1 天")]
    [InlineData(30, "30 天")]
    [InlineData(364, "364 天")]
    [InlineData(365, "1 年")]
    [InlineData(730, "2 年")]
    public void Days_Formats_And_Treats_Zero_As_Unlimited(int days, string expected)
        => Assert.Equal(expected, SettingsFormat.Days(days));

    [Theory]
    [InlineData(0, "不限制")]
    [InlineData(-5, "不限制")]
    [InlineData(100, "100 MB")]
    [InlineData(1000, "1000 MB")]
    [InlineData(1024, "1 GB")]
    [InlineData(2048, "2 GB")]
    [InlineData(1536, "1536 MB")]
    public void Megabytes_Prefers_Whole_Gigabytes(int megabytes, string expected)
        => Assert.Equal(expected, SettingsFormat.Megabytes(megabytes));

    [Theory]
    [InlineData(0, "0 B")]
    [InlineData(-100, "0 B")]
    [InlineData(512, "512 B")]
    [InlineData(1024, "1 KB")]
    [InlineData(1536, "1.5 KB")]
    [InlineData(1048576, "1 MB")]
    [InlineData(1572864, "1.5 MB")]
    [InlineData(1073741824, "1 GB")]
    public void Bytes_Scales_Units(long bytes, string expected)
        => Assert.Equal(expected, SettingsFormat.Bytes(bytes));
}

/// <summary>#RRGGBB 解析（原实现依赖 WinUI 的 Color 类型，无法在纯逻辑测试里覆盖）。</summary>
public class HexColorTests
{
    [Theory]
    [InlineData("#FF0000", 255, 0, 0)]
    [InlineData("00FF00", 0, 255, 0)]
    [InlineData("  #0000ff  ", 0, 0, 255)]
    [InlineData("#1B1B1B", 27, 27, 27)]
    public void TryParse_Reads_Rgb(string hex, byte r, byte g, byte b)
    {
        Assert.True(HexColor.TryParse(hex, out var actualR, out var actualG, out var actualB));
        Assert.Equal((r, g, b), (actualR, actualG, actualB));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("#FFF")]
    [InlineData("##FF0000")]
    [InlineData("#GGGGGG")]
    [InlineData("#FF00000")]
    public void TryParse_Rejects_Invalid(string? hex)
        => Assert.False(HexColor.TryParse(hex, out _, out _, out _));
}

public class AttachmentDirectorySanitizeTests
{
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(".")]
    [InlineData("..")]
    [InlineData("...")]
    public void DirectoryFor_Rejects_Traversal_And_Blank_Ids(string id)
    {
        Assert.Throws<ArgumentException>(() => MailClient.Core.Sending.DraftAttachmentStore.DirectoryFor("C:\\root", id));
        Assert.Throws<ArgumentException>(() => MailClient.Core.Sending.OutboxAttachmentStore.DirectoryFor("C:\\root", id));
    }

    [Fact]
    public void DirectoryFor_Sanitizes_Invalid_FileName_Characters()
    {
        var dir = MailClient.Core.Sending.DraftAttachmentStore.DirectoryFor("C:\\root", "a/b:c");
        Assert.Equal(Path.Combine("C:\\root", "a_b_c"), dir);
    }
}

/// <summary>
/// 应用与安装程序共同约定的标识必须一致。
/// 此前这些值是两处各写一遍的字面量，改一边不会报错，只会让通知标题 /
/// 任务栏分组 / 开机启动静默失效，而且没有任何测试能发现。
/// </summary>
public class AppIdentityContractTests
{
    [Fact]
    public void Setup_Uses_Same_Aumid_As_App()
        => Assert.Equal(AppIdentifiers.Aumid, SetupConstants.Aumid);

    [Fact]
    public void Setup_Uses_Same_ProductName_As_App()
        => Assert.Equal(AppIdentifiers.ProductName, SetupConstants.ProductName);

    [Fact]
    public void Startup_Run_Value_Name_Matches_Installer()
        => Assert.Equal(AppIdentifiers.StartupRunValueName, SetupConstants.StartupRunValueName);

    [Fact]
    public void StartupLaunch_Uses_Central_Run_Value_Name()
        => Assert.Equal(AppIdentifiers.StartupRunValueName, StartupLaunch.RunValueName);

    [Fact]
    public void Aumid_Is_Not_Empty_And_Has_No_Spaces()
    {
        Assert.False(string.IsNullOrWhiteSpace(AppIdentifiers.Aumid));
        Assert.DoesNotContain(' ', AppIdentifiers.Aumid);
    }
}
