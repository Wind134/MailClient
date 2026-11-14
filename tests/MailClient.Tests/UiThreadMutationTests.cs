using MailClient.Storage.Services;
using MailClient.ViewModels;

namespace MailClient.Tests;

/// <summary>
/// 打开未读邮件会在线程池上标已读。列表行的 IsUnread 绑着 WinUI 控件，
/// 必须把这次更新投回 UI 调度器，否则进程会因 0x8001010E 退出。
/// </summary>
public class UiThreadMutationTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 31, 10, 30, 0, TimeSpan.FromHours(8));

    /// <summary>类型名含 Dispatcher，才会被当成真正的 UI 调度器。</summary>
    private sealed class RecordingDispatcherContext : SynchronizationContext
    {
        public int Posts;

        public override void Post(SendOrPostCallback d, object? state)
        {
            Posts++;
            d(state);
        }
    }

    [Fact]
    public async Task Marking_Read_From_A_Background_Thread_Updates_The_Row_Via_The_Ui_Context()
    {
        await using var tmp = new TempDb();
        var ui = new RecordingDispatcherContext();
        var shell = new ShellViewModel(tmp.Database, new OutboxSendService(tmp.Database), () => Now);
        shell.AttachSynchronizationContext(ui);
        await shell.InitializeAsync();

        var row = shell.List.Messages.First(m => m.IsUnread);
        var postsBefore = ui.Posts;

        await Task.Run(() => shell.Actions.SetReadAsync(row.Id, true));

        Assert.True(ui.Posts > postsBefore);
        Assert.False(row.IsUnread);
    }
}
