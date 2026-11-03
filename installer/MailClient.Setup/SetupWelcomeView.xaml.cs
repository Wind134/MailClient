using MailClient.Setup.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage.Pickers;

namespace MailClient.Setup;

/// <summary>欢迎页：安装位置（可更改）、桌面快捷方式、安装 / 取消。</summary>
public sealed partial class SetupWelcomeView : UserControl
{
    private string _installDir = SetupPaths.DefaultInstallDir;

    public SetupWelcomeView()
    {
        InitializeComponent();
        PathText.Text = _installDir;
    }

    public event Action? InstallRequested;

    public event Action? CancelRequested;

    public Func<IntPtr>? WindowHandleProvider { get; set; }

    public string InstallDir
    {
        get => _installDir;
        set
        {
            _installDir = value;
            PathText.Text = value;
            ToolTipService.SetToolTip(PathText, value);
        }
    }

    public bool DesktopShortcut
    {
        get => DesktopBox.IsChecked == true;
        set => DesktopBox.IsChecked = value;
    }

    public void SetSpaceInfo(long bytes)
    {
        SpaceText.Text = $"约需 {Math.Max(1, bytes / 1024 / 1024)} MB 磁盘空间 · 只为当前用户安装，不需要管理员权限";
    }

    public void SetNotice(string message, InfoBarSeverity severity = InfoBarSeverity.Informational)
    {
        Notice.Message = message;
        Notice.Severity = severity;
        Notice.IsOpen = true;
    }

    public void ShowError(string? message)
    {
        ErrorText.Text = message ?? string.Empty;
        ErrorText.Visibility = string.IsNullOrEmpty(message) ? Visibility.Collapsed : Visibility.Visible;
    }

    public void LockLocation()
    {
        // 升级安装固定在原位置，避免装出两份
        BrowseButton.IsEnabled = false;
    }

    private async void OnBrowse(object sender, RoutedEventArgs e)
    {
        try
        {
            var picker = new FolderPicker();
            picker.FileTypeFilter.Add("*");
            var hwnd = WindowHandleProvider?.Invoke() ?? IntPtr.Zero;
            WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
            var folder = await picker.PickSingleFolderAsync();
            if (folder is null)
            {
                return;
            }

            var chosen = folder.Path;
            // 选了一个已有内容的文件夹（又不是我们之前的安装）就在里面建一个 MailClient 子文件夹，别和别的文件混在一起
            try
            {
                var hasContent = Directory.EnumerateFileSystemEntries(chosen).Any();
                var isOurs = File.Exists(Path.Combine(chosen, SetupConstants.AppExeName));
                if (hasContent && !isOurs)
                {
                    chosen = Path.Combine(chosen, "MailClient");
                }
            }
            catch (Exception)
            {
                // 读不了就原样用，安装时会给出明确的错误
            }

            InstallDir = chosen;
            ShowError(SetupPaths.ValidateInstallDir(chosen));
        }
        catch (Exception ex)
        {
            ShowError("无法打开文件夹选择器：" + ex.Message);
        }
    }

    private void OnInstall(object sender, RoutedEventArgs e)
    {
        var problem = SetupPaths.ValidateInstallDir(_installDir);
        ShowError(problem);
        if (problem is null)
        {
            InstallRequested?.Invoke();
        }
    }

    private void OnCancel(object sender, RoutedEventArgs e) => CancelRequested?.Invoke();
}
