using MailClient.Core;
using MailClient.Storage.Demo;
using MailClient.ViewModels;

namespace MailClient.Tests;

public class AccountAvatarTests
{
    [Fact]
    public async Task Demo_Accounts_Get_Distinct_Colors_And_Letters_Even_Without_Saved_Color()
    {
        await using var db = await TempDb.CreateSeededAsync();
        var tree = new MailboxTreeViewModel(db.Database); // 没有 ColorResolver：模拟从未设置过颜色
        await tree.LoadAsync();

        Assert.Equal(2, tree.Accounts.Count);
        Assert.All(tree.Accounts, a => Assert.True(AccountPalette.IsValidHex(a.ColorHex)));
        Assert.Equal(2, tree.Accounts.Select(a => a.ColorHex).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Equal(["O", "Q"], tree.Accounts.Select(a => a.AvatarLetter).OrderBy(x => x, StringComparer.Ordinal).ToArray());
    }

    [Fact]
    public async Task Saved_Color_Wins_And_Others_Avoid_It()
    {
        await using var db = await TempDb.CreateSeededAsync();
        var tree = new MailboxTreeViewModel(db.Database)
        {
            ColorResolver = id => id == DemoDataSeeder.OutlookAccountId ? AccountPalette.ColorFor(DemoDataSeeder.QqAccountId) : null
        };
        await tree.LoadAsync();

        var outlook = tree.Accounts.Single(a => a.AccountId == DemoDataSeeder.OutlookAccountId);
        var qq = tree.Accounts.Single(a => a.AccountId == DemoDataSeeder.QqAccountId);
        Assert.Equal(AccountPalette.ColorFor(DemoDataSeeder.QqAccountId), outlook.ColorHex);
        Assert.NotEqual(outlook.ColorHex, qq.ColorHex);
    }

    [Fact]
    public void Assign_Is_Stable_Distinct_Up_To_Palette_Size_And_Ignores_Invalid_Saved()
    {
        var ids = Enumerable.Range(0, AccountPalette.Colors.Count).Select(_ => Guid.NewGuid()).ToList();
        var input = ids.Select(i => (i, (string?)null)).ToList();
        var a = AccountPalette.Assign(input);
        var b = AccountPalette.Assign(input);

        Assert.Equal(a.OrderBy(x => x.Key), b.OrderBy(x => x.Key));
        Assert.Equal(AccountPalette.Colors.Count, a.Values.Distinct(StringComparer.OrdinalIgnoreCase).Count());

        var bad = AccountPalette.Assign([(ids[0], "not-a-color")]);
        Assert.True(AccountPalette.IsValidHex(bad[ids[0]]));
    }

    [Theory]
    [InlineData("Outlook", "a@b.com", "O")]
    [InlineData("  qq 邮箱", "a@b.com", "Q")]
    [InlineData("邮箱一号", "a@b.com", "邮")]
    [InlineData("", "demo.outlook@example.com", "D")]
    [InlineData(null, "@weird", "W")]
    [InlineData("", "", "?")]
    [InlineData("😀 fun", "x@y.z", "😀")]
    public void LetterFor_Picks_First_Visible_Character(string? name, string? email, string expected)
        => Assert.Equal(expected, AccountPalette.LetterFor(name, email));

    [Fact]
    public async Task Unread_Badge_Is_Hidden_When_Same_Number_As_All_Inboxes()
    {
        await using var db = await TempDb.CreateSeededAsync();
        var tree = new MailboxTreeViewModel(db.Database);
        await tree.LoadAsync();

        var all = tree.Favorites.Single(n => n.Kind == MailboxNodeKind.AllInboxes);
        var unread = tree.Favorites.Single(n => n.Kind == MailboxNodeKind.Unread);
        Assert.Equal(unread.UnreadCount != all.UnreadCount, unread.ShowBadge);
        Assert.NotEqual(all.Glyph, unread.Glyph);
        Assert.True(all.ShowBadge);
    }
}
