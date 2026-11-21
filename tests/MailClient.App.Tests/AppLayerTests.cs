using MailClient.Core;
using MailClient_App;

namespace MailClient.App.Tests;

/// <summary>App 层内部约定：资产路径与标识常量。</summary>
public class AppIdentityTests
{
    [Fact]
    public void Unpackaged_Aumid_Comes_From_Core()
        => Assert.Equal(AppIdentifiers.Aumid, AppIdentity.UnpackagedAumid);

    [Fact]
    public void AssetPath_Is_Absolute_And_Under_Assets()
    {
        var path = AppIdentity.AssetPath("AppIcon.ico");

        Assert.True(Path.IsPathRooted(path));
        Assert.Equal("Assets", Path.GetFileName(Path.GetDirectoryName(path)));
        Assert.Equal("AppIcon.ico", Path.GetFileName(path));
    }

    [Fact]
    public void AssetPath_Is_Stable()
        => Assert.Equal(AppIdentity.AssetPath("a.png"), AppIdentity.AssetPath("a.png"));
}

/// <summary>通知激活参数解析：冷启动点通知时由 App.OnLaunched 调用。</summary>
public class NotificationArgumentsTests
{
    [Fact]
    public void Missing_Arguments_Produce_No_Action()
        => Assert.Null(NotificationService.ParseArguments(null));

    [Fact]
    public void Empty_Arguments_Produce_No_Action()
        => Assert.Null(NotificationService.ParseArguments(new Dictionary<string, string>()));
}
