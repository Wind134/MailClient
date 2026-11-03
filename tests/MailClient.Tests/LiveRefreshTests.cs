using MailClient.Core;
using MailClient.Core.Sync;
using MailClient.Storage.Services;
using MailClient.ViewModels;

namespace MailClient.Tests;

/// <summary>同步在后台把服务器变化写进本地库之后，界面原地刷新：行对象、选中项都不重建。</summary>
public class LiveRefreshTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 31, 10, 30, 0, TimeSpan.FromHours(8));

    private static async Task<(ShellViewModel Shell, TempDb Tmp)> StartAsync()
    {
        var tmp = new TempDb();
        var shell = new ShellViewModel(tmp.Database, new OutboxSendService(tmp.Database), () => Now);
        await shell.InitializeAsync();
        var until = DateTime.UtcNow.AddSeconds(5);
        while (shell.List.Messages.Count == 0 && DateTime.UtcNow < until)
        {
            await Task.Delay(20);
        }

        return (shell, tmp);
    }

    [Fact]
    public async Task RefreshInPlace_Updates_Read_State_On_The_Same_Row_Objects()
    {
        var (shell, tmp) = await StartAsync();
        await using var _ = tmp;
        var before = shell.List.Messages.ToList();
        var target = before.First(m => m.IsUnread);

        await tmp.Database.ApplyMessageChangeAsync(target.Id, PendingOpKind.MarkRead);       // 模拟同步写入的变化
        await shell.List.RefreshInPlaceAsync();

        Assert.False(target.IsUnread);
        Assert.Equal(before.Count, shell.List.Messages.Count);
        for (var i = 0; i < before.Count; i++)
        {
            Assert.Same(before[i], shell.List.Messages[i]);
        }
    }

    [Fact]
    public async Task RefreshInPlace_Removes_Deleted_Rows_And_Keeps_The_Selection_Of_Others()
    {
        var (shell, tmp) = await StartAsync();
        await using var _ = tmp;
        var keep = shell.List.Messages[0];
        var gone = shell.List.Messages[1];
        shell.List.SelectedMessage = keep;

        await tmp.Database.ApplyMessageChangeAsync(gone.Id, PendingOpKind.Delete);
        await shell.List.RefreshInPlaceAsync();

        Assert.DoesNotContain(shell.List.Messages, m => m.Id == gone.Id);
        Assert.Same(keep, shell.List.SelectedMessage);
        Assert.Same(keep, shell.List.Messages[0]);
    }

    [Fact]
    public async Task RefreshInPlace_Clears_The_Selection_When_The_Selected_Mail_Is_Gone()
    {
        var (shell, tmp) = await StartAsync();
        await using var _ = tmp;
        var doomed = shell.List.Messages[0];
        shell.List.SelectedMessage = doomed;
        var raised = 0;
        shell.List.SelectionChangedExternally += (_, _) => raised++;

        await tmp.Database.ApplyMessageChangeAsync(doomed.Id, PendingOpKind.Delete);
        await shell.List.RefreshInPlaceAsync();

        Assert.Null(shell.List.SelectedMessage);
        Assert.True(raised > 0);
        Assert.DoesNotContain(shell.List.Messages, m => m.Id == doomed.Id);
    }

    [Fact]
    public async Task RefreshInPlace_Is_A_No_Op_While_Searching()
    {
        var (shell, tmp) = await StartAsync();
        await using var _ = tmp;

        shell.List.SearchText = "zzzz-no-such-text";
        await shell.List.RefreshInPlaceAsync();

        Assert.True(shell.List.IsSearching);       // 搜索结果的高亮片段依赖整表重建，原地刷新直接返回
    }

    [Fact]
    public async Task Tree_RefreshAfterSync_Keeps_The_Selected_Node_When_Folders_Are_Unchanged()
    {
        var (shell, tmp) = await StartAsync();
        await using var _ = tmp;
        var node = shell.Tree.SelectedNode;

        await shell.Tree.RefreshAfterSyncAsync();

        Assert.Same(node, shell.Tree.SelectedNode);
    }
}
