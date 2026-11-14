using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MailClient.Core;
using MailClient.Core.Search;
using MailClient.Core.Services;
using MailClient.Providers.Outbox;
using MailClient.Providers.Setup;
using MailClient.Providers.WriteBack;
using MailClient.Storage;
using MailClient.Storage.Demo;
using MailClient.ViewModels.AddAccount;

namespace MailClient.ViewModels;

/// <summary>主窗口 VM：把左 / 中 / 右三栏串起来。</summary>
public sealed partial class ShellViewModel : ObservableObject
{
    private readonly MailDatabase _database;
    private readonly ISendService _sendService;
    private readonly ISyncService? _syncService;
    private readonly IAccountSetupService? _accountSetup;
    private readonly AppSettingsStore? _settings;
    private readonly IDraftStore? _draftStore;
    private SynchronizationContext? _ui = SynchronizationContext.Current;

    /// <summary>在 UI 线程就绪后注入（WinUI 构造 Shell 时 SynchronizationContext 往往还是 null）。</summary>
    public void AttachSynchronizationContext(SynchronizationContext? context = null)
    {
        _ui = context ?? SynchronizationContext.Current ?? _ui;
        Tree.AttachSynchronizationContext(_ui);
        List.AttachSynchronizationContext(_ui);
        Reading.AttachSynchronizationContext(_ui);
    }
    private int _syncRunning;
    private Guid? _selectInboxOfAccount;

