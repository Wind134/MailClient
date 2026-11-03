namespace MailClient.Core.Updates;

/// <summary>当前运行的是哪种发行形态，决定「立即更新」怎么走。</summary>
public enum UpdateEdition
{
    /// <summary>exe 安装程序装出来的（应用目录里有安装清单）：可以自动下载并静默安装。</summary>
    Installed,

    /// <summary>MSIX 打包版（有包标识）：只能打开发布页。</summary>
    Packaged,

    /// <summary>绿色版 zip（免打包、没有安装清单）：只能打开发布页。</summary>
    Portable
}

public static class EditionDetector
{
    /// <summary>与安装程序写入的文件名保持一致（installer\MailClient.Setup.Core\SetupConstants）。</summary>
    public const string ManifestFileName = "install-manifest.txt";

    public const string SetupExeName = "MailClient.Setup.exe";

    public static UpdateEdition Detect(bool isPackaged, string baseDirectory)
    {
        if (isPackaged)
        {
            return UpdateEdition.Packaged;
        }

        try
        {
            if (File.Exists(Path.Combine(baseDirectory, ManifestFileName)) && File.Exists(Path.Combine(baseDirectory, SetupExeName)))
            {
                return UpdateEdition.Installed;
            }
        }
        catch (Exception)
        {
            // 读不了目录就按绿色版处理（只开网页，最保守）
        }

        return UpdateEdition.Portable;
    }

    public static bool CanSelfUpdate(UpdateEdition edition) => edition == UpdateEdition.Installed;

    public static string DisplayName(UpdateEdition edition) => edition switch
    {
        UpdateEdition.Installed => "exe 安装版",
        UpdateEdition.Packaged => "MSIX 版",
        _ => "绿色版"
    };
}