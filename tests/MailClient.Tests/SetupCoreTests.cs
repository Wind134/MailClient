using MailClient.Setup.Core;

namespace MailClient.Tests;

/// <summary>安装程序里不碰系统的纯逻辑：命令行解析、安装目录校验、文件清单。（真正写注册表 / 快捷方式的部分在真机上验证。）</summary>
public class SetupCoreTests
{
    [Fact]
    public void Parse_Defaults_AreInteractiveInstallWithDesktopShortcut()
    {
        var a = SetupArguments.Parse(Array.Empty<string>());
        Assert.Equal(SetupMode.Install, a.Mode);
        Assert.False(a.Silent);
        Assert.True(a.DesktopShortcut);
        Assert.False(a.DeleteUserData);
        Assert.Null(a.InstallDir);
        Assert.Null(a.Preview);
    }

    [Fact]
    public void Parse_Uninstall_AndSilent_AndFlags()
    {
        var a = SetupArguments.Parse(new[] { "/uninstall", "/S", "/deletedata", "/nodesktop", "/run" });
        Assert.Equal(SetupMode.Uninstall, a.Mode);
        Assert.True(a.Silent);
        Assert.True(a.DeleteUserData);
        Assert.False(a.DesktopShortcut);
        Assert.True(a.Run);
    }

    [Theory]
    [InlineData("/D=D:\\Apps\\MailClient")]
    [InlineData("--dir=D:\\Apps\\MailClient")]
    [InlineData("/dir=\"D:\\Apps\\MailClient\"")]
    public void Parse_InstallDir_Forms(string arg)
    {
        Assert.Equal("D:\\Apps\\MailClient", SetupArguments.Parse(new[] { arg }).InstallDir);
    }

    [Fact]
    public void Parse_InstallDir_SeparateValue()
    {
        Assert.Equal("D:\\Apps\\MailClient", SetupArguments.Parse(new[] { "--dir", "D:\\Apps\\MailClient" }).InstallDir);
    }

    [Fact]
    public void Parse_PreviewAndTheme()
    {
        var a = SetupArguments.Parse(new[] { "/preview=done", "/theme=dark" });
        Assert.Equal("done", a.Preview);
        Assert.Equal("dark", a.Theme);
    }

    [Fact]
    public void Default_InstallDir_IsPerUser_AndValid()
    {
        Assert.Contains(Path.Combine("Programs", "MailClient"), SetupPaths.DefaultInstallDir);
        Assert.Null(SetupPaths.ValidateInstallDir(SetupPaths.DefaultInstallDir));
        Assert.NotEqual(SetupPaths.UserDataDir, SetupPaths.DefaultInstallDir);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("relative\\path")]
    [InlineData("C:\\")]
    [InlineData("\\\\server\\share\\MailClient")]
    public void ValidateInstallDir_RejectsBadInput(string dir)
    {
        Assert.NotNull(SetupPaths.ValidateInstallDir(dir));
    }

    [Fact]
    public void ValidateInstallDir_RejectsUserDataFolderAndItsParentsAndChildren()
    {
        var data = SetupPaths.UserDataDir;
        Assert.NotNull(SetupPaths.ValidateInstallDir(data));
        Assert.NotNull(SetupPaths.ValidateInstallDir(Path.Combine(data, "app")));
        Assert.NotNull(SetupPaths.ValidateInstallDir(SetupPaths.LocalAppData));
    }

    [Fact]
    public void ValidateInstallDir_RejectsWindowsFolder()
    {
        var win = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        Assert.NotNull(SetupPaths.ValidateInstallDir(Path.Combine(win, "MailClient")));
    }

    [Fact]
    public void ValidateInstallDir_RejectsOverlongPath()
    {
        Assert.NotNull(SetupPaths.ValidateInstallDir("C:\\" + new string('a', 150) + "\\" + new string('b', 100)));
    }

    [Theory]
    [InlineData("a.dll", true)]
    [InlineData("Assets\\icon.png", true)]
    [InlineData("Assets/icon.png", true)]
    [InlineData("..\\evil.dll", false)]
    [InlineData("a\\..\\..\\evil.dll", false)]
    [InlineData("C:\\Windows\\notepad.exe", false)]
    [InlineData("\\abs\\path", false)]
    [InlineData("", false)]
    public void Manifest_OnlyAllowsRelativePathsInsideInstallDir(string line, bool safe)
    {
        Assert.Equal(safe, InstallManifest.IsSafeRelative(line));
    }

    [Fact]
    public void Manifest_RoundTrip_DropsUnsafeLines()
    {
        var dir = Path.Combine(Path.GetTempPath(), "mc-setup-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            InstallManifest.Write(dir, new[] { "b.dll", "Assets\\a.png", "a.exe" });
            Assert.Equal(new[] { "a.exe", "Assets\\a.png", "b.dll" }, InstallManifest.Read(dir));

            File.AppendAllText(Path.Combine(dir, SetupConstants.ManifestFileName), "..\\..\\evil.dll\r\nC:\\Windows\\x.dll\r\n");
            Assert.Equal(3, InstallManifest.Read(dir).Count);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void Manifest_Read_MissingFile_IsEmpty()
    {
        Assert.Empty(InstallManifest.Read(Path.Combine(Path.GetTempPath(), "mc-setup-missing-" + Guid.NewGuid().ToString("N"))));
    }

    [Fact]
    public void IsSameOrInside_Works()
    {
        Assert.True(SetupPaths.IsSameOrInside("C:\\a\\b", "C:\\a"));
        Assert.True(SetupPaths.IsSameOrInside("C:\\A\\B\\", "c:\\a\\b"));
        Assert.False(SetupPaths.IsSameOrInside("C:\\ab", "C:\\a"));
        Assert.False(SetupPaths.IsSameOrInside("C:\\a", "C:\\a\\b"));
    }

    [Fact]
    public void Constants_MatchAppIdentity()
    {
        Assert.Equal("MailClient.Desktop", SetupConstants.Aumid);
        Assert.Equal("MailClient.App.exe", SetupConstants.AppExeName);
        Assert.Equal("--quit", SetupConstants.QuitArgument);
    }
}
