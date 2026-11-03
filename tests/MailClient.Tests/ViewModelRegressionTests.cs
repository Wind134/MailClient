using MailClient.Core;
using MailClient.Core.Services;
using MailClient.Storage.Services;
using MailClient.ViewModels;

namespace MailClient.Tests;

/// <summary>
/// 回归：“在 Outlook 收件箱里点开一封邮件后，另一封邮件从列表消失、未读数 8→6”。
/// 排查结论：属于从“所有收件箱”切换到“Outlook 收件箱”（该文件夹本来就不含 QQ 账号的邮件）
/// 叠加“打开即标记已读”的正常行为，不是列表被误删。这里固定住正确行为：
/// 选中并阅读一封邮件后，所在文件夹、列表内容与顺序不变，节点未读数只减 1。
/// </summary>
public class ViewModelRegressionTests
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

    [Fact]
    public async Task Reading_A_Message_In_Account_Inbox_Keeps_Folder_And_List_Intact()
    {
        await using var tmp = new TempDb();
        var shell = new ShellViewModel(tmp.Database, new OutboxSendService(tmp.Database), () => Now);
        await shell.InitializeAsync();
        await WaitUntilAsync(() => shell.List.Messages.Count > 0);

        foreach (var account in shell.Tree.Accounts)
        {
            var inbox = account.Children.First(c => c.Special == SpecialFolder.Inbox);
            shell.Tree.SelectedNode = inbox;
            await WaitUntilAsync(() => shell.List.Messages.Count > 0 &&
                                       shell.List.Messages.All(m => m.AccountId == account.AccountId));

            var before = shell.List.Messages.Select(m => m.Id).ToList();
            var unreadBefore = shell.List.Messages.Count(m => m.IsUnread);
            var target = shell.List.Messages.First(m => m.IsUnread);

            shell.List.SelectedMessage = target;
            await WaitUntilAsync(() => shell.Reading.HasMessage && !target.IsUnread);
            await Task.Delay(300); // 给 Mutated 触发的刷新留出时间

            Assert.Same(inbox, shell.Tree.SelectedNode);
            Assert.Equal(before, shell.List.Messages.Select(m => m.Id).ToList());
            Assert.Equal(unreadBefore - 1, shell.List.Messages.Count(m => m.IsUnread));
            Assert.Same(target, shell.List.SelectedMessage);
        }
    }
}
