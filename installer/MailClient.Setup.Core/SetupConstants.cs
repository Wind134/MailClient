namespace MailClient.Setup.Core;

/// <summary>安装程序与应用共同约定的名字。AUMID 必须和 MailClient.App 里的 AppIdentity.UnpackagedAumid 一致。</summary>
public static class SetupConstants
{
    public const string ProductName = "邮件";
    public const string Publisher = "MailClient";
    public const string AppExeName = "MailClient.App.exe";
    public const string SetupExeName = "MailClient.Setup.exe";
    public const string Aumid = "MailClient.Desktop";
    public const string UninstallKeyName = "MailClient";
    public const string ManifestFileName = "install-manifest.txt";
    /// <summary>安装程序自己用到的文件清单（构建时生成，随负载安装）。卸载程序运行时不能删这些，交给收尾批处理。</summary>
    public const string SetupFilesListName = "setup-files.txt";
    public const string QuitArgument = "--quit";
    public const string MsixPackageNamePrefix = "49635C82-16E9-4745-B089-82337985CB02_";
}
