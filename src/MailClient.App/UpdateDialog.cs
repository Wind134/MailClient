using MailClient.Core.Updates;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace MailClient_App;

/// <summary>「发现新版本」对话框：版本、更新说明；立即更新 / 稍后提醒 / 跳过此版本；exe 安装版可在框里下载并安装。</summary>
internal sealed class UpdateDialog
{
    private enum State { Idle, Downloading, Launching, Error }

    private readonly UpdateService _service;
    private readonly ReleaseInfo _release;
    private readonly Action _requestQuit;
    private readonly bool _canSelfUpdate;
    private readonly ContentDialog _dialog;
    private readonly FrameworkElement _notesArea;
    private readonly StackPanel _progressArea;
    private readonly TextBlock _progressTitle;
    private readonly ProgressBar _bar;
    private readonly TextBlock _progressText;
    private readonly TextBlock _errorText;
    private State _state = State.Idle;
    private CancellationTokenSource? _cts;
    private long _lastBytes;
    private long _lastTick;
    private double _speed;

    private UpdateDialog(XamlRoot xamlRoot, UpdateService service, ReleaseInfo release, Action requestQuit)
    {
        _service = service;
        _release = release;
        _requestQuit = requestQuit;
        _canSelfUpdate = EditionDetector.CanSelfUpdate(service.Edition) && UpdateAssets.SelectInstaller(release) is not null;

        var installer = UpdateAssets.SelectInstaller(release);
        var meta = $"当前版本 {UpdateVersion.Display(service.CurrentVersion)}";
        if (release.PublishedAt is { } published)
        {
            meta += $"  ·  发布于 {published.ToLocalTime():yyyy-MM-dd}";
        }

        if (installer is { Size: > 0 })
        {
            meta += $"  ·  安装包 {installer.Size / 1048576.0:F0} MB";
        }

        var header = new StackPanel { Spacing = 2 };
        header.Children.Add(new TextBlock
        {
            Text = $"邮件 {UpdateVersion.Display(release.Version)}",
            Style = (Style)Application.Current.Resources["SubtitleTextBlockStyle"]
        });
        header.Children.Add(Caption(meta));

        _notesArea = BuildNotes(release);

        _progressTitle = new TextBlock { Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"] };
        _bar = new ProgressBar { Minimum = 0, Maximum = 100, IsIndeterminate = true, HorizontalAlignment = HorizontalAlignment.Stretch };
        AutomationProperties.SetAutomationId(_bar, "UpdateProgressBar");
        _progressText = Caption(string.Empty);
        AutomationProperties.SetAutomationId(_progressText, "UpdateProgressText");
        _progressArea = new StackPanel { Spacing = 10, Visibility = Visibility.Collapsed, Padding = new Thickness(0, 12, 0, 8) };
        _progressArea.Children.Add(_progressTitle);
        _progressArea.Children.Add(_bar);
        _progressArea.Children.Add(_progressText);

        _errorText = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Foreground = (Brush)Application.Current.Resources["SystemFillColorCriticalBrush"],
            Visibility = Visibility.Collapsed
        };
        AutomationProperties.SetAutomationId(_errorText, "UpdateErrorText");

        var note = Caption(service.Edition switch
        {
            UpdateEdition.Installed when _canSelfUpdate => "「立即更新」会下载安装包，校验 SHA256 通过后静默安装，并自动重新打开「邮件」。你的邮件和设置不会被改动。",
            UpdateEdition.Packaged => "你使用的是 MSIX 版，不能在软件内自动更新：点「前往下载页」在浏览器里下载并安装新版本。",
            UpdateEdition.Installed => "这个发布里没有找到安装程序，请点「前往下载页」查看。",
            _ => "你使用的是绿色版（zip），不能自动更新：点「前往下载页」下载新的压缩包，替换现有文件夹即可（邮件数据不受影响）。"
        });
        AutomationProperties.SetAutomationId(note, "UpdateEditionNote");

        var content = new StackPanel { Spacing = 14, MinWidth = 500 };
        content.Children.Add(header);
        content.Children.Add(_notesArea);
        content.Children.Add(_progressArea);
        content.Children.Add(_errorText);
        content.Children.Add(note);
        _editionNote = note;

        _dialog = new ContentDialog
        {
            XamlRoot = xamlRoot,
            Title = "发现新版本",
            Content = content,
            PrimaryButtonText = _canSelfUpdate ? "立即更新" : "前往下载页",
            SecondaryButtonText = "跳过此版本",
            CloseButtonText = "稍后提醒",
            DefaultButton = ContentDialogButton.Primary
        };
        _dialog.Resources["ContentDialogMaxWidth"] = 600d;
        AutomationProperties.SetAutomationId(_dialog, "UpdateDialog");
        _dialog.PrimaryButtonClick += OnPrimary;
        _dialog.SecondaryButtonClick += OnSecondary;
        _dialog.Closing += OnClosing;
    }