    public ShellViewModel(
        MailDatabase database,
        ISendService sendService,
        Func<DateTimeOffset>? clock = null,
        ISyncService? syncService = null,
        IAccountSetupService? accountSetup = null,
        AppSettingsStore? settings = null,
        IDraftStore? draftStore = null,
        IRecentSearchStore? recentSearches = null,
        IMessageBodyLoader? bodyLoader = null)
    {
        _database = database;
        _sendService = sendService;
        _syncService = syncService;
        _accountSetup = accountSetup;
        _settings = settings;
        _draftStore = draftStore;
        RecentSearches = recentSearches ?? (settings is not null ? new SettingsRecentSearchStore(settings) : new MemoryRecentSearchStore());
        Tree = new MailboxTreeViewModel(database);
        if (settings is not null)
        {
            Tree.ColorResolver = settings.GetAccountColor;
        }

        if (syncService is IProgressSyncService progressService)
        {
            progressService.ProgressChanged += (_, p) => Post(() => OnSyncProgress(p));
        }

        List = new MessageListViewModel(database, clock);
        Actions = new MessageActions(database, Tree, draftStore);
        Reading = new ReadingPaneViewModel(database, Tree, Actions, bodyLoader);
        Outbox = new OutboxViewModel(database, clock);

        Tree.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MailboxTreeViewModel.SelectedNode) && Tree.SelectedNode is { IsSelectable: true } node)
            {
                _ = ShowNodeSafeAsync(node);
            }
        };
        List.SelectionChangedExternally += async (_, _) => await Reading.LoadAsync(List.SelectedMessage?.Id);
        Outbox.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(OutboxViewModel.IsEmpty))
            {
                OnPropertyChanged(nameof(ShowOutboxEmpty));
            }
        };
        List.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MessageListViewModel.IsEmpty))
            {
                OnPropertyChanged(nameof(ShowListEmpty));
            }
            else if (e.PropertyName == nameof(MessageListViewModel.HighlightTerms))
            {
                Reading.HighlightTerms = List.HighlightTerms.All;
            }
        };
        Reading.EditDraftRequested += (_, id) => { _ = OpenDraftAsync(id); };
        Reading.DeleteDraftRequested += (_, id) => { _ = DeleteLocalDraftAsync(id); };
        database.DraftsChanged += (_, _) => Post(() => _ = OnDraftsChangedAsync());
        // 已读 / 星标在数据库写完后于线程池上触发。x:Bind 会在 PropertyChanged 里直接改 Visibility，
        // 后台线程改会抛 0x8001010E（RPC_E_WRONG_THREAD），async void 接不住，进程直接退出。
        Actions.Mutated += (_, e) => Post(() => _ = OnMutatedAsync(e));
        Actions.Notice += (_, n) => Post(() => ShowStatus(n.Message, n.IsError));
        Reading.ComposeRequested += (_, r) => ComposeRequested?.Invoke(this, r);
        database.OutboxChanged += (_, _) => Post(() => _ = OnOutboxChangedAsync());
    }

    public MessageActions Actions { get; }
    public OutboxViewModel Outbox { get; }

    /// <summary>已打开的写信窗口（按草稿键登记，保证同一份草稿只开一个）。</summary>
    public SingleInstanceTracker<IComposeWindow> ComposeWindows { get; } = new();

    /// <summary>最近的搜索（搜索框下拉建议）。</summary>
    public IRecentSearchStore RecentSearches { get; }

    private readonly SemaphoreSlim _composeOpenGate = new(1, 1);

    /// <summary>当前选中的是“发件箱”（中栏显示发件箱列表而不是邮件列表）。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsMessageListMode), nameof(ShowOutboxEmpty), nameof(ShowListEmpty))]
    private bool _isOutboxMode;

    public bool IsMessageListMode => !IsOutboxMode;
    public bool ShowOutboxEmpty => IsOutboxMode && Outbox.IsEmpty;
    public bool ShowListEmpty => !IsOutboxMode && List.IsEmpty;

    /// <summary>把后台发件 / 写回服务接上来：发送成功或失败时弹提示，写回被服务器拒绝时通知用户。</summary>
    public void AttachBackgroundServices(OutboxProcessor? outbox, PendingOpsProcessor? pendingOps)
    {
        if (outbox is not null)
        {
            outbox.ItemProcessed += (_, r) => Post(() => _ = OnOutboxItemProcessedAsync(r));
        }

        if (pendingOps is not null)
        {
            pendingOps.OpDropped += (_, d) => Post(() => ShowStatus($"有一项操作未能同步到服务器：{d.Failure.Message}（已恢复为服务器上的状态）", true));
        }
    }

    private async Task OnOutboxChangedAsync()
    {
        await Tree.RefreshOutboxNodeAsync();
        if (IsOutboxMode)
        {
            await Outbox.ReloadAsync();
        }
    }

    private async Task OnOutboxItemProcessedAsync(OutboxResult result)
    {
        var subject = string.IsNullOrWhiteSpace(result.Message?.Subject) ? UiStrings.NoSubject : result.Message!.Subject;
        if (result.Sent)
        {
            ShowStatus($"{UiStrings.MailSent}：{subject}");
            await Tree.RefreshCountsAsync();
            if (Tree.SelectedNode is { Kind: MailboxNodeKind.Folder, Special: SpecialFolder.Sent } or { Kind: MailboxNodeKind.Account })
            {
                await List.ReloadAsync();
            }
        }
        else if (result.Failure is { } failure)
        {
            var retryText = result.WillRetry ? "。稍后会自动重试。" : "。请在发件箱里查看并重试。";
            ShowStatus($"{UiStrings.MailSendFailed}：{subject}（{failure.Message.TrimEnd('。', '.')}）{retryText}", !result.WillRetry);
        }
    }

    // 列表右键菜单用的命令（针对被点击的那一行）
    public Task ToggleReadAsync(MessageRowViewModel row) => Actions.SetReadAsync(row.Id, row.IsUnread);

    public Task ToggleFlagAsync(MessageRowViewModel row) => Actions.SetFlaggedAsync(row.Id, !row.IsFlagged);

    public Task ArchiveRowAsync(MessageRowViewModel row) => Actions.ArchiveAsync(row.Id);

    /// <summary>删除一行；本地草稿走草稿删除（先确认，并关掉它已打开的写信窗口），其余走普通删除。</summary>
    public async Task DeleteRowAsync(MessageRowViewModel row)
    {
        if (row.IsLocalDraft)
        {
            await DeleteLocalDraftAsync(row.Id);
            return;
        }

        await Actions.DeleteAsync(row.Id);
    }

    /// <summary>UI 层提供的确认框（返回 true 才删除）；没有设置时直接删除。</summary>
    public Func<Task<bool>>? ConfirmDraftDeletionAsync { get; set; }

    /// <summary>删除一份本地草稿：确认 → 放弃它已打开窗口里的内容 → 删除草稿 / 附件副本 / 服务器源副本 → 关窗口。</summary>
    public async Task<bool> DeleteLocalDraftAsync(string messageId)
    {
        if (ConfirmDraftDeletionAsync is not null && !await ConfirmDraftDeletionAsync())
        {
            return false;
        }

        IComposeWindow? open = null;
        var key = "draft:" + messageId[MailDatabase.DraftMessagePrefix.Length..];
        if (ComposeWindows.TryGet(key, out var window) && window is not null)
        {
            // 草稿被删了：窗口里的内容不再保存
            window.ViewModel.Abandon();
            open = window;
        }

        var deleted = await Actions.DeleteAsync(messageId);
        open?.Close();
        return deleted;
    }

    public MailboxTreeViewModel Tree { get; }
    public MessageListViewModel List { get; }
    public ReadingPaneViewModel Reading { get; }

    [ObservableProperty]
    private bool _isInitialized;

    [ObservableProperty]
    private string _statusMessage = string.Empty;

    [ObservableProperty]
    private bool _isStatusOpen;

    [ObservableProperty]
    private bool _isStatusError;

    // ───────── 账号相关状态 ─────────

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNoRealAccounts), nameof(ShowClearDemo), nameof(ShouldShowFirstRunPrompt))]
    private bool _hasRealAccounts;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowClearDemo))]
    private bool _hasDemoData;

    [ObservableProperty]
    private bool _isSyncing;

    [ObservableProperty]
    private string _syncStatusText = string.Empty;

    [ObservableProperty]
    private double _syncProgressValue;

    [ObservableProperty]
    private bool _isSyncIndeterminate = true;

    public bool HasNoRealAccounts => !HasRealAccounts;

    /// <summary>已有真实账号、同时还留着演示数据时，显示“清除演示数据”。</summary>
    public bool ShowClearDemo => HasRealAccounts && HasDemoData;

    /// <summary>首次运行（没有真实账号，且未被用户关闭过）时应弹出欢迎 / 添加账号提示。</summary>
    public bool ShouldShowFirstRunPrompt =>
        CanAddAccount && !HasRealAccounts && _settings?.Snapshot().FirstRunPromptDismissed != true;

    public bool CanAddAccount => _accountSetup is not null;
    public bool CanSync => _syncService is not null;

    public event EventHandler<ComposeRequest>? ComposeRequested;

    public async Task InitializeAsync(bool seedDemoData = true)
    {
        await _database.InitializeAsync();
        // 重算被 CSS 污染的列表预览（有 BodyHtml/干净 BodyText 时）；幂等
        try
        {
            await _database.RepairBadPreviewsAsync();
        }
        catch
        {
            // 预览修复失败不挡启动
        }

        if (seedDemoData && _settings?.Snapshot().DemoDataDismissed != true)
        {
            await DemoDataSeeder.SeedIfEmptyAsync(_database);
        }
        else
        {
            // 正式版 / 升级：清掉残留演示账号（Outlook / QQ 邮箱种子），不动用户自己加的账号
            await DemoDataSeeder.ClearDemoDataAsync(_database);
            _settings?.Update(s => s.DemoDataDismissed = true);
        }

        await RefreshAccountFlagsAsync();
        await Tree.LoadAsync();
        var first = Tree.Favorites.FirstOrDefault();
        if (first is not null)
        {
            Tree.SelectedNode = first;
            // 显式 await：PropertyChanged 里的 fire-and-forget 在启动时可能尚未把列表灌上
            await ShowNodeAsync(first);
        }

        IsInitialized = true;
    }

    private async Task ShowNodeAsync(MailboxNodeViewModel node)
    {
        List.SelectedMessage = null;
        if (node.Kind == MailboxNodeKind.Outbox)
        {
            IsOutboxMode = true;
            List.Title = UiStrings.FolderOutbox;
            List.Subtitle = string.Empty;
            List.ShowCategoryTabs = false;
            await Outbox.ReloadAsync();
            return;
        }

        IsOutboxMode = false;
        _isDraftsView = node.Kind == MailboxNodeKind.Drafts || (node.Kind == MailboxNodeKind.Folder && node.Special == SpecialFolder.Drafts);
        await List.ShowAsync(node, Tree.ResolveFolderIds(node));
    }

    private async Task ShowNodeSafeAsync(MailboxNodeViewModel node)
    {
        try
        {
            await ShowNodeAsync(node);
        }
        catch (Exception ex)
        {
            ShowStatus($"无法打开文件夹：{ex.Message}", true);
        }
    }

    private bool _isDraftsView;

    /// <summary>
    /// 在三栏里打开某封邮件（来自系统通知的点击）：切到它所在的文件夹 / 分类标签，清掉搜索，选中该行。
    /// 邮件已不在本地（被删除 / 移走）或不在最近 200 封里时，在状态条提示并返回 false。
    /// </summary>
    public async Task<bool> OpenMessageAsync(string messageId)
    {
        var message = await _database.GetMessageAsync(messageId);
        if (message is null)
        {
            ShowStatus("这封邮件已经不在本机了（可能已被删除或移动）。", true);
            return false;
        }

        var node = Tree.FindNode(n => n.Kind == MailboxNodeKind.Folder && n.FolderId == message.FolderId);
        if (node is null)
        {
            ShowStatus("找不到这封邮件所在的文件夹。", true);
            return false;
        }

        IsOutboxMode = false;
        if (!ReferenceEquals(Tree.SelectedNode, node))
        {
            Tree.SelectedNode = node;
        }

        List.SearchText = string.Empty;
        if (node.ShowsCategories)
        {
            var categories = MessageListViewModel.Categories.ToList();
            var index = categories.IndexOf(message.Category ?? MailCategory.Primary);
            List.SelectedCategoryIndex = Math.Max(0, index);
        }

        await List.ReloadAsync();
        var row = List.Messages.FirstOrDefault(r => r.Id == messageId);
        if (row is null)
        {
            ShowStatus("这封邮件不在当前列表的最近邮件里，请在收件箱中查找。", true);
            return false;
        }

        List.SelectedMessage = row;
        return true;
    }

    /// <summary>系统通知上的“标为已读”：走与界面相同的待写回路径（本地立即生效，随后同步到服务器）。</summary>
    public Task MarkReadByIdAsync(string messageId) => Actions.SetReadAsync(messageId, true);

    /// <summary>系统通知上的“删除”：与界面相同——移入已删除文件夹（没有则永久删除）。</summary>
    public Task<bool> DeleteByIdAsync(string messageId) => Actions.DeleteAsync(messageId);

    private async Task OnDraftsChangedAsync()
    {
        await Tree.RefreshCountsAsync();
        if (_isDraftsView && !IsOutboxMode && !List.IsSearching)
        {
            await List.ReloadAsync();
        }
    }

    private async Task OnMutatedAsync(MessageMutationEventArgs e)
    {
        switch (e.Mutation)
        {
            case MessageMutation.Read:
                List.UpdateRow(e.MessageId, isUnread: false);
                break;
            case MessageMutation.Unread:
                List.UpdateRow(e.MessageId, isUnread: true);
                break;
            case MessageMutation.Flagged:
                List.UpdateRow(e.MessageId, isFlagged: true);
                break;
            case MessageMutation.Unflagged:
                List.UpdateRow(e.MessageId, isFlagged: false);
                break;
            case MessageMutation.Archived:
            case MessageMutation.Deleted:
                ShowStatus(e.Mutation == MessageMutation.Archived ? UiStrings.Archived
                    : MessageActions.IsLocalDraft(e.MessageId) ? UiStrings.DraftDeleted : UiStrings.Deleted);
                List.RemoveRow(e.MessageId);
                break;
        }

        await Tree.RefreshCountsAsync();
    }

    [RelayCommand]
    private void NewMessage() => ComposeRequested?.Invoke(this, ComposeRequest.New());

    [RelayCommand]
    private async Task RefreshAsync()
    {
        if (_syncService is not null && HasRealAccounts)
        {
            await SyncAsync(null, userInitiated: true);
            return;
        }

        await Tree.RefreshCountsAsync();
        await List.ReloadAsync();
    }

    // ───────── 添加 / 移除账号 ─────────

    public AddAccountViewModel CreateAddAccountViewModel()
    {
        if (_accountSetup is null)
        {
            throw new InvalidOperationException("未配置账号设置服务。");
        }

        return new AddAccountViewModel(_accountSetup, _settings);
    }

    public void DismissFirstRunPrompt()
        => _settings?.Update(s => s.FirstRunPromptDismissed = true);

    /// <summary>向导完成后：刷新左栏并在后台开始首次同步。</summary>
    public async Task OnAccountAddedAsync(AccountEntity account)
    {
        await RefreshAccountFlagsAsync();
        await Tree.ReloadPreservingSelectionAsync();
        ShowStatus($"已添加账号「{account.DisplayName}」，正在后台同步…");
        _selectInboxOfAccount = account.Id;
        _ = SyncAsync(account.Id, userInitiated: false);
    }

    /// <summary>重命名账号显示名；空名称回退到邮箱本地部分。</summary>
    public async Task RenameAccountAsync(Guid accountId, string? displayName)
    {
        var account = await _database.GetAccountAsync(accountId)
            ?? throw new InvalidOperationException("账号不存在或已删除。");
        var name = HtmlPreview.ResolveDisplayName(displayName, account.EmailAddress);
        await _database.UpsertAccountAsync(account with
        {
            DisplayName = name,
            UpdatedAt = DateTimeOffset.Now
        });
        await Tree.ReloadPreservingSelectionAsync();
        ShowStatus($"已重命名为「{name}」。");
    }

    public async Task RemoveAccountAsync(Guid accountId)
    {
        var name = Tree.Accounts.FirstOrDefault(a => a.AccountId == accountId)?.Title ?? string.Empty;
        if (_accountSetup is not null)
        {
            await _accountSetup.RemoveAccountAsync(accountId, CancellationToken.None);
        }
        else
        {
            await _database.DeleteAccountAsync(accountId);
        }

        _settings?.SetAccountColor(accountId, null);
        await RefreshAccountFlagsAsync();
        // 选中项属于被移除的账号时回到收藏夹
        if (Tree.SelectedNode?.AccountId == accountId)
        {
            Tree.SelectedNode = null;
            List.SelectedMessage = null;
        }

        await Tree.ReloadPreservingSelectionAsync();
        if (Tree.SelectedNode is null && Tree.Favorites.FirstOrDefault() is { } first)
        {
            Tree.SelectedNode = first;
        }

        ShowStatus(string.IsNullOrEmpty(name) ? "已移除账号。" : $"已移除账号「{name}」。");
    }

    public async Task ClearDemoDataAsync()
    {
        await DemoDataSeeder.ClearDemoDataAsync(_database);
        _settings?.Update(s => s.DemoDataDismissed = true);
        await RefreshAccountFlagsAsync();
        if (Tree.SelectedNode?.AccountId is { } id && DemoDataSeeder.IsDemoAccount(id))
        {
            Tree.SelectedNode = null;
            List.SelectedMessage = null;
        }

        await Tree.ReloadPreservingSelectionAsync();
        if (Tree.SelectedNode is null && Tree.Favorites.FirstOrDefault() is { } first)
        {
            Tree.SelectedNode = first;
        }

        ShowStatus("已清除演示数据。");
    }

    private async Task RefreshAccountFlagsAsync()
    {
        var accounts = await _database.ListAccountsAsync();
        HasRealAccounts = accounts.Any(a => !DemoDataSeeder.IsDemoAccount(a.Id));
        HasDemoData = accounts.Any(a => DemoDataSeeder.IsDemoAccount(a.Id));
        OnPropertyChanged(nameof(ShouldShowFirstRunPrompt));
    }

    // ───────── 同步 ─────────

    /// <summary>同步真实账号（accountId 为 null 表示全部）；同一时间只跑一个，错误通过状态条提示。</summary>
    public async Task SyncAsync(Guid? accountId, bool userInitiated)
    {
        if (_syncService is null)
        {
            return;
        }

        if (Interlocked.Exchange(ref _syncRunning, 1) == 1)
        {
            if (userInitiated)
            {
                ShowStatus("同步正在进行中…");
            }

            return;
        }

        IsSyncing = true;
        IsSyncIndeterminate = true;
        SyncStatusText = "正在同步…";
        string? failure = null;
        try
        {
            // 手动同步 = 强制对账：忽略“多久对账一次”的节流，把服务器上的已读 / 星标 / 删除 / 移动都拉回来
            var request = new SyncRequest(Force: userInitiated);
            if (accountId is { } id)
            {
                await Task.Run(() => _syncService is IIncrementalSyncService inc
                    ? inc.SyncAccountAsync(id, request)
                    : _syncService.SyncAccountAsync(id));
            }
            else
            {
                await Task.Run(() => _syncService is IIncrementalSyncService inc
                    ? inc.SyncAllAsync(request)
                    : _syncService.SyncAllAsync());
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            failure = ex.Message;
        }
        finally
        {
            Interlocked.Exchange(ref _syncRunning, 0);
            IsSyncing = false;
            if (failure is null)
            {
                SyncStatusText = string.Empty;
            }
        }

        // 同步可能新增 / 删除了文件夹：有变化才重建树，否则只更新数字；之后原地刷新当前列表
        if (_selectInboxOfAccount is null)
        {
            await Tree.RefreshAfterSyncAsync();
        }
        else
        {
            await Tree.ReloadPreservingSelectionAsync();
        }

        if (_selectInboxOfAccount is { } target)
        {
            _selectInboxOfAccount = null;
            var inbox = Tree.FindNode(n => n.AccountId == target && n.Special == SpecialFolder.Inbox);
            if (inbox is not null && failure is null)
            {
                Tree.SelectedNode = inbox;
            }
        }
        else
        {
            // 原地刷新：不清空列表，选中项和滚动位置保持不变
            await List.RefreshInPlaceAsync();
            await Reading.RefreshStateAsync();
        }

        if (failure is not null)
        {
            ShowStatus($"同步失败：{failure}", error: true);
        }
        else if (userInitiated || accountId is not null)
        {
            ShowStatus("同步完成。");
        }
    }

    private int _backgroundRefresh;
    private int _backgroundRefreshAgain;

    /// <summary>后台自动同步（轮询 / IDLE）改动了本地库后，静默刷新界面：文件夹计数 + 当前列表（原地）+ 阅读窗格状态。</summary>
    private async Task RefreshAfterBackgroundSyncAsync()
    {
        if (Interlocked.Exchange(ref _backgroundRefresh, 1) == 1)
        {
            Volatile.Write(ref _backgroundRefreshAgain, 1);      // 正在刷新：结束后再来一遍，不丢这次变化
            return;
        }

        try
        {
            do
            {
                Volatile.Write(ref _backgroundRefreshAgain, 0);
                if (Volatile.Read(ref _syncRunning) == 1)
                {
                    break;      // 手动同步在跑，它结束后自己会刷新
                }

                await Tree.RefreshAfterSyncAsync();
                await List.RefreshInPlaceAsync();
                await Reading.RefreshStateAsync();
            }
            while (Volatile.Read(ref _backgroundRefreshAgain) == 1);
        }
        catch (Exception)
        {
            // 后台刷新失败不打扰用户；下一次同步会再刷新
        }
        finally
        {
            Interlocked.Exchange(ref _backgroundRefresh, 0);
        }
    }

    private void OnSyncProgress(SyncProgress p)
    {
        if (p.Background)
        {
            // 后台同步不弹“正在同步”提示、不报错；只有改动了本地库时才静默刷新界面
            if (p.Phase == SyncPhase.Completed && p.Changed)
            {
                _ = RefreshAfterBackgroundSyncAsync();
            }

            return;
        }

        switch (p.Phase)
        {
            case SyncPhase.Started:
            case SyncPhase.Progress:
                if (Volatile.Read(ref _syncRunning) == 0)
                {
                    break; // 迟到的进度事件
                }

                IsSyncing = true;
                SyncStatusText = $"{p.AccountName}：{p.Message}";
                IsSyncIndeterminate = p.Total <= 0;
                SyncProgressValue = p.Total > 0 ? Math.Min(100d, 100d * p.Done / p.Total) : 0;
                break;
            case SyncPhase.Completed:
                SyncStatusText = $"{p.AccountName}：{p.Message}";
                break;
            case SyncPhase.Failed:
                SyncStatusText = $"{p.AccountName}：同步失败 — {p.Error ?? p.Message}";
                break;
        }
    }

    private void ShowStatus(string message, bool error = false)
    {
        IsStatusError = error;
        StatusMessage = message;
        IsStatusOpen = true;
    }

    private void Post(Action action) => UiMarshal.Post(_ui, action);

    [RelayCommand]
    private void DismissStatus() => IsStatusOpen = false;

    public void NotifySent()
    {
        ShowStatus(UiStrings.SentQueued);
    }

    // ───────── 草稿：重新打开 ─────────

    /// <summary>
    /// 继续编辑一份草稿（列表双击 / Enter / 阅读窗格“继续编辑”）。
    /// 本地草稿从 drafts 表还原全部状态；服务器草稿则由邮件行 + 收件人生成，并分配本地草稿 Id（已为它建过本地草稿就接着用那份）。
    /// 该草稿的窗口已经打开时只激活那个窗口。
    /// </summary>
    public async Task OpenDraftAsync(string messageId)
    {
        string? draftId = null;
        if (MessageActions.IsLocalDraft(messageId))
        {
            draftId = messageId[MailDatabase.DraftMessagePrefix.Length..];
        }
        else
        {
            if (ComposeWindows.TryActivate("src:" + messageId, w => w.Activate()))
            {
                return;
            }

            draftId = await _database.FindDraftIdBySourceAsync(messageId).ConfigureAwait(false);
        }

        if (draftId is not null)
        {
            if (ComposeWindows.TryActivate("draft:" + draftId, w => w.Activate()))
            {
                return;
            }

            var draft = _draftStore is null ? null : await _draftStore.GetAsync(draftId).ConfigureAwait(false);
            if (draft is null)
            {
                ShowStatus(UiStrings.DraftGone, true);
                await Tree.RefreshCountsAsync().ConfigureAwait(false);
                await List.ReloadAsync().ConfigureAwait(false);
                return;
            }

            ComposeRequested?.Invoke(this, ComposeRequest.FromDraft(draft));
            return;
        }

        var message = await _database.GetMessageAsync(messageId).ConfigureAwait(false);
        if (message is null)
        {
            ShowStatus(UiStrings.DraftGone, true);
            return;
        }

        var recipients = await _database.ListRecipientsAsync(messageId).ConfigureAwait(false);
        ComposeRequested?.Invoke(this, ComposeRequest.FromServerDraft(message, recipients, Guid.NewGuid().ToString("N")));
    }

    /// <summary>
    /// 打开写信窗口：该草稿已有窗口时激活它并返回，否则创建 ViewModel + 窗口并按草稿键登记，窗口关闭时注销。
    /// <paramref name="createWindow"/> 由 UI 层提供（创建并显示窗口）。
    /// </summary>
    public async Task<IComposeWindow> OpenComposeWindowAsync(ComposeRequest request, Func<ComposeViewModel, IComposeWindow> createWindow)
    {
        await _composeOpenGate.WaitAsync();
        try
        {
            foreach (var key in request.WindowKeys)
            {
                if (ComposeWindows.TryGet(key, out var existing) && existing is not null)
                {
                    existing.Activate();
                    return existing;
                }
            }

            var vm = await CreateComposeAsync(request);
            var window = createWindow(vm);
            var keys = vm.WindowKeys;
            foreach (var key in keys)
            {
                ComposeWindows.Register(key, window);
            }

            window.OnClosed(() =>
            {
                foreach (var key in keys)
                {
                    ComposeWindows.Unregister(key, window);
                }
            });
            return window;
        }
        finally
        {
            _composeOpenGate.Release();
        }
    }

    // ───────── 搜索 ─────────

    /// <summary>用户提交了搜索（回车 / 点放大镜）：记入最近搜索。</summary>
    public void CommitSearch()
    {
        var text = List.SearchText?.Trim();
        if (!string.IsNullOrEmpty(text) && List.IsSearching)
        {
            RecentSearches.Add(text);
        }
    }

    /// <summary>搜索框下拉建议：最近的搜索，按输入内容过滤（输入为空时全部列出）。</summary>
    public IReadOnlyList<string> GetSearchSuggestions(string? typed)
    {
        var all = RecentSearches.GetAll();
        var t = typed?.Trim();
        return string.IsNullOrEmpty(t)
            ? all
            : all.Where(s => s.Contains(t, StringComparison.OrdinalIgnoreCase) && !string.Equals(s, t, StringComparison.OrdinalIgnoreCase)).ToList();
    }

    public async Task ClearSearchAsync() => await List.ClearSearchAsync();

    public async Task<ComposeViewModel> CreateComposeAsync(ComposeRequest request)
    {
        var accounts = await _database.ListAccountsAsync();
        return new ComposeViewModel(
            _sendService,
            accounts.Where(a => a.IsEnabled).Select(a => new ComposeAccountOption(a.Id, $"{a.DisplayName} <{a.EmailAddress}>", a.ProviderId, a.EmailAddress)),
            request,
            _draftStore);
    }
}
