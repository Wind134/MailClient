using MailClient.Core;
using MailClient.Storage;
using MailClient.Storage.Services;
using MailClient.ViewModels;

namespace MailClient.Tests;

public class CategoryTabTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 31, 10, 30, 0, TimeSpan.FromHours(8));
    private static readonly Guid GmailId = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeee1");
    private const string InboxId = "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeee1:INBOX";

    private static async Task WaitUntilAsync(Func<bool> condition, int timeoutMs = 5000)
    {
        var until = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!condition())
        {
            if (DateTime.UtcNow > until)
            {
                throw new TimeoutException("条件未在预期时间内满足。");
            }

            await Task.Delay(20);
        }
    }

    private static async Task SeedGmailInboxAsync(MailDatabase db)
    {
        var now = Now;
        await db.UpsertAccountAsync(new AccountEntity(
            GmailId, "演示 Gmail", "demo.gmail@example.com", GmailEndpoints.ProviderId,
            AuthKind.OAuth, now, now, null, true));
        await db.UpsertFolderAsync(new FolderEntity(InboxId, GmailId, "收件箱", SpecialFolder.Inbox, "INBOX", 0, 0, 0));

        MessageEntity Msg(string id, long uid, string subject, string from, MailCategory? category, bool read, int minutesAgo) => new(
            id, GmailId, InboxId, uid, null, null, subject, from, from.Split('@')[0], "预览", null, null,
            now.AddMinutes(-minutesAgo), null, read, false, false, false, category, 100);

        await db.UpsertMessagesAsync([
            Msg("m-primary-unread", 1, "周末安排", "friend@example.com", MailCategory.Primary, false, 10),
            Msg("m-primary-null", 2, "无分类旧邮件", "old@example.com", null, true, 20),
            Msg("m-trans", 3, "订单已发货", "order@shop.example.com", MailCategory.Transactions, false, 30),
            Msg("m-update", 4, "PR 已合并", "notifications@github.example.com", MailCategory.Updates, true, 40),
            Msg("m-promo-unread", 5, "限时优惠", "promo@shop.example.com", MailCategory.Promotions, false, 50),
            Msg("m-promo-read", 6, "会员折扣", "news@shop.example.com", MailCategory.Promotions, true, 60)
        ]);
    }

    private static MailboxNodeViewModel GmailInboxNode() => new(MailboxNodeKind.Folder, "收件箱", "\uE715")
    {
        Special = SpecialFolder.Inbox,
        ProviderId = GmailEndpoints.ProviderId,
        AccountId = GmailId,
        FolderId = InboxId
    };

    [Fact]
    public void Tabs_Put_All_First_And_Default_To_Primary()
    {
        Assert.Equal(5, MessageListViewModel.CategoryTabs.Count);
        Assert.True(MessageListViewModel.CategoryTabs[0].IsAll);
        Assert.Equal("All", MessageListViewModel.CategoryTabs[0].Key);
        Assert.Equal(UiStrings.AllCategories, MessageListViewModel.CategoryTabs[0].Name);
        Assert.Equal(MailCategory.Primary, MessageListViewModel.CategoryTabs[1].Category);
        Assert.Equal(1, MessageListViewModel.DefaultCategoryIndex);
        Assert.Equal(1, MessageListViewModel.IndexOfCategory(MailCategory.Primary));
        Assert.Equal(4, MessageListViewModel.IndexOfCategory(MailCategory.Promotions));
        Assert.Equal(0, MessageListViewModel.ParseTabIndex("All"));
        Assert.Equal(0, MessageListViewModel.ParseTabIndex("all"));
        Assert.Equal(1, MessageListViewModel.ParseTabIndex(null));
        Assert.Equal(1, MessageListViewModel.ParseTabIndex(""));
        Assert.Equal(1, MessageListViewModel.ParseTabIndex("not-a-tab"));
        Assert.Equal(4, MessageListViewModel.ParseTabIndex("Promotions"));
        Assert.Equal("Primary", MessageListViewModel.TabKey(1));
        Assert.Equal("全部", UiStrings.CategoryName((MailCategory?)null));
        Assert.Equal("全部 3", UiStrings.CategoryTabLabel(null, 3));
        Assert.Equal("主要", UiStrings.CategoryTabLabel(MailCategory.Primary, 0));
    }

    [Fact]
    public async Task New_List_Defaults_To_Primary_And_All_Drops_Category_Filter()
    {
        await using var tmp = await TempDb.CreateAsync();
        var list = new MessageListViewModel(tmp.Database, () => Now);
        Assert.Equal(MessageListViewModel.DefaultCategoryIndex, list.SelectedCategoryIndex);
        Assert.Equal(MailCategory.Primary, list.SelectedCategory);
        Assert.False(list.ShowCategoryChips);

        var node = GmailInboxNode();
        var ids = new[] { InboxId };
        var all = list.BuildFilter(node, ids, null, SearchScope.CurrentFolder, category: null);
        Assert.Null(all.Category);
        var primary = list.BuildFilter(node, ids, null, SearchScope.CurrentFolder, MailCategory.Primary);
        Assert.Equal(MailCategory.Primary, primary.Category);
    }

    [Fact]
    public async Task Settings_Restore_And_Remember_Last_Tab()
    {
        using var dir = new TempDir();
        var path = Path.Combine(dir.Path, "settings.json");
        var store = new AppSettingsStore(path);
        store.Update(s => s.LastInboxCategoryTab = "All");

        await using var tmp = await TempDb.CreateAsync();
        var list = new MessageListViewModel(tmp.Database, () => Now, store);
        Assert.Equal(0, list.SelectedCategoryIndex);
        Assert.Null(list.SelectedCategory);

        list.SelectedCategoryIndex = MessageListViewModel.IndexOfCategory(MailCategory.Promotions);
        Assert.Equal("Promotions", store.Snapshot().LastInboxCategoryTab);
        Assert.Equal("Promotions", new AppSettingsStore(path).Snapshot().LastInboxCategoryTab);
    }

    [Fact]
    public async Task Snapshot_Clone_Keeps_LastInboxCategoryTab()
    {
        using var dir = new TempDir();
        var path = Path.Combine(dir.Path, "settings.json");
        var store = new AppSettingsStore(path);
        store.Update(s => s.LastInboxCategoryTab = "All");
        Assert.Equal("All", store.Snapshot().LastInboxCategoryTab);
        Assert.Equal("All", new AppSettingsStore(path).Snapshot().LastInboxCategoryTab);
    }

    [Fact]
    public async Task All_Tab_Lists_Every_Category_And_Shows_Chips()
    {
        await using var tmp = await TempDb.CreateAsync();
        await SeedGmailInboxAsync(tmp.Database);
        var list = new MessageListViewModel(tmp.Database, () => Now);
        list.SearchDelay = TimeSpan.Zero;

        await list.ShowAsync(GmailInboxNode(), [InboxId]);
        await WaitUntilAsync(() => !list.IsLoading && list.Messages.Count > 0);

        Assert.True(list.ShowCategoryTabs);
        Assert.Equal(MailCategory.Primary, list.SelectedCategory);
        Assert.Equal(2, list.Messages.Count);
        Assert.All(list.Messages, m =>
        {
            Assert.Equal(MailCategory.Primary, m.Category);
            Assert.False(m.ShowCategoryChip);
        });

        list.SelectedCategoryIndex = 0;
        await WaitUntilAsync(() => !list.IsLoading && list.Messages.Count == 6 && list.ShowCategoryChips);
        Assert.Null(list.SelectedCategory);
        Assert.All(list.Messages, m => Assert.True(m.ShowCategoryChip));
        Assert.Contains(list.Messages, m => m.CategoryLabel == "促销" && m.Subject == "限时优惠");
        Assert.Contains(list.Messages, m => m.CategoryLabel == "交易");
        Assert.Contains(list.Messages, m => m.CategoryLabel == "更新");
        Assert.Contains(list.Messages, m => m.Id == "m-primary-null" && m.CategoryLabel == "主要");
    }

    [Fact]
    public async Task Unread_Counts_Group_Null_As_Primary()
    {
        await using var tmp = await TempDb.CreateAsync();
        await SeedGmailInboxAsync(tmp.Database);

        var counts = await tmp.Database.CountUnreadByCategoryAsync([InboxId]);
        Assert.Equal(3, counts.All);
        Assert.Equal(1, counts.Primary);
        Assert.Equal(1, counts.Transactions);
        Assert.Equal(0, counts.Updates);
        Assert.Equal(1, counts.Promotions);
        Assert.Equal(3, counts.For(null));
        Assert.Equal(CategoryUnreadCounts.Empty, await tmp.Database.CountUnreadByCategoryAsync(Array.Empty<string>()));
    }

    [Fact]
    public async Task ShowAsync_Refreshes_Tab_Unread_Labels()
    {
        await using var tmp = await TempDb.CreateAsync();
        await SeedGmailInboxAsync(tmp.Database);
        var list = new MessageListViewModel(tmp.Database, () => Now);
        await list.ShowAsync(GmailInboxNode(), [InboxId]);
        await WaitUntilAsync(() => list.CategoryTabTexts.Count == 5 && list.CategoryTabTexts[0].Contains("3"));
        Assert.Equal("全部 3", list.CategoryTabTexts[0]);
        Assert.Equal("主要 1", list.CategoryTabTexts[1]);
        Assert.Equal("交易 1", list.CategoryTabTexts[2]);
        Assert.Equal("更新", list.CategoryTabTexts[3]);
        Assert.Equal("促销 1", list.CategoryTabTexts[4]);
    }

    [Fact]
    public async Task OpenMessage_Selects_The_Messages_Category_Tab()
    {
        await using var tmp = await TempDb.CreateAsync();
        await SeedGmailInboxAsync(tmp.Database);
        using var dir = new TempDir();
        var settings = new AppSettingsStore(Path.Combine(dir.Path, "settings.json"));
        settings.Update(s => s.DemoDataDismissed = true);
        var shell = new ShellViewModel(tmp.Database, new OutboxSendService(tmp.Database), () => Now, settings: settings);
        await shell.InitializeAsync(seedDemoData: false);

        var opened = await shell.OpenMessageAsync("m-promo-unread");
        Assert.True(opened);
        Assert.Equal(MessageListViewModel.IndexOfCategory(MailCategory.Promotions), shell.List.SelectedCategoryIndex);
        Assert.Equal(MailCategory.Promotions, shell.List.SelectedCategory);
        Assert.Equal("m-promo-unread", shell.List.SelectedMessage?.Id);
        Assert.False(shell.List.ShowCategoryChips);
        Assert.Equal("Promotions", settings.Snapshot().LastInboxCategoryTab);
    }
}