    private readonly TextBlock _editionNote;

    public static async Task ShowAsync(XamlRoot xamlRoot, UpdateService service, ReleaseInfo release, Action requestQuit)
    {
        var d = new UpdateDialog(xamlRoot, service, release, requestQuit);
        await d._dialog.ShowAsync();
        d._cts?.Dispose();
    }

    // ───────── 按钮 ─────────

    private void OnPrimary(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        args.Cancel = true; // 对话框保持打开，由下面的流程决定何时关
        if (_state is State.Downloading or State.Launching)
        {
            return;
        }

        if (!_canSelfUpdate)
        {
            _service.OpenReleasePage(_release);
            _service.Pending = null;
            sender.Hide();
            return;
        }

        _ = DownloadAndInstallAsync();
    }

    private void OnSecondary(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        if (_state == State.Error)
        {
            _service.OpenReleasePage(_release);
            return;
        }

        _service.SkipVersion(_release);
    }

    private void OnClosing(ContentDialog sender, ContentDialogClosingEventArgs args)
    {
        if (_state == State.Launching)
        {
            args.Cancel = true; // 安装程序已经启动，不能再取消
            return;
        }

        if (_state == State.Downloading)
        {
            try { _cts?.Cancel(); } catch (ObjectDisposedException) { }
        }

        if (args.Result == ContentDialogResult.None)
        {
            _service.Pending = null; // 稍后提醒：下一轮检查（6 小时后）会再提示
        }
    }

    // ───────── 下载并安装 ─────────

    private async Task DownloadAndInstallAsync()
    {
        _state = State.Downloading;
        _cts?.Dispose();
        _cts = new CancellationTokenSource();
        _lastBytes = 0;
        _lastTick = Environment.TickCount64;
        _speed = 0;

        _dialog.Title = "正在更新";
        _notesArea.Visibility = Visibility.Collapsed;
        _editionNote.Visibility = Visibility.Collapsed;
        _errorText.Visibility = Visibility.Collapsed;
        _progressArea.Visibility = Visibility.Visible;
        _progressTitle.Text = $"正在下载 邮件 {UpdateVersion.Display(_release.Version)}";
        _bar.IsIndeterminate = true;
        _bar.Value = 0;
        _progressText.Text = "正在连接…";
        _dialog.IsPrimaryButtonEnabled = false;
        _dialog.PrimaryButtonText = "正在下载…";
        _dialog.SecondaryButtonText = string.Empty;
        _dialog.CloseButtonText = "取消";
        _dialog.DefaultButton = ContentDialogButton.Close;

        try
        {
            var progress = new Progress<DownloadProgress>(OnProgress);
            var downloaded = await _service.DownloadAsync(_release, progress, _cts.Token);

            _state = State.Launching;
            _bar.IsIndeterminate = true;
            _progressTitle.Text = "准备安装";
            _progressText.Text = (downloaded.Verified ? "已下载，SHA256 校验通过。" : "已下载（这个发布没有校验和文件，未校验）。")
                + "正在启动安装程序，「邮件」会自动关闭并在更新后重新打开…";
            _dialog.PrimaryButtonText = "正在安装…";
            _dialog.CloseButtonText = string.Empty;

            if (!_service.LaunchInstaller(downloaded.Path))
            {
                throw new UpdateException(UpdateErrorKind.DownloadFailed, "无法启动安装程序。");
            }

            await Task.Delay(1500);
            _requestQuit();
        }
        catch (OperationCanceledException)
        {
            // 用户点了「取消」，对话框已经在关闭
        }
        catch (Exception ex)
        {
            ShowError(ex is UpdateException ? ex.Message : "更新失败：" + ex.Message);
        }
    }

