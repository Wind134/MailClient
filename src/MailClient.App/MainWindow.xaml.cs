using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Threading;
using MailClient.Core.Tray;
using MailClient.Core.Updates;
using MailClient.ViewModels;
using MailClient.ViewModels.AddAccount;
using MailClient_App.Controls;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Windows.Graphics;
using Windows.System;

namespace MailClient_App;

public sealed partial class MainWindow : Window
{
    private const double NarrowWidth = 760;
    private readonly Dictionary<MailboxNodeViewModel, NavigationViewItem> _navItems = new();
    private bool _syncingNav;
    private bool _syncingTabs;
    private bool _narrow;
    private double _listWidth = 380;
    private bool _webReady;
    private bool _webInitStarted;
    private bool _dialogOpen;
    private int _renderVersion;
    private bool _forceClose;
    private bool _closingWired;

    public MainWindow(ShellViewModel viewModel)
    {
        ViewModel = viewModel;
        try
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MailClient");
            Directory.CreateDirectory(dir);
            MailClient.Core.Logging.RotatingLog.Append(Path.Combine(dir, "startup.log"), DateTimeOffset.Now.ToString("O") + " mainwindow:before-InitializeComponent\n");
        }
        catch { }
        InitializeComponent();
        try
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MailClient");
            MailClient.Core.Logging.RotatingLog.Append(Path.Combine(dir, "startup.log"), DateTimeOffset.Now.ToString("O") + " mainwindow:after-InitializeComponent\n");
        }
        catch { }

        try
        {
            var uiCtx = new Microsoft.UI.Dispatching.DispatcherQueueSynchronizationContext(DispatcherQueue);
            // 设为 Current：OnLaunched 里 await InitializeAsync 的续体回到 UI，Title/列表变更才生效。
            SynchronizationContext.SetSynchronizationContext(uiCtx);
            ViewModel.AttachSynchronizationContext(uiCtx);
            ViewModel.List.AttachSynchronizationContext(uiCtx);
            ViewModel.Reading.AttachSynchronizationContext(uiCtx);
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MailClient");
            Directory.CreateDirectory(dir);
            MailClient.Core.Logging.RotatingLog.Append(Path.Combine(dir, "startup.log"), DateTimeOffset.Now.ToString("O") + " mainwindow:ui-marshal-attached\n");
        }
        catch (Exception ex)
        {
            try
            {
                var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MailClient");
                MailClient.Core.Logging.RotatingLog.Append(Path.Combine(dir, "startup.log"), DateTimeOffset.Now.ToString("O") + " mainwindow:ui-marshal-FAIL " + ex + "\n");
            }
            catch { }
        }

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        AppWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Tall;
        try
        {
            AppWindow.SetIcon(AppIdentity.AssetPath("AppIcon.ico"));
        }
        catch
        {
            // 图标缺失不影响启动
        }

        SetInitialSize();
        Splitter.Target = ListColumn;
        Splitter.DefaultWidth = 380;

        ViewModel.Tree.Rebuilt += (_, _) => RebuildNav();
        ViewModel.Tree.PropertyChanged += OnTreePropertyChanged;
        ViewModel.List.PropertyChanged += OnListPropertyChanged;
        ViewModel.Reading.PropertyChanged += OnReadingPropertyChanged;
        ViewModel.Reading.AttachmentSaveRequested += OnAttachmentSaveRequested;
        ViewModel.ComposeRequested += OnComposeRequested;
        ViewModel.PropertyChanged += OnShellPropertyChanged;
        ViewModel.ConfirmDraftDeletionAsync = ConfirmDeleteDraftAsync;
        // Ctrl+F：聚焦搜索框
        var findAccelerator = new KeyboardAccelerator { Key = VirtualKey.F, Modifiers = VirtualKeyModifiers.Control };
        findAccelerator.Invoked += (_, e) =>
        {
            SearchBox.Focus(FocusState.Keyboard);
            e.Handled = true;
        };
        if (Content is UIElement root)
        {
            root.KeyboardAccelerators.Add(findAccelerator);
        }

        WireCloseToTray();

        UpdateAddAccountStyle();
        UpdateEmptyPanels();
        // ItemsSource 改由代码挂接：x:Bind 在 Activated 的 Update_ 批里若 set_ItemsSource 抛 NRE，
        // 会中断同批 Title/Subtitle 等绑定（表现为标题空白 + 中栏空列表）。
        WireMessageListSource("ctor");
        Activated += OnWindowActivatedWireList;
    }

    private ObservableCollection<MessageRowViewModel>? _wiredMessages;
    private bool _rebuildingMessageItems;

    private void OnWindowActivatedWireList(object sender, WindowActivatedEventArgs args)
    {
        WireMessageListSource("activated");
    }

    /// <summary>
    /// 中栏列表不用 ItemsSource=ObservableCollection（Release+PublishTrimmed 下 CsWinRT
    /// set_ItemsSource 会在 GetAbiToProjectionVftblPtr 抛 NRE，连带 x:Bind 同批 Title 也被中断）。
    /// 改为 Items.Clear/Add，并订阅 CollectionChanged 保持同步。
    /// </summary>
    private void WireMessageListSource(string reason)
    {
        try
        {
            var messages = ViewModel.List.Messages;
            if (!ReferenceEquals(_wiredMessages, messages))
            {
                if (_wiredMessages is not null)
                {
                    _wiredMessages.CollectionChanged -= OnMessagesCollectionChanged;
                }

                _wiredMessages = messages;
                messages.CollectionChanged += OnMessagesCollectionChanged;
                // 确保不是 ItemsSource 模式，否则 Items.Add 不可用
                MessageListView.ItemsSource = null;
                RebuildMessageListItems(reason);
            }
            else
            {
                // 同一集合：属性变化时只同步标题；条目已由 CollectionChanged 维护
                SyncListHeaderTexts();
            }
        }
        catch (Exception)
        {
            try { SyncListHeaderTexts(); } catch { }
        }
    }

    private void OnMessagesCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (_rebuildingMessageItems)
        {
            return;
        }

        void apply()
        {
            try
            {
                MessageListView.ItemsSource = null;
                switch (e.Action)
                {
                    case NotifyCollectionChangedAction.Reset:
                        MessageListView.Items.Clear();
                        break;
                    case NotifyCollectionChangedAction.Add when e.NewItems is not null:
                        var insertAt = e.NewStartingIndex < 0 ? MessageListView.Items.Count : e.NewStartingIndex;
                        for (var i = 0; i < e.NewItems.Count; i++)
                        {
                            MessageListView.Items.Insert(insertAt + i, e.NewItems[i]);
                        }
                        break;
                    case NotifyCollectionChangedAction.Remove when e.OldItems is not null:
                        foreach (var item in e.OldItems)
                        {
                            MessageListView.Items.Remove(item);
                        }
                        break;
                    case NotifyCollectionChangedAction.Replace when e.NewItems is not null && e.OldItems is not null:
                        for (var i = 0; i < e.OldItems.Count; i++)
                        {
                            MessageListView.Items.Remove(e.OldItems[i]);
                        }
                        var at = e.NewStartingIndex < 0 ? MessageListView.Items.Count : e.NewStartingIndex;
                        for (var i = 0; i < e.NewItems.Count; i++)
                        {
                            MessageListView.Items.Insert(at + i, e.NewItems[i]);
                        }
                        break;
                    case NotifyCollectionChangedAction.Move:
                        RebuildMessageListItems("col:Move");
                        return;
                    default:
                        RebuildMessageListItems("col:" + e.Action);
                        return;
                }

                SyncListHeaderTexts();
            }
            catch (Exception)
            {
                RebuildMessageListItems("col-fallback");
            }
        }

        if (MessageListView.DispatcherQueue is not null && !MessageListView.DispatcherQueue.HasThreadAccess)
        {
            MessageListView.DispatcherQueue.TryEnqueue(apply);
            return;
        }

        apply();
    }

    private void RebuildMessageListItems(string reason)
    {
        if (_rebuildingMessageItems)
        {
            return;
        }

        _rebuildingMessageItems = true;
        try
        {
            MessageListView.ItemsSource = null;
            MessageListView.Items.Clear();
            var messages = ViewModel.List.Messages;
            foreach (var row in messages)
            {
                MessageListView.Items.Add(row);
            }

            if (ViewModel.List.SelectedMessage is { } selected)
            {
                MessageListView.SelectedItem = selected;
            }

            SyncListHeaderTexts();
        }
        catch (Exception)
        {
            try { SyncListHeaderTexts(); } catch { }
        }
        finally
        {
            _rebuildingMessageItems = false;
        }
    }

    private void SyncListHeaderTexts()
    {
        if (ListTitleText is not null && ListTitleText.Text != ViewModel.List.Title)
        {
            ListTitleText.Text = ViewModel.List.Title ?? string.Empty;
        }

        if (ListSubtitleText is not null && ListSubtitleText.Text != ViewModel.List.Subtitle)
        {
            ListSubtitleText.Text = ViewModel.List.Subtitle ?? string.Empty;
        }
    }

    public ShellViewModel ViewModel { get; }

    // ───────── 左栏 ─────────

    private void TitleBar_PaneToggleRequested(TitleBar sender, object args)
    {
        // 窄屏 / 紧凑模式下 Auto 会自动收起；这里允许用户手动折叠 / 展开。
        NavView.PaneDisplayMode = NavView.PaneDisplayMode == NavigationViewPaneDisplayMode.Auto
            ? NavigationViewPaneDisplayMode.LeftMinimal
            : NavigationViewPaneDisplayMode.Auto;
        if (NavView.PaneDisplayMode == NavigationViewPaneDisplayMode.LeftMinimal)
        {
            NavView.IsPaneOpen = false;
        }
    }

    private void RebuildNav()
    {
        _navItems.Clear();
        NavView.MenuItems.Clear();

        NavView.MenuItems.Add(Header(UiStrings.Favorites));
        foreach (var fav in ViewModel.Tree.Favorites)
        {
            NavView.MenuItems.Add(CreateItem(fav));
        }

        foreach (var account in ViewModel.Tree.Accounts)
        {
            NavView.MenuItems.Add(new NavigationViewItemSeparator());
            var accountItem = CreateItem(account);
            accountItem.SelectsOnInvoked = false;
            accountItem.IsExpanded = true;
            foreach (var folder in account.Children)
            {
                accountItem.MenuItems.Add(CreateItem(folder));
            }

            NavView.MenuItems.Add(accountItem);
        }

        SyncNavSelection();
    }

    private static NavigationViewItemHeader Header(string text) => new() { Content = text };

    private NavigationViewItem CreateItem(MailboxNodeViewModel node)
    {
        var item = new NavigationViewItem
        {
            Content = node.Title,
            Tag = node,
            Icon = BuildIcon(node),
            InfoBadge = BuildBadge(node)
        };
        if (node.Kind == MailboxNodeKind.Account)
        {
            item.ContextFlyout = BuildAccountFlyout(node);
        }

        node.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MailboxNodeViewModel.UnreadCount))
            {
                item.InfoBadge = BuildBadge(node);
            }
            else if (e.PropertyName == nameof(MailboxNodeViewModel.ShowBadge))
            {
                item.InfoBadge = BuildBadge(node);
            }
            else if (e.PropertyName == nameof(MailboxNodeViewModel.ColorHex))
            {
                item.Icon = BuildIcon(node);
            }
        };
        _navItems[node] = item;
        return item;
    }

    /// <summary>账号节点：彩色圆底 + 首字母（每个账号一眼可辨）；其他节点沿用主题色字体图标。</summary>
    private IconElement BuildIcon(MailboxNodeViewModel node)
    {
        if (node.Kind == MailboxNodeKind.Account)
        {
            var color = Converters.ParseColor(node.ColorHex) ?? Windows.UI.Color.FromArgb(255, 0x7A, 0x75, 0x74);
            var letter = string.IsNullOrEmpty(node.AvatarLetter) ? "?" : node.AvatarLetter;
            var scale = Math.Max(1.0, (Content?.XamlRoot?.RasterizationScale) ?? 2.0);
            return LetterAvatar.Create(letter, color, Math.Max(2.0, scale));
        }

        return new FontIcon { Glyph = node.Glyph };
    }

    /// <summary>
    /// 初始窗口大小：AppWindow.Resize 用物理像素，所以按 DPI 把 1280×800（逻辑像素）换算；
    /// 再限制在当前显示器工作区内，并居中。
    /// </summary>
    private void SetInitialSize()
    {
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        var scale = GetDpiForWindow(hwnd) / 96.0;
        var area = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
        var width = Math.Min((int)(1280 * scale), area.Width);
        var height = Math.Min((int)(800 * scale), area.Height);
        AppWindow.Resize(new SizeInt32(width, height));
        AppWindow.Move(new PointInt32(area.X + (area.Width - width) / 2, area.Y + (area.Height - height) / 2));
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    private MenuFlyout BuildAccountFlyout(MailboxNodeViewModel node)
    {
        var flyout = new MenuFlyout();
        var accountId = node.AccountId;
        if (accountId is null)
        {
            return flyout;
        }

        var rename = new MenuFlyoutItem { Text = "重命名…", Icon = new FontIcon { Glyph = "\uE70F" } };
        rename.Click += async (_, _) => await RenameAccountAsync(accountId.Value, node.Title, node.EmailAddress);
        var sync = new MenuFlyoutItem { Text = "同步此账号", Icon = new FontIcon { Glyph = "\uE72C" } };
        sync.Click += (_, _) => _ = ViewModel.SyncAsync(accountId, userInitiated: true);
        sync.IsEnabled = !node.IsDemo && ViewModel.CanSync;
        var remove = new MenuFlyoutItem { Text = "移除账号…", Icon = new FontIcon { Glyph = "\uE74D" } };
        remove.Click += async (_, _) => await ConfirmRemoveAccountAsync(accountId.Value, node.Title);

        flyout.Items.Add(rename);
        if (!node.IsDemo)
        {
            flyout.Items.Add(sync);
        }

        flyout.Items.Add(remove);
        return flyout;
    }

    private static InfoBadge? BuildBadge(MailboxNodeViewModel node)
        => node.UnreadCount > 0 && node.ShowBadge && node.Kind != MailboxNodeKind.Account
            ? new InfoBadge { Value = node.UnreadCount }
            : null;

    private void SyncNavSelection()
    {
        var node = ViewModel.Tree.SelectedNode;
        if (node is not null && _navItems.TryGetValue(node, out var item))
        {
            _syncingNav = true;
            try
            {
                NavView.SelectedItem = item;
            }
            finally
            {
                _syncingNav = false;
            }
        }
    }

    private void OnTreePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MailboxTreeViewModel.SelectedNode))
        {
            SyncNavSelection();
        }
    }

    private void NavView_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (_syncingNav)
        {
            return;
        }

        if (args.SelectedItem is NavigationViewItem { Tag: MailboxNodeViewModel { IsSelectable: true } node })
        {
            ViewModel.Tree.SelectedNode = node;
            // 窄屏时选了文件夹就收起抽屉
            if (_narrow)
            {
                NavView.IsPaneOpen = false;
            }
        }
    }

    // ───────── 中栏 ─────────

    private void SearchBox_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        if (args.Reason == AutoSuggestionBoxTextChangeReason.UserInput)
        {
            ViewModel.List.SearchText = sender.Text;
            sender.ItemsSource = ViewModel.GetSearchSuggestions(sender.Text);
        }
    }

    /// <summary>聚焦且输入为空时，把最近的搜索列出来供选择。</summary>
    private void SearchBox_GotFocus(object sender, RoutedEventArgs e)
    {
        var suggestions = ViewModel.GetSearchSuggestions(SearchBox.Text);
        SearchBox.ItemsSource = suggestions;
        if (string.IsNullOrEmpty(SearchBox.Text) && suggestions.Count > 0)
        {
            SearchBox.IsSuggestionListOpen = true;
        }
    }

    private void SearchBox_SuggestionChosen(AutoSuggestBox sender, AutoSuggestBoxSuggestionChosenEventArgs args)
    {
        if (args.SelectedItem is string text)
        {
            sender.Text = text;
            ViewModel.List.SearchText = text;
        }
    }

    private void SearchBox_QuerySubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args)
    {
        if (args.ChosenSuggestion is string chosen)
        {
            sender.Text = chosen;
        }

        ViewModel.List.SearchText = sender.Text;
        ViewModel.CommitSearch();
    }

    /// <summary>Esc：清空搜索并恢复当前文件夹视图。</summary>
    private async void SearchBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Escape && (!string.IsNullOrEmpty(SearchBox.Text) || ViewModel.List.IsSearching))
        {
            e.Handled = true;
            SearchBox.Text = string.Empty;
            await ViewModel.ClearSearchAsync();
        }
    }

    private void CategoryBar_SelectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        if (_syncingTabs)
        {
            return;
        }

        var index = sender.Items.IndexOf(sender.SelectedItem);
        if (index >= 0)
        {
            ViewModel.List.SelectedCategoryIndex = index;
        }
    }

    private void OnListPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(MessageListViewModel.SelectedCategoryIndex):
                var index = ViewModel.List.SelectedCategoryIndex;
                if (index >= 0 && index < CategoryBar.Items.Count && CategoryBar.Items.IndexOf(CategoryBar.SelectedItem) != index)
                {
                    _syncingTabs = true;
                    try
                    {
                        CategoryBar.SelectedItem = CategoryBar.Items[index];
                    }
                    finally
                    {
                        _syncingTabs = false;
                    }
                }

                break;
            case nameof(MessageListViewModel.SelectedMessage):
                if (!_rebuildingMessageItems &&
                    !ReferenceEquals(MessageListView.SelectedItem, ViewModel.List.SelectedMessage))
                {
                    MessageListView.SelectedItem = ViewModel.List.SelectedMessage;
                }
                ApplyLayout();
                break;
            case nameof(MessageListViewModel.SearchText):
                if (SearchBox.Text != ViewModel.List.SearchText)
                {
                    SearchBox.Text = ViewModel.List.SearchText;
                }

                break;
            case nameof(MessageListViewModel.IsEmpty):
            case nameof(MessageListViewModel.ShowCategoryBar):
            case nameof(MessageListViewModel.ShowCategoryTabs):
            case nameof(MessageListViewModel.Title):
            case nameof(MessageListViewModel.Subtitle):
                WireMessageListSource("list-pc:" + (e.PropertyName ?? ""));
                UpdateEmptyPanels();
                break;
        }
    }

    // ───────── 响应式布局 ─────────

    private void ContentGrid_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        var narrow = e.NewSize.Width < NarrowWidth;
        if (narrow != _narrow)
        {
            _narrow = narrow;
            ApplyLayout();
        }
    }

    /// <summary>宽屏：三栏并排；窄屏：中栏 / 右栏二选一，右栏带“返回列表”。</summary>
    private void ApplyLayout()
    {
        if (!_narrow)
        {
            if (ListColumn.Width.Value < 1)
            {
                ListColumn.Width = new GridLength(_listWidth);
            }

            ListColumn.MinWidth = 280;
            ReadColumn.MinWidth = 360;
            Splitter.Visibility = Visibility.Visible;
            Grid.SetColumn(ListPane, 0);
            Grid.SetColumnSpan(ListPane, 1);
            Grid.SetColumn(ReadPane, 2);
            Grid.SetColumnSpan(ReadPane, 1);
            ListPane.Visibility = Visibility.Visible;
            ReadPane.Visibility = Visibility.Visible;
            BackButton.Visibility = Visibility.Collapsed;
            return;
        }

        if (ListColumn.ActualWidth > 1)
        {
            _listWidth = ListColumn.ActualWidth;
        }

        ListColumn.MinWidth = 0;
        ReadColumn.MinWidth = 0;
        ListColumn.Width = new GridLength(0);
        Splitter.Visibility = Visibility.Collapsed;
        Grid.SetColumn(ListPane, 0);
        Grid.SetColumnSpan(ListPane, 3);
        Grid.SetColumn(ReadPane, 0);
        Grid.SetColumnSpan(ReadPane, 3);
        var showReading = ViewModel.List.SelectedMessage is not null;
        ListPane.Visibility = showReading ? Visibility.Collapsed : Visibility.Visible;
        ReadPane.Visibility = showReading ? Visibility.Visible : Visibility.Collapsed;
        BackButton.Visibility = showReading ? Visibility.Visible : Visibility.Collapsed;
    }

    private void BackButton_Click(object sender, RoutedEventArgs e) => ViewModel.List.SelectedMessage = null;

    // ───────── 右栏：正文渲染 ─────────

    private void OnReadingPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ReadingPaneViewModel.DisplayBodyHtml)
            or nameof(ReadingPaneViewModel.HasMessage)
            or nameof(ReadingPaneViewModel.IsBodyLoading)
            or nameof(ReadingPaneViewModel.BodyText))
        {
            _ = RenderBodyAsync();
        }
    }

    private async void OnAttachmentSaveRequested(object? sender, AttachmentSaveRequest e)
    {
        try
        {
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            var picker = new Windows.Storage.Pickers.FileSavePicker();
            WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
            picker.SuggestedFileName = e.SuggestedFileName;
            var ext = System.IO.Path.GetExtension(e.SuggestedFileName);
            if (string.IsNullOrWhiteSpace(ext))
            {
                ext = ".bin";
            }

            picker.FileTypeChoices.Add("附件", new List<string> { ext });
            picker.DefaultFileExtension = ext;
            var file = await picker.PickSaveFileAsync();
            if (file is null)
            {
                return;
            }

            await ViewModel.Reading.SaveAttachmentToAsync(e.AttachmentId, file.Path);
            ViewModel.IsStatusError = false;
            ViewModel.StatusMessage = $"已保存：{file.Name}";
            ViewModel.IsStatusOpen = true;
        }
        catch (Exception ex)
        {
            ViewModel.IsStatusError = true;
            ViewModel.StatusMessage = "保存附件失败：" + ex.Message;
            ViewModel.IsStatusOpen = true;
        }
    }

    private async Task RenderBodyAsync()
    {
        var reading = ViewModel.Reading;
        var version = ++_renderVersion;
        if (!reading.HasMessage || reading.IsBodyLoading)
        {
            // 加载中：隐藏正文，让 ProgressRing 露出来
            PlainBodyScroll.Visibility = Visibility.Collapsed;
            BodyWebView.Visibility = Visibility.Collapsed;
            return;
        }

        var html = reading.DisplayBodyHtml;
        if (string.IsNullOrWhiteSpace(html))
        {
            ShowPlainBody(true);
            return;
        }

        if (!await EnsureWebViewAsync())
        {
            ShowPlainBody(true);
            return;
        }

        // 异步初始化期间用户可能已切换到别的邮件
        if (version != _renderVersion)
        {
            return;
        }

        ShowPlainBody(false);
        BodyWebView.NavigateToString(WrapHtml(html));
    }

    private void ShowPlainBody(bool plain)
    {
        PlainBodyScroll.Visibility = plain ? Visibility.Visible : Visibility.Collapsed;
        BodyWebView.Visibility = plain ? Visibility.Collapsed : Visibility.Visible;
        BodyWebView.Opacity = 1;
    }

    private async Task<bool> EnsureWebViewAsync()
    {
        if (_webReady)
        {
            return true;
        }

        if (_webInitStarted)
        {
            return false;
        }

        _webInitStarted = true;
        try
        {
            // 折叠状态下 WebView2 可能不会初始化：先以透明方式放进可视树。
            BodyWebView.Opacity = 0;
            BodyWebView.Visibility = Visibility.Visible;
            await BodyWebView.EnsureCoreWebView2Async();
            var core = BodyWebView.CoreWebView2;
            // 邮件正文是不可信内容：禁用脚本、右键菜单里的开发者工具，并拦截一切用户点击的导航。
            core.Settings.IsScriptEnabled = false;
            core.Settings.AreDevToolsEnabled = false;
            core.Settings.AreDefaultContextMenusEnabled = false;
            core.Settings.IsStatusBarEnabled = false;
            core.Settings.IsZoomControlEnabled = false;
            core.NavigationStarting += (_, args) =>
            {
                // 正文是 NavigateToString 加载的（data:/about:），非用户发起的导航放行；其余按链接路由处理
                var route = MailClient.Core.Links.LinkRouter.Route(args.Uri, CurrentLinkMode(), args.IsUserInitiated);
                if (route.Action != MailClient.Core.Links.LinkAction.Allow)
                {
                    args.Cancel = true;
                    if (args.IsUserInitiated)
                    {
                        HandleMailLink(route);
                    }
                }
            };
            core.NewWindowRequested += (_, args) =>
            {
                // target=_blank / window.open：绝不在阅读窗格里开新窗口，按链接设置路由
                args.Handled = true;
                HandleMailLink(MailClient.Core.Links.LinkRouter.Route(args.Uri, CurrentLinkMode(), args.IsUserInitiated));
            };
            _webReady = true;
        }
        catch
        {
            // 没有 WebView2 运行时：回退到纯文本
            _webReady = false;
            BodyWebView.Visibility = Visibility.Collapsed;
        }

        return _webReady;
    }

    private MailClient.Core.Links.LinkOpenMode CurrentLinkMode() =>
        AppSettings?.GetLinkOpenMode() ?? MailClient.Core.Links.LinkOpenMode.System;

    private InAppBrowserWindow? _inAppBrowser;

    /// <summary>邮件正文里的链接点击后的去向：系统浏览器 / 内置浏览器 / 写信 / 拦截。</summary>
    internal void HandleMailLink(MailClient.Core.Links.LinkRoute route)
    {
        switch (route.Action)
        {
            case MailClient.Core.Links.LinkAction.OpenSystem when route.Uri is { } systemUri:
                _ = LaunchSystemBrowserAsync(systemUri);
                break;
            case MailClient.Core.Links.LinkAction.OpenInternal when route.Uri is { } internalUri:
                OpenInInternalBrowser(internalUri);
                break;
            case MailClient.Core.Links.LinkAction.Compose when route.Mailto is { } mailto:
                OnMailtoRequested(mailto);
                break;
            case MailClient.Core.Links.LinkAction.Block:
                ViewModel.StatusMessage = "已阻止不安全或不支持的链接" + (string.IsNullOrEmpty(route.BlockedScheme) ? "。" : $"（{route.BlockedScheme}:）。");
                ViewModel.IsStatusError = false;
                ViewModel.IsStatusOpen = true;
                break;
        }
    }

    private static async Task LaunchSystemBrowserAsync(Uri uri)
    {
        try
        {
            await Launcher.LaunchUriAsync(uri);
        }
        catch
        {
            // 没有可用的默认浏览器：忽略
        }
    }

    private void OpenInInternalBrowser(Uri uri)
    {
        try
        {
            if (_inAppBrowser is null)
            {
                var window = new InAppBrowserWindow();
                window.Closed += (_, _) => _inAppBrowser = null;
                window.ComposeRequested += mailto => DispatcherQueue.TryEnqueue(() => OnMailtoRequested(mailto));
                _inAppBrowser = window;
            }

            _inAppBrowser.Navigate(uri);
            _inAppBrowser.Activate();
        }
        catch (Exception ex)
        {
            _inAppBrowser = null;
            ViewModel.StatusMessage = "无法打开内置浏览器：" + ex.Message;
            ViewModel.IsStatusError = true;
            ViewModel.IsStatusOpen = true;
            _ = LaunchSystemBrowserAsync(uri);
        }
    }

    private void OnMailtoRequested(MailClient.Core.Links.MailtoData mailto) =>
        OnComposeRequested(this, new ComposeRequest(ComposeMode.New, null, mailto.To, mailto.Cc, mailto.Subject, mailto.Body, mailto.Bcc));

    /// <summary>仅供自动化测试 / 截图：用当前设置打开一个链接（和点邮件里的链接走同一条路由）。</summary>
    internal void OpenLinkForTest(string url) =>
        HandleMailLink(MailClient.Core.Links.LinkRouter.Route(url, CurrentLinkMode()));

    private string WrapHtml(string body)
    {
        var dark = Content is FrameworkElement fe && fe.ActualTheme == ElementTheme.Dark;
        var fg = dark ? "#F3F3F3" : "#1B1B1B";
        var bg = dark ? "#2B2B2B" : "#FFFFFF";
        var link = dark ? "#60CDFF" : "#005FB8";
        const string template =
            "<!DOCTYPE html><html><head><meta charset=\"utf-8\">" +
            "<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">" +
            "<style>" +
            "html,body{background:@BG@;color:@FG@;}" +
            "body{font-family:'Segoe UI Variable Text','Segoe UI','Microsoft YaHei UI',sans-serif;font-size:14px;line-height:1.65;margin:18px 24px;word-wrap:break-word;}" +
            "a{color:@LINK@;} img{max-width:100%;height:auto;} p{margin:0 0 12px 0;}" +
            "mark.mc-hit{background:#FFD54F;color:#000;border-radius:2px;}" +
            "</style></head><body>@BODY@</body></html>";
        return template
            .Replace("@BG@", bg)
            .Replace("@FG@", fg)
            .Replace("@LINK@", link)
            .Replace("@BODY@", body);
    }

    // ───────── 列表右键菜单 / 发件箱 ─────────

    private void MessageListView_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_rebuildingMessageItems)
        {
            return;
        }

        if (MessageListView.SelectedItem is MessageRowViewModel row)
        {
            if (!ReferenceEquals(ViewModel.List.SelectedMessage, row))
            {
                ViewModel.List.SelectedMessage = row;
            }
        }
        else if (MessageListView.SelectedItem is null && ViewModel.List.SelectedMessage is not null)
        {
            // 重建 Items 过程中会短暂清空；忽略，由 Rebuild 末尾恢复。
        }
    }

    private void MessageListView_ContextRequested(UIElement sender, ContextRequestedEventArgs args)
    {
        if (args.OriginalSource is not FrameworkElement { DataContext: MessageRowViewModel row } source)
        {
            return;
        }

        var flyout = new MenuFlyout();
        if (row.IsDraft)
        {
            AddDraftItems(flyout, row);
            ShowFlyout(flyout, source, args);
            return;
        }

        var read = new MenuFlyoutItem
        {
            Text = row.IsUnread ? "标记为已读" : "标记为未读",
            Icon = new FontIcon { Glyph = row.IsUnread ? "\uE73E" : "\uE715" }
        };
        read.Click += async (_, _) => await ViewModel.ToggleReadAsync(row);
        var flag = new MenuFlyoutItem
        {
            Text = row.IsFlagged ? "取消标记" : "标记",
            Icon = new FontIcon { Glyph = "\uE7C1" }
        };
        flag.Click += async (_, _) => await ViewModel.ToggleFlagAsync(row);
        var archive = new MenuFlyoutItem { Text = "归档", Icon = new FontIcon { Glyph = "\uE8B7" } };
        archive.Click += async (_, _) => await ViewModel.ArchiveRowAsync(row);
        var delete = new MenuFlyoutItem { Text = "删除", Icon = new FontIcon { Glyph = "\uE74D" } };
        delete.Click += async (_, _) => await ViewModel.DeleteRowAsync(row);
        flyout.Items.Add(read);
        flyout.Items.Add(flag);
        flyout.Items.Add(new MenuFlyoutSeparator());
        flyout.Items.Add(archive);
        flyout.Items.Add(delete);

        ShowFlyout(flyout, source, args);
    }

    private static void ShowFlyout(MenuFlyout flyout, FrameworkElement source, ContextRequestedEventArgs args)
    {
        if (args.TryGetPosition(source, out var point))
        {
            flyout.ShowAt(source, new FlyoutShowOptions { Position = point });
        }
        else
        {
            flyout.ShowAt(source);
        }

        args.Handled = true;
    }

    // ───────── 草稿：打开 / 删除 ─────────

    /// <summary>草稿行的右键菜单：继续编辑 / 删除草稿（本地草稿删除前会确认）。</summary>
    private void AddDraftItems(MenuFlyout flyout, MessageRowViewModel row)
    {
        var edit = new MenuFlyoutItem { Text = "继续编辑", Icon = new FontIcon { Glyph = "\uE70F" } };
        edit.Click += async (_, _) => await ViewModel.OpenDraftAsync(row.Id);
        var delete = new MenuFlyoutItem { Text = "删除草稿", Icon = new FontIcon { Glyph = "\uE74D" } };
        delete.Click += async (_, _) => await ViewModel.DeleteRowAsync(row);
        flyout.Items.Add(edit);
        flyout.Items.Add(new MenuFlyoutSeparator());
        flyout.Items.Add(delete);
    }

    private async void MessageListView_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (e.OriginalSource is FrameworkElement { DataContext: MessageRowViewModel { IsDraft: true } row })
        {
            e.Handled = true;
            await ViewModel.OpenDraftAsync(row.Id);
        }
    }

    /// <summary>Enter 打开选中的草稿；Delete 删除选中的草稿。</summary>
    private async void MessageListView_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (ViewModel.List.SelectedMessage is not { IsDraft: true } row)
        {
            return;
        }

        if (e.Key == VirtualKey.Enter)
        {
            e.Handled = true;
            await ViewModel.OpenDraftAsync(row.Id);
        }
        else if (e.Key == VirtualKey.Delete)
        {
            e.Handled = true;
            await ViewModel.DeleteRowAsync(row);
        }
    }

    private async Task<bool> ConfirmDeleteDraftAsync()
    {
        if (_dialogOpen || Content?.XamlRoot is null)
        {
            return false;
        }

        _dialogOpen = true;
        try
        {
            var dialog = new ContentDialog
            {
                XamlRoot = Content.XamlRoot,
                Title = "删除这份草稿？",
                Content = "草稿和它的附件副本会被删除，无法恢复。",
                PrimaryButtonText = "删除",
                CloseButtonText = "取消",
                DefaultButton = ContentDialogButton.Close
            };
            return await dialog.ShowAsync() == ContentDialogResult.Primary;
        }
        finally
        {
            _dialogOpen = false;
        }
    }

    private async void OutboxRetry_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string id })
        {
            await ViewModel.Outbox.RetryAsync(id);
        }
    }

    private async void OutboxDelete_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string id } || _dialogOpen || Content?.XamlRoot is null)
        {
            return;
        }

        _dialogOpen = true;
        try
        {
            var dialog = new ContentDialog
            {
                XamlRoot = Content.XamlRoot,
                Title = "从发件箱删除这封邮件？",
                Content = "删除后这封邮件不会再被发送。",
                PrimaryButtonText = "删除",
                CloseButtonText = "取消",
                DefaultButton = ContentDialogButton.Close
            };
            if (await dialog.ShowAsync() == ContentDialogResult.Primary)
            {
                await ViewModel.Outbox.DeleteAsync(id);
            }
        }
        finally
        {
            _dialogOpen = false;
        }
    }

    // ───────── 写信 ─────────

    private async void OnComposeRequested(object? sender, ComposeRequest request)
    {
        try
        {
            // 同一份草稿只开一个窗口：已经打开就激活它
            await ViewModel.OpenComposeWindowAsync(request, vm =>
            {
                var window = new ComposeWindow(vm);
                vm.Sent += (_, _) =>
                {
                    ViewModel.NotifySent();
                    window.Close();
                };
                window.Activate();
                return window;
            });
        }
        catch (Exception ex)
        {
            ViewModel.StatusMessage = $"无法打开写信窗口：{ex.Message}";
            ViewModel.IsStatusOpen = true;
        }
    }

    // ───────── 添加 / 移除账号 ─────────

    private void OnShellPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ShellViewModel.HasNoRealAccounts))
        {
            UpdateAddAccountStyle();
        }

        if (e.PropertyName is nameof(ShellViewModel.IsOutboxMode)
            or nameof(ShellViewModel.ShowOutboxEmpty)
            or nameof(ShellViewModel.ShowListEmpty)
            or nameof(ShellViewModel.IsMessageListMode))
        {
            UpdateEmptyPanels();
        }
    }

    /// <summary>
    /// x:Bind 布尔→Visibility 在部分启动时序下不会刷新；用代码同步空状态与列表可见性。
    /// </summary>
    private void UpdateEmptyPanels()
    {
        var outbox = ViewModel.IsOutboxMode;
        MessageListView.Visibility = outbox ? Visibility.Collapsed : Visibility.Visible;
        OutboxListView.Visibility = outbox ? Visibility.Visible : Visibility.Collapsed;
        OutboxEmptyPanel.Visibility = ViewModel.ShowOutboxEmpty ? Visibility.Visible : Visibility.Collapsed;
        ListEmptyPanel.Visibility = ViewModel.ShowListEmpty ? Visibility.Visible : Visibility.Collapsed;
        CategoryBar.Visibility = ViewModel.List.ShowCategoryBar && !outbox ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>还没有真实账号时，“添加账号”按钮用强调色，引导用户。</summary>
    private void UpdateAddAccountStyle()
    {
        AddAccountButton.Style = ViewModel.HasNoRealAccounts
            ? (Style)Application.Current.Resources["AccentButtonStyle"]
            : (Style)Application.Current.Resources["DefaultButtonStyle"];
    }

    private async void AddAccountButton_Click(object sender, RoutedEventArgs e) => await ShowAddAccountAsync();

    // ───────── 设置 / 通知 ─────────

    /// <summary>由 App 注入：设置存储 + 系统通知服务（设置对话框要读写开关、发测试通知）。</summary>
    internal MailClient.Core.AppSettingsStore? AppSettings { get; set; }

    internal NotificationService? Notifications { get; set; }

    internal MailClient.Storage.Services.StorageMaintenanceService? Storage { get; set; }

    /// <summary>设置里「关闭主窗口时保留在后台」被切换时通知 App 同步托盘。</summary>
    public event Action<bool>? BackgroundSettingChanged;

    private async void SettingsButton_Click(object sender, RoutedEventArgs e) => await ShowSettingsAsync();

    public async Task ShowSettingsAsync()
    {
        if (_dialogOpen || Content?.XamlRoot is null || AppSettings is null)
        {
            return;
        }

        _dialogOpen = true;
        try
        {
            var settings = AppSettings;
            var notifications = Notifications;
            var toggle = new ToggleSwitch
            {
                Header = "新邮件通知",
                OnContent = "开：收到新邮件时弹出 Windows 通知",
                OffContent = "关：不弹通知",
                IsOn = settings.Snapshot().NotificationsEnabled
            };
            toggle.SetValue(Microsoft.UI.Xaml.Automation.AutomationProperties.AutomationIdProperty, "NotificationsToggle");
            toggle.SetValue(Microsoft.UI.Xaml.Automation.AutomationProperties.NameProperty, "新邮件通知");
            toggle.Toggled += (_, _) => settings.Update(s => s.NotificationsEnabled = toggle.IsOn);

            var windowsStatus = new TextBlock
            {
                Text = notifications?.WindowsStatusText ?? "通知服务不可用。",
                TextWrapping = TextWrapping.Wrap,
                Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorSecondaryBrush"]
            };
            var testResult = new TextBlock
            {
                TextWrapping = TextWrapping.Wrap,
                Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorSecondaryBrush"]
            };
            var testButton = new Button
            {
                Content = "发送测试通知",
                IsEnabled = notifications?.IsRegistered == true
            };
            testButton.SetValue(Microsoft.UI.Xaml.Automation.AutomationProperties.AutomationIdProperty, "TestNotificationButton");
            testButton.Click += (_, _) =>
            {
                var ok = notifications?.ShowTest() == true;
                testResult.Text = ok
                    ? "已发送。如果没有看到，请检查 Windows 的「专注助手 / 勿扰」和下面链接里的通知开关。"
                    : "发送失败：" + (notifications?.WindowsStatusText ?? "通知服务不可用。");
            };
            var note = new TextBlock
            {
                Text = "新邮件通知只在软件运行时（包括最小化或关到托盘后台）弹出；首次添加账号时同步下来的旧邮件、已读邮件和你自己发出的邮件不会通知。点通知会打开对应邮件，通知上的「标为已读」「删除」会同步到邮箱服务器。",
                TextWrapping = TextWrapping.Wrap,
                Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
                Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorSecondaryBrush"]
            };
            var windowsLink = new HyperlinkButton
            {
                Content = "打开 Windows 通知设置",
                Padding = new Thickness(0)
            };
            windowsLink.Click += async (_, _) =>
            {
                if (await WindowsSettings.OpenAsync("notifications"))
                {
                    return;
                }

                windowsStatus.Text = "打不开 Windows 的通知设置。请手动打开：设置 → 系统 → 通知。";
            };

            var notifPanel = new StackPanel { Spacing = 10, Padding = new Thickness(0, 8, 0, 8) };
            notifPanel.Children.Add(toggle);
            notifPanel.Children.Add(windowsStatus);
            notifPanel.Children.Add(windowsLink);
            notifPanel.Children.Add(testButton);
            notifPanel.Children.Add(testResult);
            notifPanel.Children.Add(note);

            var generalPanel = SettingsSections.BuildGeneral(settings, enabled =>
            {
                // 设置变更时由 App 同步托盘显隐（通过事件）
                BackgroundSettingChanged?.Invoke(enabled);
            }, Updates, release =>
            {
                // 设置里点「查看并更新」：先关掉设置对话框，再打开更新对话框（同一时间只能有一个 ContentDialog）
                _updateFromSettings = release;
                _settingsDialog?.Hide();
            });
            var linksPanel = SettingsSections.BuildLinks(settings);
            var storagePanel = SettingsSections.BuildStorage(settings, Storage, message =>
            {
                ViewModel.StatusMessage = message;
                ViewModel.IsStatusError = true;
                ViewModel.IsStatusOpen = true;
            });

            var pivot = new Pivot { MinWidth = 520, Height = 470 };
            pivot.SetValue(Microsoft.UI.Xaml.Automation.AutomationProperties.AutomationIdProperty, "SettingsPivot");
            pivot.Items.Add(MakeSettingsPage("常规", "SettingsTabGeneral", generalPanel));
            pivot.Items.Add(MakeSettingsPage("通知", "SettingsTabNotifications", notifPanel));
            pivot.Items.Add(MakeSettingsPage("链接", "SettingsTabLinks", linksPanel));
            pivot.Items.Add(MakeSettingsPage("存储", "SettingsTabStorage", storagePanel));
            var panel = pivot;

            var dialog = new ContentDialog
            {
                XamlRoot = Content.XamlRoot,
                Title = "设置",
                Content = panel,
                CloseButtonText = "完成",
                DefaultButton = ContentDialogButton.Close
            };
            dialog.Resources["ContentDialogMaxWidth"] = 720d;
            _settingsDialog = dialog;
            await dialog.ShowAsync();
            _settingsDialog = null;
            if (_updateFromSettings is { } wanted)
            {
                _updateFromSettings = null;
                _dialogOpen = false;
                await ShowUpdateDialogAsync(wanted);
            }
        }
        catch (Exception ex)
        {
            ViewModel.StatusMessage = $"无法打开设置：{ex.Message}";
            ViewModel.IsStatusError = true;
            ViewModel.IsStatusOpen = true;
        }
        finally
        {
            _dialogOpen = false;
        }
    }

    private static PivotItem MakeSettingsPage(string header, string automationId, UIElement content)
    {
        var item = new PivotItem
        {
            Header = header,
            Content = new ScrollViewer
            {
                Content = content,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Padding = new Thickness(0, 0, 12, 0)
            }
        };
        item.SetValue(Microsoft.UI.Xaml.Automation.AutomationProperties.AutomationIdProperty, automationId);
        item.SetValue(Microsoft.UI.Xaml.Automation.AutomationProperties.NameProperty, header);
        return item;
    }

    // ───────── 检查更新 ─────────

    private ContentDialog? _settingsDialog;
    private ReleaseInfo? _updateFromSettings;
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _updatePendingTimer;
    private string? _toastedUpdateTag;

    /// <summary>检查更新服务（由 App 创建）。</summary>
    public UpdateService? Updates { get; set; }

    /// <summary>接上更新服务：自动检查发现新版本时提示；窗口藏在托盘时发通知，等窗口出现再弹对话框。</summary>
    public void StartUpdatePrompts()
    {
        if (Updates is null)
        {
            return;
        }

        Updates.UpdateAnnounced += release => DispatcherQueue.TryEnqueue(() => OnUpdateAnnounced(release));
        _updatePendingTimer = DispatcherQueue.CreateTimer();
        _updatePendingTimer.Interval = TimeSpan.FromSeconds(10);
        _updatePendingTimer.Tick += (_, _) => ShowPendingUpdateIfPossible();
        _updatePendingTimer.Start();
    }

    private bool CanShowDialogNow()
    {
        try
        {
            return Content?.XamlRoot is not null
                && AppWindow.IsVisible
                && AppWindow.Presenter is not OverlappedPresenter { State: OverlappedPresenterState.Minimized };
        }
        catch (Exception)
        {
            return false;
        }
    }

    private void OnUpdateAnnounced(ReleaseInfo release)
    {
        if (CanShowDialogNow())
        {
            ShowPendingUpdateIfPossible();
            return;
        }

        // 窗口藏在托盘（或最小化）：发一条通知，点它会打开更新对话框；窗口再出现时也会自动弹出
        if (_toastedUpdateTag != release.TagName)
        {
            _toastedUpdateTag = release.TagName;
            Notifications?.ShowUpdateAvailable(UpdateVersion.Display(release.Version));
        }
    }

    private void ShowPendingUpdateIfPossible()
    {
        if (Updates?.Pending is { } pending && !_dialogOpen && CanShowDialogNow())
        {
            _ = ShowUpdateDialogAsync(pending);
        }
    }

    /// <summary>点「邮件有新版本」通知后：显示窗口并打开更新对话框（还没有检查结果时现查一次）。</summary>
    public async Task ShowUpdateFromToastAsync()
    {
        if (Updates is null)
        {
            return;
        }

        BringToFront();
        var release = Updates.Pending;
        if (release is null)
        {
            var result = await Updates.CheckAsync(manual: true);
            release = result.Status == UpdateCheckStatus.UpdateAvailable ? result.Release : null;
        }

        if (release is not null)
        {
            await ShowUpdateDialogAsync(release);
        }
    }

    public async Task ShowUpdateDialogAsync(ReleaseInfo release)
    {
        if (_dialogOpen || Content?.XamlRoot is null || Updates is null)
        {
            return;
        }

        _dialogOpen = true;
        try
        {
            Updates.Pending = null;
            await UpdateDialog.ShowAsync(Content.XamlRoot, Updates, release, RequestQuit);
        }
        catch (Exception ex)
        {
            ViewModel.StatusMessage = $"无法显示更新对话框：{ex.Message}";
            ViewModel.IsStatusError = true;
            ViewModel.IsStatusOpen = true;
        }
        finally
        {
            _dialogOpen = false;
        }
    }

    /// <summary>开机静默启动时先不弹欢迎框，等窗口第一次被打开再提示。</summary>
    public bool PromptFirstRunWhenShown { get; set; }

    /// <summary>窗口从隐藏变为可见（托盘 / 开始菜单唤起）。</summary>
    public event Action? ShownFromHidden;

    /// <summary>把窗口带到最前（从系统通知 / 托盘点进来时用）：若已隐藏或最小化则还原，并抢到前台。</summary>
    public void BringToFront()
    {
        var wasHidden = false;
        try
        {
            wasHidden = !AppWindow.IsVisible;
            if (AppWindow.Presenter is OverlappedPresenter presenter)
            {
                if (presenter.State == OverlappedPresenterState.Minimized)
                {
                    presenter.Restore();
                }
            }

            if (!AppWindow.IsVisible)
            {
                AppWindow.Show();
            }

            Activate();
            SetForegroundWindow(WinRT.Interop.WindowNative.GetWindowHandle(this));
        }
        catch
        {
            // 抢前台失败（系统限制）时窗口至少会在任务栏闪烁
        }

        if (!wasHidden)
        {
            return;
        }

        try
        {
            ShownFromHidden?.Invoke();
        }
        catch
        {
            // 托盘显隐失败不影响打开窗口
        }

        if (PromptFirstRunWhenShown)
        {
            PromptFirstRunWhenShown = false;
            _ = ShowFirstRunPromptIfNeededAsync();
        }
    }

    /// <summary>托盘「退出」或设置关闭后台后的真正退出：先关写信窗，再强制关闭主窗。</summary>
    public void RequestQuit()
    {
        _forceClose = true;
        try
        {
            foreach (var window in ViewModel.ComposeWindows.Snapshot())
            {
                try { window.Close(); } catch { /* ignore */ }
            }
        }
        catch { /* ignore */ }

        try
        {
            Close();
        }
        catch
        {
            // 极端情况下窗口已毁
        }
    }

    /// <summary>当前是否处于“关窗进托盘”模式（设置默认开）。</summary>
    public bool IsCloseToTrayEnabled
        => CloseToTrayPolicy.ResolveKeepRunning(AppSettings?.Snapshot().KeepRunningInBackground);

    /// <summary>藏到托盘后通知 App（用于首次提示、确保托盘图标还在）。</summary>
    public event Action? HiddenToTray;

    private void WireCloseToTray()
    {
        if (_closingWired)
        {
            return;
        }

        _closingWired = true;
        AppWindow.Closing += OnAppWindowClosing;
    }

    private void OnAppWindowClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        var keep = CloseToTrayPolicy.ResolveKeepRunning(AppSettings?.Snapshot().KeepRunningInBackground);
        if (!CloseToTrayPolicy.ShouldHideInsteadOfClose(keep, _forceClose))
        {
            return;
        }

        args.Cancel = true;
        try
        {
            AppWindow.Hide();
        }
        catch
        {
            // Hide 失败则仍保持取消关闭，至少进程不退
        }

        try
        {
            HiddenToTray?.Invoke();
        }
        catch
        {
            // 提示失败不影响后台
        }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hwnd);

    public async Task ShowAddAccountAsync()
    {
        if (_dialogOpen || !ViewModel.CanAddAccount || Content?.XamlRoot is null)
        {
            return;
        }

        _dialogOpen = true;
        try
        {
            var vm = ViewModel.CreateAddAccountViewModel();
            AccountEntityHolder holder = new();
            var dialog = new ContentDialog
            {
                XamlRoot = Content.XamlRoot,
                Content = new AddAccountView(vm),
                // 向导自带“取消 / 上一步 / 下一步”，不用 ContentDialog 的默认按钮
                DefaultButton = ContentDialogButton.None
            };
            dialog.Resources["ContentDialogMaxWidth"] = 760d;
            dialog.Resources["ContentDialogMinWidth"] = 640d;
            vm.Completed += (_, account) => holder.Account = account;
            vm.CloseRequested += (_, _) => dialog.Hide();

            await dialog.ShowAsync();

            // 按 Esc 等方式关闭：丢弃尚未保存的草稿（OAuth 令牌等）
            if (holder.Account is null)
            {
                vm.Cancel();
            }
            else
            {
                await ViewModel.OnAccountAddedAsync(holder.Account);
            }
        }
        catch (Exception ex)
        {
            ViewModel.StatusMessage = $"无法完成添加账号：{ex.Message}";
            ViewModel.IsStatusError = true;
            ViewModel.IsStatusOpen = true;
        }
        finally
        {
            _dialogOpen = false;
        }
    }

    private sealed class AccountEntityHolder
    {
        public MailClient.Core.AccountEntity? Account { get; set; }
    }


    private async Task RenameAccountAsync(Guid accountId, string currentName, string? emailAddress)
    {
        if (_dialogOpen || Content?.XamlRoot is null)
        {
            return;
        }

        _dialogOpen = true;
        try
        {
            var box = new TextBox
            {
                Text = currentName,
                PlaceholderText = emailAddress ?? "显示名称",
                MaxLength = 80
            };
            var dialog = new ContentDialog
            {
                XamlRoot = Content.XamlRoot,
                Title = "重命名账号",
                Content = box,
                PrimaryButtonText = "保存",
                CloseButtonText = "取消",
                DefaultButton = ContentDialogButton.Primary
            };
            box.SelectAll();
            if (await dialog.ShowAsync() == ContentDialogResult.Primary)
            {
                await ViewModel.RenameAccountAsync(accountId, box.Text);
            }
        }
        catch (Exception ex)
        {
            ViewModel.StatusMessage = $"重命名失败：{ex.Message}";
            ViewModel.IsStatusError = true;
            ViewModel.IsStatusOpen = true;
        }
        finally
        {
            _dialogOpen = false;
        }
    }

    private async Task ConfirmRemoveAccountAsync(Guid accountId, string name)
    {
        if (_dialogOpen || Content?.XamlRoot is null)
        {
            return;
        }

        _dialogOpen = true;
        try
        {
            var dialog = new ContentDialog
            {
                XamlRoot = Content.XamlRoot,
                Title = $"移除账号「{name}」？",
                Content = "只会删除本机上这个账号的邮件缓存和保存的登录凭据，不会删除邮箱服务器上的邮件。",
                PrimaryButtonText = "移除",
                CloseButtonText = "取消",
                DefaultButton = ContentDialogButton.Close
            };
            if (await dialog.ShowAsync() == ContentDialogResult.Primary)
            {
                await ViewModel.RemoveAccountAsync(accountId);
            }
        }
        catch (Exception ex)
        {
            ViewModel.StatusMessage = $"移除账号失败：{ex.Message}";
            ViewModel.IsStatusError = true;
            ViewModel.IsStatusOpen = true;
        }
        finally
        {
            _dialogOpen = false;
        }
    }

    private async void ClearDemoButton_Click(object sender, RoutedEventArgs e)
    {
        if (_dialogOpen || Content?.XamlRoot is null)
        {
            return;
        }

        _dialogOpen = true;
        try
        {
            var dialog = new ContentDialog
            {
                XamlRoot = Content.XamlRoot,
                Title = "清除演示数据？",
                Content = "将删除示例账号和示例邮件。你自己添加的账号不受影响。",
                PrimaryButtonText = "清除",
                CloseButtonText = "取消",
                DefaultButton = ContentDialogButton.Close
            };
            if (await dialog.ShowAsync() == ContentDialogResult.Primary)
            {
                await ViewModel.ClearDemoDataAsync();
            }
        }
        catch (Exception ex)
        {
            ViewModel.StatusMessage = $"清除演示数据失败：{ex.Message}";
            ViewModel.IsStatusError = true;
            ViewModel.IsStatusOpen = true;
        }
        finally
        {
            _dialogOpen = false;
        }
    }

    /// <summary>首次运行（没有真实账号且没点过“不再提示”）时弹出欢迎提示。</summary>
    public async Task ShowFirstRunPromptIfNeededAsync()
    {
        if (!ViewModel.ShouldShowFirstRunPrompt || _dialogOpen)
        {
            return;
        }

        // 内容树加载完成后 XamlRoot 才可用
        if (Content is FrameworkElement root && !root.IsLoaded)
        {
            var loaded = new TaskCompletionSource();
            RoutedEventHandler handler = (_, _) => loaded.TrySetResult();
            root.Loaded += handler;
            if (!root.IsLoaded)
            {
                await Task.WhenAny(loaded.Task, Task.Delay(3000));
            }

            root.Loaded -= handler;
        }

        if (Content?.XamlRoot is null)
        {
            return;
        }

        _dialogOpen = true;
        ContentDialogResult result;
        try
        {
            var dialog = new ContentDialog
            {
                XamlRoot = Content.XamlRoot,
                Title = "欢迎使用邮件",
                Content = "现在看到的是演示数据。添加你的邮箱账号后，就可以收发真实邮件了——支持 Gmail、Outlook、QQ 邮箱、网易邮箱和其他 IMAP/SMTP 邮箱。",
                PrimaryButtonText = "添加账号",
                SecondaryButtonText = "稍后再说",
                CloseButtonText = "不再提示",
                DefaultButton = ContentDialogButton.Primary
            };
            result = await dialog.ShowAsync();
        }
        finally
        {
            _dialogOpen = false;
        }

        switch (result)
        {
            case ContentDialogResult.Primary:
                await ShowAddAccountAsync();
                break;
            case ContentDialogResult.None:
                ViewModel.DismissFirstRunPrompt();
                break;
        }
    }
}
