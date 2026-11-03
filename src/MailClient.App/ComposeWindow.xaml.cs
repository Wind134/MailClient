using MailClient.ViewModels;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Graphics;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace MailClient_App;

public sealed partial class ComposeWindow : Window, IComposeWindow
{
    public ComposeWindow(ComposeViewModel viewModel)
    {
        ViewModel = viewModel;
        InitializeComponent();

        Title = viewModel.WindowTitle;
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(ComposeTitleBar);
        AppWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Tall;
        // AppWindow.Resize 用的是物理像素，按窗口 DPI 换算，否则高分屏上正文框会被压没
        var scale = GetDpiForWindow(WindowNative.GetWindowHandle(this)) / 96.0;
        AppWindow.Resize(new SizeInt32((int)(820 * scale), (int)(760 * scale)));
        // 直接关窗口 = 保留草稿（发送或点“丢弃”后 VM 已标记完成，不会再保存）
        Closed += async (_, _) => await ViewModel.FlushDraftAsync();
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    public ComposeViewModel ViewModel { get; }

    private void ShowCc_Click(object sender, RoutedEventArgs e) => ViewModel.ShowCc = true;

    /// <summary>带到前台（已最小化时先还原）；同一份草稿再次打开时调用。</summary>
    public new void Activate()
    {
        if (AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized } presenter)
        {
            presenter.Restore();
        }

        base.Activate();
    }

    public void OnClosed(Action callback) => Closed += (_, _) => callback();

    private bool _discardDialogOpen;

    private async void Discard_Click(object sender, RoutedEventArgs e)
    {
        // 有内容（或已保存过）的草稿：丢弃前先确认，丢弃后不可恢复
        if (ViewModel.NeedsDiscardConfirmation)
        {
            if (_discardDialogOpen || Content?.XamlRoot is null)
            {
                return;
            }

            _discardDialogOpen = true;
            try
            {
                var dialog = new ContentDialog
                {
                    XamlRoot = Content.XamlRoot,
                    Title = "丢弃这封草稿？",
                    Content = "草稿（包括它的附件副本）会被删除，无法恢复。若只是想稍后再写，直接关闭窗口即可保留草稿。",
                    PrimaryButtonText = "丢弃",
                    CloseButtonText = "取消",
                    DefaultButton = ContentDialogButton.Close
                };
                if (await dialog.ShowAsync() != ContentDialogResult.Primary)
                {
                    return;
                }
            }
            finally
            {
                _discardDialogOpen = false;
            }
        }

        await ViewModel.DiscardAsync();
        Close();
    }

    private async void AddAttachment_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FileOpenPicker();
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));
        picker.FileTypeFilter.Add("*");
        var files = await picker.PickMultipleFilesAsync();
        if (files is null)
        {
            return;
        }

        foreach (var file in files)
        {
            ViewModel.AddAttachment(file.Path);
        }
    }

    private void RemoveAttachment_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: ComposeAttachmentViewModel attachment })
        {
            ViewModel.RemoveAttachment(attachment);
        }
    }
}