    private void OnProgress(DownloadProgress p)
    {
        if (_state != State.Downloading)
        {
            return;
        }

        var now = Environment.TickCount64;
        if (now - _lastTick >= 500)
        {
            var instant = (p.Received - _lastBytes) * 1000.0 / (now - _lastTick);
            _speed = _speed <= 0 ? instant : _speed * 0.6 + instant * 0.4;
            _lastBytes = p.Received;
            _lastTick = now;
        }

        var received = p.Received / 1048576.0;
        if (p.Fraction is { } f)
        {
            _bar.IsIndeterminate = false;
            _bar.Value = f * 100;
            _progressText.Text = $"{received:F1} / {p.Total!.Value / 1048576.0:F1} MB（{f * 100:F0}%）" + SpeedText();
        }
        else
        {
            _bar.IsIndeterminate = true;
            _progressText.Text = $"已下载 {received:F1} MB" + SpeedText();
        }
    }

    private string SpeedText() => _speed > 0 ? $"  ·  {_speed / 1048576.0:F1} MB/s" : string.Empty;

    private void ShowError(string message)
    {
        _state = State.Error;
        _progressArea.Visibility = Visibility.Collapsed;
        _notesArea.Visibility = Visibility.Visible;
        _dialog.Title = "更新失败";
        _errorText.Text = message;
        _errorText.Visibility = Visibility.Visible;
        _dialog.IsPrimaryButtonEnabled = true;
        _dialog.PrimaryButtonText = "重试";
        _dialog.SecondaryButtonText = "前往下载页";
        _dialog.CloseButtonText = "关闭";
        _dialog.DefaultButton = ContentDialogButton.Primary;
    }

    // ───────── 更新说明 ─────────

    private static TextBlock Caption(string text) => new()
    {
        Text = text,
        TextWrapping = TextWrapping.Wrap,
        Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
        Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"]
    };

    private static FrameworkElement BuildNotes(ReleaseInfo release)
    {
        var blocks = MarkdownLite.Parse(release.Body);
        var stack = new StackPanel { Spacing = 6, Padding = new Thickness(14, 12, 14, 12) };
        if (blocks.Count == 0)
        {
            stack.Children.Add(Caption("这个版本没有填写更新说明。"));
        }

        foreach (var b in blocks)
        {
            switch (b.Kind)
            {
                case MarkdownBlockKind.Heading:
                    stack.Children.Add(new TextBlock
                    {
                        Text = b.Text,
                        TextWrapping = TextWrapping.Wrap,
                        FontWeight = FontWeights.SemiBold,
                        FontSize = b.Level <= 2 ? 15 : 14,
                        Margin = new Thickness(0, stack.Children.Count == 0 ? 0 : 8, 0, 0)
                    });
                    break;
                case MarkdownBlockKind.Bullet:
                    var row = new Grid { ColumnSpacing = 8 };
                    row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                    row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                    var dot = new TextBlock { Text = "•", Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"] };
                    var text = new TextBlock { Text = b.Text, TextWrapping = TextWrapping.Wrap };
                    Grid.SetColumn(text, 1);
                    row.Children.Add(dot);
                    row.Children.Add(text);
                    stack.Children.Add(row);
                    break;
                case MarkdownBlockKind.Code:
                    stack.Children.Add(new Border
                    {
                        Background = (Brush)Application.Current.Resources["ControlFillColorSecondaryBrush"],
                        CornerRadius = new CornerRadius(4),
                        Padding = new Thickness(10, 6, 10, 6),
                        Child = new TextBlock { Text = b.Text, FontFamily = new FontFamily("Consolas"), FontSize = 12, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true }
                    });
                    break;
                default:
                    stack.Children.Add(new TextBlock { Text = b.Text, TextWrapping = TextWrapping.Wrap });
                    break;
            }
        }

        var scroll = new ScrollViewer
        {
            MaxHeight = 260,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = stack
        };
        AutomationProperties.SetAutomationId(scroll, "UpdateReleaseNotes");
        return new Border
        {
            Background = (Brush)Application.Current.Resources["CardBackgroundFillColorDefaultBrush"],
            BorderBrush = (Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"],
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Child = scroll
        };
    }
}