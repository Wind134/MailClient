using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using MailClient.Core;
using MailClient.Storage;
using MailClient.Storage.Demo;

namespace MailClient.ViewModels;

/// <summary>左栏：收藏夹 + 各账号文件夹。</summary>
public sealed partial class MailboxTreeViewModel : ObservableObject
{
    private readonly MailDatabase _database;
    private IReadOnlyList<FolderEntity> _allFolders = Array.Empty<FolderEntity>();

    public MailboxTreeViewModel(MailDatabase database) => _database = database;

    public ObservableCollection<MailboxNodeViewModel> Favorites { get; } = new();
    public ObservableCollection<MailboxNodeViewModel> Accounts { get; } = new();

    [ObservableProperty]
    private MailboxNodeViewModel? _selectedNode;

    /// <summary>按账号 Id 取颜色（由 Shell 注入，来自应用设置）。</summary>
    public Func<Guid, string?>? ColorResolver { get; set; }

    /// <summary>树结构重建完成（App 据此重新生成 NavigationView 菜单）。</summary>
    public event EventHandler? Rebuilt;

    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        var accounts = await _database.ListAccountsAsync(cancellationToken);
        _allFolders = await _database.ListAllFoldersAsync(cancellationToken);

        Favorites.Clear();
        Accounts.Clear();

        var inboxUnread = _allFolders.Where(f => f.Special == SpecialFolder.Inbox).Sum(f => f.UnreadCount);
        Favorites.Add(new MailboxNodeViewModel(MailboxNodeKind.AllInboxes, UiStrings.AllInboxes, "\uE715") { UnreadCount = inboxUnread });
        Favorites.Add(new MailboxNodeViewModel(MailboxNodeKind.Flagged, UiStrings.Flagged, "\uE7C1"));
        Favorites.Add(new MailboxNodeViewModel(MailboxNodeKind.Unread, UiStrings.Unread, "\uE8C3") { UnreadCount = _allFolders.Sum(f => f.Special is SpecialFolder.Inbox or SpecialFolder.Custom or SpecialFolder.Archive ? f.UnreadCount : 0) });

        Favorites.Add(new MailboxNodeViewModel(MailboxNodeKind.Drafts, UiStrings.FolderDrafts, "\uE70F") { UnreadCount = DraftTotal() });

        var outboxCount = await _database.CountOutboxItemsAsync(cancellationToken);
        if (outboxCount > 0)
        {
            Favorites.Add(NewOutboxNode(outboxCount));
        }

        var enabled = accounts.Where(a => a.IsEnabled).ToList();
        var colors = AccountPalette.Assign(enabled.Select(a => (a.Id, ColorResolver?.Invoke(a.Id))));
        foreach (var account in enabled)
        {
            var accountNode = new MailboxNodeViewModel(MailboxNodeKind.Account, account.DisplayName, "\uE77B")
            {
                AccountId = account.Id,
                EmailAddress = account.EmailAddress,
                ProviderId = account.ProviderId,
                IsDemo = DemoDataSeeder.IsDemoAccount(account.Id),
                ColorHex = colors[account.Id],
                AvatarLetter = AccountPalette.LetterFor(account.DisplayName, account.EmailAddress)
            };

            foreach (var folder in _allFolders
                         .Where(f => f.AccountId == account.Id)
                         .OrderBy(f => MailboxNodeViewModel.OrderFor(f.Special))
                         .ThenBy(f => f.SortOrder)
                         .ThenBy(f => f.Name))
            {
                accountNode.Children.Add(new MailboxNodeViewModel(
                    MailboxNodeKind.Folder,
                    MailboxNodeViewModel.TitleFor(folder.Special, folder.Name),
                    MailboxNodeViewModel.GlyphFor(folder.Special))
                {
                    AccountId = account.Id,
                    FolderId = folder.Id,
                    ProviderId = account.ProviderId,
                    Special = folder.Special,
                    // 草稿显示总数，其余显示未读数
                    UnreadCount = folder.Special == SpecialFolder.Drafts ? folder.TotalCount : folder.UnreadCount
                });
            }

            accountNode.UnreadCount = accountNode.Children.FirstOrDefault(c => c.Special == SpecialFolder.Inbox)?.UnreadCount ?? 0;
            Accounts.Add(accountNode);
        }

        UpdateBadgeVisibility();
        Rebuilt?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>「未读」徽标仅在数字与「所有收件箱」不同时显示。</summary>
    private void UpdateBadgeVisibility()
    {
        var all = Favorites.FirstOrDefault(n => n.Kind == MailboxNodeKind.AllInboxes);
        var unread = Favorites.FirstOrDefault(n => n.Kind == MailboxNodeKind.Unread);
        if (all is not null && unread is not null)
        {
            unread.ShowBadge = unread.UnreadCount != all.UnreadCount;
        }
    }

    /// <summary>
    /// 重建树并尽量保持选中项（按 种类 + 账号 + 文件夹 匹配）；找不到则回到第一个收藏项。
    /// </summary>
    public async Task ReloadPreservingSelectionAsync(CancellationToken cancellationToken = default)
    {
        var previous = SelectedNode;
        await LoadAsync(cancellationToken);
        if (previous is null)
        {
            return;
        }

        var match = FindNode(n => n.IsSelectable && n.Kind == previous.Kind && n.AccountId == previous.AccountId && n.FolderId == previous.FolderId)
                    ?? Favorites.FirstOrDefault();
        SelectedNode = match;
    }

