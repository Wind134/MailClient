using System.Text.Json;
using MailClient.Core;
using MailClient.Core.Services;
using MailClient.Storage.Services;
using MailClient.ViewModels;

namespace MailClient.Tests;

public class ViewModelTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 31, 10, 30, 0, TimeSpan.FromHours(8));

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

    private static async Task<(ShellViewModel Shell, TempDb Tmp)> StartShellAsync()
    {
        var tmp = new TempDb();
        var shell = new ShellViewModel(tmp.Database, new OutboxSendService(tmp.Database), () => Now);
        await shell.InitializeAsync();
        await WaitUntilAsync(() => shell.List.Messages.Count > 0);
        return (shell, tmp);
    }

    [Fact]
    public async Task Shell_Initialize_Selects_AllInboxes_Without_Category_Tabs()
    {
        var (shell, tmp) = await StartShellAsync();
        await using var _ = tmp;

        Assert.True(shell.IsInitialized);
        Assert.Equal(MailboxNodeKind.AllInboxes, shell.Tree.SelectedNode!.Kind);
        // 演示账号是 Outlook + QQ：分类标签仅 Gmail 收件箱显示
        Assert.False(shell.List.ShowCategoryTabs);
        Assert.Equal(2, shell.Tree.Accounts.Count);
        Assert.All(shell.Tree.Accounts, a => Assert.Equal(6, a.Children.Count));
        Assert.Equal("所有收件箱", shell.List.Title);
        Assert.True(shell.List.Messages.Count > 0);
    }

    [Fact]
    public async Task NonGmail_Inbox_Hides_Category_Tabs_And_Lists_All()
    {
        var (shell, tmp) = await StartShellAsync();
        await using var _ = tmp;

        var outlookInbox = shell.Tree.Accounts
            .SelectMany(a => a.Children)
            .First(n => n.Special == SpecialFolder.Inbox && n.ProviderId == "microsoft.graph");
        // 通过 SelectedNode 触发 ShowNode
        shell.Tree.SelectedNode = outlookInbox;
        await WaitUntilAsync(() => shell.List.Title == outlookInbox.Title && !shell.List.IsLoading);
        Assert.False(shell.List.ShowCategoryTabs);
        Assert.False(outlookInbox.ShowsCategories);
        Assert.True(shell.List.Messages.Count > 0);
    }

    [Fact]
    public void Gmail_Inbox_Node_Shows_Categories()
    {
        var gmail = new MailboxNodeViewModel(MailboxNodeKind.Folder, "收件箱", "\uE715")
        {
            Special = SpecialFolder.Inbox,
            ProviderId = GmailEndpoints.ProviderId
        };
        var outlook = new MailboxNodeViewModel(MailboxNodeKind.Folder, "收件箱", "\uE715")
        {
            Special = SpecialFolder.Inbox,
            ProviderId = "microsoft.graph"
        };
        var all = new MailboxNodeViewModel(MailboxNodeKind.AllInboxes, "所有收件箱", "\uE715");
        Assert.True(gmail.ShowsCategories);
        Assert.False(outlook.ShowsCategories);
        Assert.False(all.ShowsCategories);
    }

    [Fact]
    public async Task Search_Ignores_Category_Filter()
    {
        var (shell, tmp) = await StartShellAsync();
        await using var _ = tmp;

        shell.List.SearchText = "项目";
        await WaitUntilAsync(() => shell.List.Messages.Count > 0 && shell.List.Messages.All(m =>
            m.Subject.Contains("项目") || m.Preview.Contains("项目") || m.Sender.Contains("项目")));
        // 演示数据无 Gmail：分类栏本身就关；搜索同样不按 Category 过滤
        Assert.False(shell.List.ShowCategoryTabs);
    }

    [Fact]
    public async Task Selecting_Message_Loads_Reading_Pane_And_Marks_Read()
    {
        var (shell, tmp) = await StartShellAsync();
        await using var _ = tmp;

        var row = shell.List.Messages.First(m => m.IsUnread);
        shell.List.SelectedMessage = row;
        await WaitUntilAsync(() => shell.Reading.HasMessage && shell.Reading.Subject == row.Subject);
        await WaitUntilAsync(() => !row.IsUnread);

        Assert.False(shell.Reading.IsEmpty);
        Assert.False(string.IsNullOrWhiteSpace(shell.Reading.FromName));
        Assert.True((await tmp.Database.GetMessageAsync(row.Id))!.IsRead);
    }

    [Fact]
    public async Task Delete_Moves_To_Trash_And_Removes_Row()
    {
        var (shell, tmp) = await StartShellAsync();
        await using var _ = tmp;

        var row = shell.List.Messages.First();
        shell.List.SelectedMessage = row;
        await WaitUntilAsync(() => shell.Reading.HasMessage);
        await shell.Reading.DeleteCommand.ExecuteAsync(null);
        await WaitUntilAsync(() => shell.List.Messages.All(m => m.Id != row.Id));

        var moved = await tmp.Database.GetMessageAsync(row.Id);
        Assert.NotNull(moved);
        Assert.Equal(shell.Tree.FindFolderId(row.AccountId, SpecialFolder.Trash), moved!.FolderId);
    }

    [Fact]
    public async Task Reply_Raises_Compose_Request_With_Re_Prefix()
    {
        var (shell, tmp) = await StartShellAsync();
        await using var _ = tmp;

        ComposeRequest? captured = null;
        shell.ComposeRequested += (_, r) => captured = r;
        shell.List.SelectedMessage = shell.List.Messages.First();
        await WaitUntilAsync(() => shell.Reading.HasMessage);
        await Task.Run(() => shell.Reading.ReplyCommand.Execute(null));
        await WaitUntilAsync(() => captured is not null);

        Assert.StartsWith("Re: ", captured!.Subject);
        Assert.Equal(ComposeMode.Reply, captured.Mode);
        Assert.False(string.IsNullOrWhiteSpace(captured.To));
    }

    [Fact]
    public async Task Compose_Send_Writes_Pending_Outbox_Item()
    {
        var (shell, tmp) = await StartShellAsync();
        await using var _ = tmp;

        var compose = await shell.CreateComposeAsync(ComposeRequest.New());
        Assert.Equal(2, compose.Accounts.Count);
        var sent = false;
        compose.Sent += (_, _) => sent = true;

        Assert.False(compose.SendCommand.CanExecute(null));
        compose.To = "a@example.com; b@example.com，a@example.com";
        compose.Subject = "你好";
        compose.Body = "第一行\n第二行 <b>";
        Assert.True(compose.SendCommand.CanExecute(null));
        await compose.SendCommand.ExecuteAsync(null);

        Assert.True(sent);
        var pending = await tmp.Database.ListPendingOutboxAsync();
        var item = Assert.Single(pending);
        Assert.Equal(OutboxStatus.Pending, item.Status);
        var payload = JsonSerializer.Deserialize<OutgoingMailMessage>(item.PayloadJson)!;
        Assert.Equal(new[] { "a@example.com", "b@example.com" }, payload.To);
        Assert.Equal("你好", payload.Subject);
        Assert.Contains("第一行<br", payload.BodyHtml);
        Assert.Contains("&lt;b&gt;", payload.BodyHtml);
    }

    [Fact]
    public async Task Compose_Rejects_Invalid_Address()
    {
        var (shell, tmp) = await StartShellAsync();
        await using var _ = tmp;

        var compose = await shell.CreateComposeAsync(ComposeRequest.New());
        compose.To = "not-an-address";
        await compose.SendCommand.ExecuteAsync(null);

        Assert.Contains("无效", compose.ErrorMessage);
        Assert.Empty(await tmp.Database.ListPendingOutboxAsync());
    }

    [Theory]
    [InlineData("a@b.com", true)]
    [InlineData("a@b", false)]
    [InlineData("@b.com", false)]
    [InlineData("a b@c.com", false)]
    public void IsValidAddress_Basics(string address, bool expected)
        => Assert.Equal(expected, ComposeViewModel.IsValidAddress(address));

    [Fact]
    public void MakeInitials_Handles_Chinese_And_Latin()
    {
        Assert.Equal("张", MessageRowViewModel.MakeInitials("张伟"));
        Assert.Equal("AB", MessageRowViewModel.MakeInitials("Alice Brown"));
    }

    [Fact]
    public async Task OutboxSendService_Throws_Without_Account()
    {
        await using var tmp = new TempDb();
        await tmp.Database.InitializeAsync();
        var service = new OutboxSendService(tmp.Database);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.EnqueueAsync(
            new OutgoingMailMessage(null, new[] { "a@b.com" }, Array.Empty<string>(), Array.Empty<string>(), "s", "b", Array.Empty<string>())));
    }
}
