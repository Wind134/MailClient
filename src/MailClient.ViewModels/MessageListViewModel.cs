using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using MailClient.Core;
using MailClient.Core.Search;
using MailClient.Storage;

namespace MailClient.ViewModels;

/// <summary>搜索范围。</summary>
public enum SearchScope
{
    CurrentFolder,
    CurrentAccount,
    AllMailboxes
}

/// <summary>中栏：分类标签 + 搜索 + 邮件列表。</summary>
public sealed partial class MessageListViewModel : ObservableObject
{
    private static readonly MailCategory[] TabOrder =
        [MailCategory.Primary, MailCategory.Transactions, MailCategory.Updates, MailCategory.Promotions];

    private readonly MailDatabase _database;
    private readonly Func<DateTimeOffset> _clock;
    private SynchronizationContext? _ui = SynchronizationContext.Current;

    /// <summary>在 UI 线程就绪后注入（WinUI 构造 Shell 时 SynchronizationContext 往往还是 null）。</summary>
    public void AttachSynchronizationContext(SynchronizationContext? context = null)
        => _ui = context ?? SynchronizationContext.Current ?? _ui;
    private readonly SemaphoreSlim _loadGate = new(1, 1);
    private int _loadVersion;
    private CancellationTokenSource? _searchDebounce;
    private MailboxNodeViewModel? _node;
    private IReadOnlyList<string>? _folderIds;
    private bool _suppressSelectionEvent;

    public MessageListViewModel(MailDatabase database, Func<DateTimeOffset>? clock = null)
    {
        _database = database;
        _clock = clock ?? (() => DateTimeOffset.Now);
    }

    public ObservableCollection<MessageRowViewModel> Messages { get; } = new();

    public static IReadOnlyList<MailCategory> Categories => TabOrder;

    [ObservableProperty]
    private string _title = UiStrings.AllInboxes;