    /// <summary>所有账号草稿文件夹的邮件总数（含本地草稿）。</summary>
    private int DraftTotal() => _allFolders.Where(f => f.Special == SpecialFolder.Drafts).Sum(f => f.TotalCount);

    private static MailboxNodeViewModel NewOutboxNode(int count)
        => new(MailboxNodeKind.Outbox, UiStrings.FolderOutbox, "\uE724") { UnreadCount = count };

    /// <summary>发件箱非空时才在收藏夹里出现；变空后自动移除（若正选中则回到第一个收藏项）。</summary>
    public async Task RefreshOutboxNodeAsync(CancellationToken cancellationToken = default)
    {
        var count = await _database.CountOutboxItemsAsync(cancellationToken);
        var node = Favorites.FirstOrDefault(n => n.Kind == MailboxNodeKind.Outbox);
        if (count > 0 && node is null)
        {
            Favorites.Add(NewOutboxNode(count));
            Rebuilt?.Invoke(this, EventArgs.Empty);
        }
        else if (count == 0 && node is not null)
        {
            var wasSelected = SelectedNode == node;
            Favorites.Remove(node);
            if (wasSelected)
            {
                SelectedNode = Favorites.FirstOrDefault();
            }

            Rebuilt?.Invoke(this, EventArgs.Empty);
        }
        else if (node is not null)
        {
            node.UnreadCount = count;
        }
    }

    /// <summary>
    /// 同步之后刷新：文件夹没有增删 / 改名时只更新数字（选中项、展开状态、列表滚动位置都不受影响）；
    /// 有变化才重建树（保持选中节点）。
    /// </summary>
    public async Task RefreshAfterSyncAsync(CancellationToken cancellationToken = default)
    {
        var fresh = await _database.ListAllFoldersAsync(cancellationToken);
        var sameShape = fresh.Count == _allFolders.Count &&
                        fresh.Select(f => (f.Id, f.Name)).OrderBy(x => x.Id, StringComparer.Ordinal)
                            .SequenceEqual(_allFolders.Select(f => (f.Id, f.Name)).OrderBy(x => x.Id, StringComparer.Ordinal));
        if (sameShape)
        {
            await RefreshCountsAsync(cancellationToken);
        }
        else
        {
            await ReloadPreservingSelectionAsync(cancellationToken);
        }
    }

    /// <summary>只刷新各节点上的数字（不重建树，避免丢失展开/选中状态）。</summary>
    public async Task RefreshCountsAsync(CancellationToken cancellationToken = default)
    {
        _allFolders = await _database.ListAllFoldersAsync(cancellationToken);
        var byId = _allFolders.ToDictionary(f => f.Id);

        foreach (var account in Accounts)
        {
            foreach (var child in account.Children)
            {
                if (child.FolderId is not null && byId.TryGetValue(child.FolderId, out var folder))
                {
                    child.UnreadCount = folder.Special == SpecialFolder.Drafts ? folder.TotalCount : folder.UnreadCount;
                }
            }

            account.UnreadCount = account.Children.FirstOrDefault(c => c.Special == SpecialFolder.Inbox)?.UnreadCount ?? 0;
        }

        foreach (var fav in Favorites)
        {
            if (fav.Kind == MailboxNodeKind.AllInboxes)
            {
                fav.UnreadCount = _allFolders.Where(f => f.Special == SpecialFolder.Inbox).Sum(f => f.UnreadCount);
            }
            else if (fav.Kind == MailboxNodeKind.Drafts)
            {
                fav.UnreadCount = DraftTotal();
            }
            else if (fav.Kind == MailboxNodeKind.Unread)
            {
                fav.UnreadCount = _allFolders.Sum(f => f.Special is SpecialFolder.Inbox or SpecialFolder.Custom or SpecialFolder.Archive ? f.UnreadCount : 0);
            }
        }

        UpdateBadgeVisibility();
    }

    /// <summary>把节点转成数据库过滤条件的文件夹范围；null 表示全部文件夹。</summary>
    public IReadOnlyList<string>? ResolveFolderIds(MailboxNodeViewModel node) => node.Kind switch
    {
        MailboxNodeKind.Folder when node.FolderId is not null => [node.FolderId],
        MailboxNodeKind.AllInboxes => _allFolders.Where(f => f.Special == SpecialFolder.Inbox).Select(f => f.Id).ToList(),
        MailboxNodeKind.Unread => _allFolders.Where(f => f.Special is SpecialFolder.Inbox or SpecialFolder.Custom or SpecialFolder.Archive).Select(f => f.Id).ToList(),
        MailboxNodeKind.Drafts => _allFolders.Where(f => f.Special == SpecialFolder.Drafts).Select(f => f.Id).ToList(),
        MailboxNodeKind.Flagged => null,
        _ => Array.Empty<string>()
    };

    /// <summary>找到某账号的指定特殊文件夹（归档 / 已删除等）。</summary>
    public string? FindFolderId(Guid accountId, SpecialFolder special)
        => _allFolders.FirstOrDefault(f => f.AccountId == accountId && f.Special == special)?.Id;

    public SpecialFolder? GetSpecial(string folderId)
        => _allFolders.FirstOrDefault(f => f.Id == folderId)?.Special;

    public MailboxNodeViewModel? FindNode(Func<MailboxNodeViewModel, bool> predicate)
        => Favorites.FirstOrDefault(predicate) ?? Accounts.SelectMany(a => a.Children).FirstOrDefault(predicate);
}
