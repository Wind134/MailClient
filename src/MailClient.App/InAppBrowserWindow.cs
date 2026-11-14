using MailClient.Core.Links;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Windows.Graphics;
using Windows.System;

namespace MailClient_App;

/// <summary>
/// 软件内置浏览器：点邮件里的链接时（设置 → 链接 → 软件内置浏览器）在这里打开。
/// 标题栏显示页面标题与网址；工具栏有 后退 / 前进 / 刷新、网址栏和「在系统浏览器中打开」。
/// 只允许 http / https；mailto 交给写信窗口；javascript: file: 等其余协议一律拦截。
/// </summary>
internal sealed class InAppBrowserWindow : Window
{
    private readonly WebView2 _web = new();
    private readonly Button _back = new() { Content = "\uE72B", FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets") };
    private readonly Button _forward = new() { Content = "\uE72A", FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets") };
    private readonly Button _refresh = new() { Content = "\uE72C", FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets") };
    private readonly Button _external = new() { Content = "在系统浏览器中打开" };
    private readonly TextBox _address = new() { IsReadOnly = true, VerticalAlignment = VerticalAlignment.Center };
    private readonly ProgressBar _progress = new() { IsIndeterminate = true, Visibility = Visibility.Collapsed, VerticalAlignment = VerticalAlignment.Top };
    private readonly TextBlock _status = new() { Visibility = Visibility.Collapsed, Margin = new Thickness(12, 6, 12, 6), TextWrapping = TextWrapping.Wrap };
    private Uri? _pending;
    private Uri? _current;
    private bool _ready;
    private bool _closed;

    /// <summary>页面里点了 mailto: 链接。</summary>
    public event Action<MailtoData>? ComposeRequested;

    public InAppBrowserWindow()
    {
        Title = "软件内置浏览器";
        try
        {
            SystemBackdrop = new Microsoft.UI.Xaml.Media.DesktopAcrylicBackdrop();
        }
        catch
        {
            // 不支持系统背景时保持默认纯色
        }

        Content = BuildContent();
        try
        {
            AppWindow.SetIcon(AppIdentity.AssetPath("AppIcon.ico"));
            AppWindow.Resize(new SizeInt32(1100, 780));
        }
        catch
        {
            // 图标 / 尺寸设置失败不影响使用
        }

        Closed += (_, _) =>
        {
            _closed = true;
            try
            {
                _web.Close();
            }
            catch
            {
                // 已释放
            }
        };
        _ = InitializeAsync();
    }

    /// <summary>当前网址（测试 / 诊断用）。</summary>
    public Uri? CurrentUri => _current ?? _pending;

    /// <summary>导航到网址（WebView2 初始化完成前先记下来）。</summary>
    public void Navigate(Uri uri)
    {
        _pending = uri;
        _address.Text = uri.AbsoluteUri;
        Title = uri.AbsoluteUri;
        if (_ready && _web.CoreWebView2 is { } core)
        {
            _pending = null;
            core.Navigate(uri.AbsoluteUri);
        }
    }

    private UIElement BuildContent()
    {
        var auto = AutomationProperties.AutomationIdProperty;
        _back.SetValue(auto, "InAppBrowserBack");
        _forward.SetValue(auto, "InAppBrowserForward");
        _refresh.SetValue(auto, "InAppBrowserRefresh");
        _external.SetValue(auto, "InAppBrowserOpenExternal");
        _address.SetValue(auto, "InAppBrowserAddress");
        _web.SetValue(auto, "InAppBrowserWebView");
        _back.SetValue(AutomationProperties.NameProperty, "后退");
        _forward.SetValue(AutomationProperties.NameProperty, "前进");
        _refresh.SetValue(AutomationProperties.NameProperty, "刷新");
        _address.SetValue(AutomationProperties.NameProperty, "网址");
        Microsoft.UI.Xaml.Controls.ToolTipService.SetToolTip(_back, "后退");
        Microsoft.UI.Xaml.Controls.ToolTipService.SetToolTip(_forward, "前进");
        Microsoft.UI.Xaml.Controls.ToolTipService.SetToolTip(_refresh, "刷新");
        _back.IsEnabled = false;
        _forward.IsEnabled = false;

        _back.Click += (_, _) =>
        {
            if (_web.CoreWebView2?.CanGoBack == true)
            {
                _web.CoreWebView2.GoBack();
            }
        };
        _forward.Click += (_, _) =>
        {
            if (_web.CoreWebView2?.CanGoForward == true)
            {
                _web.CoreWebView2.GoForward();
            }
        };
        _refresh.Click += (_, _) => _web.CoreWebView2?.Reload();
        _external.Click += async (_, _) =>
        {
            var uri = _current ?? _pending;
            if (uri is not null && uri.Scheme is "http" or "https")
            {
                await Launcher.LaunchUriAsync(uri);
            }
        };

        var toolbar = new Grid { ColumnSpacing = 8, Padding = new Thickness(12, 8, 12, 8) };
        toolbar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        toolbar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        toolbar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        toolbar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        toolbar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(_back, 0);
        Grid.SetColumn(_forward, 1);
        Grid.SetColumn(_refresh, 2);
        Grid.SetColumn(_address, 3);
        Grid.SetColumn(_external, 4);
        toolbar.Children.Add(_back);
        toolbar.Children.Add(_forward);
        toolbar.Children.Add(_refresh);
        toolbar.Children.Add(_address);
        toolbar.Children.Add(_external);

        var body = new Grid();
        body.Children.Add(_web);
        body.Children.Add(_progress);

        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        Grid.SetRow(toolbar, 0);
        Grid.SetRow(_status, 1);
        Grid.SetRow(body, 2);
        root.Children.Add(toolbar);
        root.Children.Add(_status);
        root.Children.Add(body);
        return root;
    }

    private async Task InitializeAsync()
    {
        try
        {
            await _web.EnsureCoreWebView2Async();
            if (_closed)
            {
                return;
            }

            var core = _web.CoreWebView2;
            // 这是个普通浏览窗口（要跑页面脚本），但不开开发者工具 / 密码与表单自动保存。
            core.Settings.AreDevToolsEnabled = false;
            core.Settings.IsPasswordAutosaveEnabled = false;
            core.Settings.IsGeneralAutofillEnabled = false;

            core.NavigationStarting += (_, args) =>
            {
                var route = LinkRouter.RouteInternalBrowser(args.Uri);
                switch (route.Action)
                {
                    case LinkAction.Compose when route.Mailto is { } mailto:
                        args.Cancel = true;
                        ComposeRequested?.Invoke(mailto);
                        break;
                    case LinkAction.Allow:
                        _progress.Visibility = Visibility.Visible;
                        HideStatus();
                        break;
                    default:
                        args.Cancel = true;
                        ShowStatus($"已阻止打开不支持或不安全的链接（{route.BlockedScheme ?? "未知"}:）");
                        break;
                }
            };
            core.NewWindowRequested += (_, args) =>
            {
                // 页面想开新窗口（target=_blank / window.open）：留在这个窗口里打开，不弹出系统浏览器
                args.Handled = true;
                var route = LinkRouter.RouteInternalBrowser(args.Uri);
                if (route.Action == LinkAction.Allow && route.Uri is { } uri)
                {
                    core.Navigate(uri.AbsoluteUri);
                }
                else if (route.Action == LinkAction.Compose && route.Mailto is { } mailto)
                {
                    ComposeRequested?.Invoke(mailto);
                }
                else
                {
                    ShowStatus($"已阻止打开不支持或不安全的链接（{route.BlockedScheme ?? "未知"}:）");
                }
            };
            core.NavigationCompleted += (_, args) =>
            {
                _progress.Visibility = Visibility.Collapsed;
                if (!args.IsSuccess && args.WebErrorStatus != Microsoft.Web.WebView2.Core.CoreWebView2WebErrorStatus.OperationCanceled)
                {
                    ShowStatus($"页面加载失败（{args.WebErrorStatus}）。可以点「刷新」重试，或用「在系统浏览器中打开」。");
                }

                UpdateNavigationState();
            };
            core.SourceChanged += (_, _) => UpdateNavigationState();
            core.HistoryChanged += (_, _) => UpdateNavigationState();
            core.DocumentTitleChanged += (_, _) => UpdateNavigationState();

            _ready = true;
            if (_pending is { } uri)
            {
                _pending = null;
                core.Navigate(uri.AbsoluteUri);
            }
        }
        catch (Exception ex)
        {
            ShowStatus("无法启动内置浏览器（缺少 WebView2 运行时？）：" + ex.Message + "。请改用「系统默认浏览器」。");
        }
    }

    private void UpdateNavigationState()
    {
        if (_closed || _web.CoreWebView2 is not { } core)
        {
            return;
        }

        _back.IsEnabled = core.CanGoBack;
        _forward.IsEnabled = core.CanGoForward;
        if (Uri.TryCreate(core.Source, UriKind.Absolute, out var current) && current.Scheme is "http" or "https")
        {
            _current = current;
            _address.Text = current.AbsoluteUri;
            var title = core.DocumentTitle;
            Title = string.IsNullOrWhiteSpace(title) || string.Equals(title, current.AbsoluteUri, StringComparison.Ordinal)
                ? current.AbsoluteUri
                : $"{title} — {current.AbsoluteUri}";
        }
    }

    private void ShowStatus(string text)
    {
        _status.Text = text;
        _status.Visibility = Visibility.Visible;
    }

    private void HideStatus() => _status.Visibility = Visibility.Collapsed;
}