    [ObservableProperty]
    private string _subtitle = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowCategoryBar))]
    private bool _showCategoryTabs;

    [ObservableProperty]
    private int _selectedCategoryIndex;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSearching), nameof(ShowCategoryBar), nameof(EmptyMessage))]
    private string _searchText = string.Empty;

    [ObservableProperty]
    private int _searchScopeIndex;

    /// <summary>当前搜索框里的内容是否构成有效查询（只有未写完的 "from:" 之类不算）。</summary>
    public bool IsSearching => !SearchQueryParser.Parse(SearchText).IsEmpty;

    public SearchScope Scope => (SearchScope)Math.Clamp(SearchScopeIndex, 0, 2);

    public static IReadOnlyList<string> ScopeNames { get; } = [UiStrings.ScopeFolder, UiStrings.ScopeAccount, UiStrings.ScopeAll];

    /// <summary>搜索时不显示分类标签（搜索跨分类）。</summary>
    public bool ShowCategoryBar => ShowCategoryTabs && !IsSearching;

    /// <summary>列表为空时的提示：搜索中 = “没有找到邮件”。</summary>
    public string EmptyMessage => IsSearching ? UiStrings.NoResults : UiStrings.EmptyFolder;

    /// <summary>当前搜索要高亮的词（列表行、阅读窗格共用）。</summary>
    [ObservableProperty]
    private SearchHighlightTerms _highlightTerms = SearchHighlightTerms.Empty;

    [ObservableProperty]
    private MessageRowViewModel? _selectedMessage;

    [ObservableProperty]
    private bool _isEmpty;

    [ObservableProperty]
    private bool _isLoading;

    public MailCategory SelectedCategory => TabOrder[Math.Clamp(SelectedCategoryIndex, 0, TabOrder.Length - 1)];

    partial void OnSelectedCategoryIndexChanged(int value) => _ = ReloadAsync();

    partial void OnSearchTextChanged(string value) => _ = DebouncedReloadAsync();

    partial void OnSearchScopeIndexChanged(int value)
    {
        if (IsSearching)
        {
            _ = ReloadAsync();
        }
    }

    /// <summary>防抖间隔（测试里可调小）。</summary>
    public TimeSpan SearchDelay { get; set; } = TimeSpan.FromMilliseconds(250);

    /// <summary>清空搜索（Esc）：立即恢复文件夹列表，不等防抖。</summary>
    public async Task ClearSearchAsync()
    {
        if (string.IsNullOrEmpty(SearchText))
        {
            return;
        }

        _searchDebounce?.Cancel();
        SearchText = string.Empty;
        _searchDebounce?.Cancel();
        await ReloadAsync();
    }

    /// <summary>把当前节点 + 范围 + 搜索词变成数据库过滤条件。</summary>
    public MessageListFilter BuildFilter(MailboxNodeViewModel node, IReadOnlyList<string>? folderIds, string? searchText, SearchScope scope, MailCategory? category, int limit = 200)
    {
        var searching = !SearchQueryParser.Parse(searchText).IsEmpty;
        if (!searching)
        {
            return new MessageListFilter(
                FolderIds: folderIds,
                Category: category,
                UnreadOnly: node.Kind == MailboxNodeKind.Unread,
                FlaggedOnly: node.Kind == MailboxNodeKind.Flagged,
                SearchText: null,
                Limit: limit);
        }

        // 在“已删除 / 垃圾邮件”文件夹里搜索，就要搜到它们；跨文件夹搜索默认不含这两类
        var inTrashOrJunk = node.Special is SpecialFolder.Trash or SpecialFolder.Junk;
        switch (scope)
        {
            case SearchScope.CurrentAccount when node.AccountId is { } accountId:
                return new MessageListFilter(null, null, false, false, searchText, limit, accountId, !inTrashOrJunk);
            case SearchScope.CurrentAccount:
            case SearchScope.AllMailboxes:
                return new MessageListFilter(null, null, false, false, searchText, limit, null, !inTrashOrJunk);
            default:
                return new MessageListFilter(
                    FolderIds: folderIds,
                    Category: null,
                    UnreadOnly: node.Kind == MailboxNodeKind.Unread,
                    FlaggedOnly: node.Kind == MailboxNodeKind.Flagged,
                    SearchText: searchText,
                    Limit: limit);
        }
    }

    private async Task DebouncedReloadAsync()
    {
        _searchDebounce?.Cancel();
        var cts = _searchDebounce = new CancellationTokenSource();
        try
        {
            await Task.Delay(SearchDelay, cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        await ReloadAsync();
    }

    /// <summary>切换到某个邮箱节点。</summary>
    public async Task ShowAsync(MailboxNodeViewModel node, IReadOnlyList<string>? folderIds)
    {
        _ui ??= SynchronizationContext.Current;
        _node = node;
        _folderIds = folderIds;
        Title = node.Title;
        ShowCategoryTabs = node.ShowsCategories;
        SearchText = string.Empty;
        await ReloadAsync();
    }

    public async Task ReloadAsync()
    {
        if (_node is null)
        {
            return;
        }

        var version = Interlocked.Increment(ref _loadVersion);
        await _loadGate.WaitAsync();
        try
        {
            if (version != Volatile.Read(ref _loadVersion))
            {
                return; // 已有更新的请求
            }

            IsLoading = true;
            var node = _node;
            var searchText = SearchText;
            var query = SearchQueryParser.Parse(searchText);
            var searching = !query.IsEmpty;
            var scope = Scope;
            var filter = BuildFilter(node, _folderIds, searchText, scope, ShowCategoryTabs ? SelectedCategory : null);

            IReadOnlyList<MessageEntity> entities;
            string? searchError = null;
            try
            {
                entities = await _database.QueryMessagesAsync(filter);
            }
            catch (Exception ex) when (searching && ex is not OperationCanceledException)
            {
                entities = Array.Empty<MessageEntity>();
                searchError = ex.Message;
            }

            if (version != Volatile.Read(ref _loadVersion))
            {
                return;
            }

            var terms = searching ? SearchHighlightTerms.From(query) : SearchHighlightTerms.Empty;
            var snippets = searching && !terms.IsEmpty ? await LoadSnippetsAsync(entities, terms) : null;
            if (version != Volatile.Read(ref _loadVersion))
            {
                return;
            }

            var now = _clock();
            var previousId = SelectedMessage?.Id;
            var rows = new List<MessageRowViewModel>(entities.Count);
            foreach (var entity in entities)
            {
                var row = new MessageRowViewModel(entity, now, terms);
                if (snippets is not null && snippets.TryGetValue(entity.Id, out var snippet))
                {
                    row.UseSnippet(snippet, terms.Body);
                }

                rows.Add(row);
            }

            void apply()
            {
                _suppressSelectionEvent = true;
                try
                {
                    // 先清选中，避免 Clear/Reset 时 TwoWay SelectedItem 仍指向已移除行而触发绑定异常
                    SelectedMessage = null;
                    Messages.Clear();
                    foreach (var row in rows)
                    {
                        Messages.Add(row);
                    }

                    SelectedMessage = previousId is null ? null : Messages.FirstOrDefault(m => m.Id == previousId);
                }
                finally
                {
                    _suppressSelectionEvent = false;
                }
            }

            var ui = _ui ?? SynchronizationContext.Current;
            if (!UiMarshal.ShouldPost(ui))
            {
                apply();
            }
            else
            {
                var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                ui!.Post(_ =>
                {
                    try
                    {
                        apply();
                        done.SetResult();
                    }
                    catch (Exception ex)
                    {
                        done.SetException(ex);
                    }
                }, null);
                await done.Task.ConfigureAwait(false);
            }

            HighlightTerms = terms;
            IsEmpty = Messages.Count == 0;
            Subtitle = searchError is not null
                ? $"搜索出错：{searchError}"
                : searching
                    ? UiStrings.SearchSummary(Messages.Count, ScopeNames[(int)scope], Messages.Count >= filter.Limit)
                    : IsEmpty ? string.Empty : UiStrings.UnreadCount(Messages.Count(m => m.IsUnread));
            OnPropertyChanged(nameof(EmptyMessage));
            if (previousId is not null && SelectedMessage is null)
            {
                SelectionChangedExternally?.Invoke(this, EventArgs.Empty);
            }
        }
        finally
        {
            IsLoading = false;
            _loadGate.Release();
        }
    }

    /// <summary>
    /// 同步之后“原地”刷新当前列表：只增删 / 移动变化的行、就地更新已读和星标，行对象保持不变，
    /// 所以选中项和滚动位置不会丢。搜索结果不动（高亮片段依赖整表重建）。
    /// 服务器上已删除 / 移走的邮件若正被选中，选中清空（阅读窗格随之清空）。
    /// </summary>
    public async Task RefreshInPlaceAsync()
    {
        if (_node is null || IsSearching)
        {
            return;
        }

        var version = Volatile.Read(ref _loadVersion);
        await _loadGate.WaitAsync();
        try
        {
            if (version != Volatile.Read(ref _loadVersion))
            {
                return; // 期间有更新的整表加载，它已经是最新的了
            }

            var filter = BuildFilter(_node, _folderIds, null, Scope, ShowCategoryTabs ? SelectedCategory : null);
            var entities = await _database.QueryMessagesAsync(filter);
            if (version != Volatile.Read(ref _loadVersion))
            {
                return;
            }

            var selectedId = SelectedMessage?.Id;
            var selectionLost = false;
            _suppressSelectionEvent = true;
            try
            {
                var fresh = entities.ToDictionary(e => e.Id);
                for (var i = Messages.Count - 1; i >= 0; i--)
                {
                    if (!fresh.ContainsKey(Messages[i].Id))
                    {
                        selectionLost |= Messages[i].Id == selectedId;
                        Messages.RemoveAt(i);
                    }
                }

                var now = _clock();
                for (var target = 0; target < entities.Count; target++)
                {
                    var entity = entities[target];
                    if (target < Messages.Count && Messages[target].Id == entity.Id)
                    {
                        Messages[target].IsUnread = !entity.IsRead;
                        Messages[target].IsFlagged = entity.IsFlagged;
                        continue;
                    }

                    var existing = -1;
                    for (var j = target + 1; j < Messages.Count; j++)
                    {
                        if (Messages[j].Id == entity.Id)
                        {
                            existing = j;
                            break;
                        }
                    }

                    if (existing >= 0)
                    {
                        Messages.Move(existing, target);
                        Messages[target].IsUnread = !entity.IsRead;
                        Messages[target].IsFlagged = entity.IsFlagged;
                    }
                    else
                    {
                        Messages.Insert(target, new MessageRowViewModel(entity, now));
                    }
                }

                if (selectionLost)
                {
                    SelectedMessage = null;
                }
            }
            finally
            {
                _suppressSelectionEvent = false;
            }

            IsEmpty = Messages.Count == 0;
            Subtitle = IsEmpty ? string.Empty : UiStrings.UnreadCount(Messages.Count(m => m.IsUnread));
            OnPropertyChanged(nameof(EmptyMessage));
            if (selectionLost)
            {
                SelectionChangedExternally?.Invoke(this, EventArgs.Empty);
            }
        }
        finally
        {
            _loadGate.Release();
        }
    }

    /// <summary>
    /// 主题 / 发件人 / 预览都没命中、只在正文里命中的邮件：取正文里围绕命中处的片段当作预览。
    /// 同步目前只保存约 200 字预览（见 docs/drafts-and-search.md），所以这主要对本地草稿和已有正文的邮件生效。
    /// </summary>
    private async Task<Dictionary<string, string>?> LoadSnippetsAsync(IReadOnlyList<MessageEntity> entities, SearchHighlightTerms terms)
    {
        if (terms.Body.Count == 0)
        {
            return null;
        }

        var candidates = entities
            .Where(e => !HighlightSpans.HasHit(e.Preview, terms.Body) && !HighlightSpans.HasHit(e.Subject, terms.All))
            .Select(e => e.Id)
            .ToList();
        if (candidates.Count == 0)
        {
            return null;
        }

        var bodies = await _database.GetBodyTextsAsync(candidates);
        var result = new Dictionary<string, string>();
        foreach (var (id, body) in bodies)
        {
            if (SnippetBuilder.Build(body, terms.Body) is { } snippet)
            {
                result[id] = snippet;
            }
        }

        return result.Count == 0 ? null : result;
    }

    /// <summary>选中项变化（排除重载列表时的程序性清空）。</summary>
    public event EventHandler? SelectionChangedExternally;

    partial void OnSelectedMessageChanged(MessageRowViewModel? value)
    {
        if (!_suppressSelectionEvent)
        {
            SelectionChangedExternally?.Invoke(this, EventArgs.Empty);
        }
    }

    public void RemoveRow(string messageId)
    {
        var row = Messages.FirstOrDefault(m => m.Id == messageId);
        if (row is null)
        {
            return;
        }

        var index = Messages.IndexOf(row);
        Messages.Remove(row);
        IsEmpty = Messages.Count == 0;
        Subtitle = IsEmpty ? string.Empty : UiStrings.UnreadCount(Messages.Count(m => m.IsUnread));
        if (SelectedMessage is null && Messages.Count > 0)
        {
            SelectedMessage = Messages[Math.Min(index, Messages.Count - 1)];
        }
    }

    public void UpdateRow(string messageId, bool? isUnread = null, bool? isFlagged = null)
    {
        var row = Messages.FirstOrDefault(m => m.Id == messageId);
        if (row is null)
        {
            return;
        }

        if (isUnread is { } u)
        {
            row.IsUnread = u;
        }

        if (isFlagged is { } f)
        {
            row.IsFlagged = f;
        }

        Subtitle = UiStrings.UnreadCount(Messages.Count(m => m.IsUnread));
    }
}
