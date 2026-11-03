using MailClient.Core;
using MailClient.Core.Services;
using MailClient.Core.Sending;
using MailClient.Core.Sync;
using MailClient.Storage;
using MailClient.ViewModels;

namespace MailClient.Tests;

internal sealed class RecordingSendService : ISendService
{
    public List<OutgoingMailMessage> Sent { get; } = new();

    public Task EnqueueAsync(OutgoingMailMessage message, CancellationToken cancellationToken = default)
    {
        Sent.Add(message);
        return Task.CompletedTask;
    }
}

internal sealed class MemoryDraftStore : IDraftStore
{
    public Dictionary<string, DraftContent> Drafts { get; } = new();

    public Task SaveAsync(DraftContent draft, CancellationToken cancellationToken = default)
    {
        lock (Drafts)
        {
            Drafts[draft.Id] = draft;
        }

        return Task.CompletedTask;
    }

    public Task DeleteAsync(string draftId, CancellationToken cancellationToken = default)
    {
        lock (Drafts)
        {
            Drafts.Remove(draftId);
        }

        return Task.CompletedTask;
    }

    public Task<DraftContent?> GetAsync(string draftId, CancellationToken cancellationToken = default)
        => Task.FromResult(Drafts.GetValueOrDefault(draftId));
}

public class ComposeRequestTests
{
    private static readonly Guid Acct = Guid.NewGuid();

    private static MessageEntity Original(string? rfc = "<orig-1@remote.example>", string subject = "季度报告")
        => new("m1", Acct, "f", 1, rfc, null, subject, "alice@example.com", "Alice", "预览", "第一行\n第二行", "<p>第一行<br>第二行</p>",
            SendFixture.T0, SendFixture.T0, true, false, false, false, null, 10);

    private static RecipientEntity Rcpt(string address, RecipientKind kind) => new(0, "m1", kind, address, null);

    [Fact]
    public void Reply_Targets_Sender_And_Sets_Threading_Headers()
    {
        var r = ComposeRequest.Reply(Original(), [Rcpt("me@example.com", RecipientKind.To)], "me@example.com", all: false);

        Assert.Equal(ComposeMode.Reply, r.Mode);
        Assert.Equal("alice@example.com", r.To);
        Assert.Equal("", r.Cc);
        Assert.Equal("Re: 季度报告", r.Subject);
        Assert.Equal("orig-1@remote.example", r.InReplyTo);
        Assert.Equal(["orig-1@remote.example"], r.References);
        Assert.Equal(Acct, r.AccountId);
    }

    [Fact]
    public void Reply_Quotes_The_Original_Without_Putting_It_In_The_Editable_Body()
    {
        var r = ComposeRequest.Reply(Original(), [], "me@example.com", all: false);

        Assert.Equal("", r.Body);
        Assert.True(r.HasQuote);
        Assert.Contains("> 第一行", r.QuotedText);
        Assert.Contains("> 第二行", r.QuotedText);
        Assert.Contains("Alice <alice@example.com> 写道", r.QuotedText);
        Assert.Contains("<blockquote", r.QuotedHtml);
    }

    [Fact]
    public void ReplyAll_Adds_Other_Recipients_And_Drops_My_Own_Address_And_Duplicates()
    {
        var r = ComposeRequest.Reply(Original(),
            [
                Rcpt("ME@example.com", RecipientKind.To),
                Rcpt("bob@example.com", RecipientKind.To),
                Rcpt("alice@example.com", RecipientKind.To),
                Rcpt("carol@example.com", RecipientKind.Cc),
                Rcpt("carol@example.com", RecipientKind.Cc)
            ],
            "me@example.com", all: true);

        Assert.Equal(ComposeMode.ReplyAll, r.Mode);
        Assert.Equal("alice@example.com; bob@example.com", r.To);
        Assert.Equal("carol@example.com", r.Cc);
    }

    [Fact]
    public void Reply_Does_Not_Double_Re_Prefix()
        => Assert.Equal("Re: 季度报告", ComposeRequest.Reply(Original(subject: "RE: 季度报告"), [], "me@example.com", false).Subject.Replace("RE:", "Re:"));

    [Fact]
    public void Reply_Without_Message_Id_Has_No_Threading_Headers()
    {
        var r = ComposeRequest.Reply(Original(rfc: null), [], "me@example.com", false);
        Assert.Null(r.InReplyTo);
        Assert.Null(r.References);
    }

    [Fact]
    public void Forward_Has_Empty_Recipients_Fwd_Prefix_And_Forward_Header()
    {
        var r = ComposeRequest.Forward(Original());

        Assert.Equal(ComposeMode.Forward, r.Mode);
        Assert.Equal("", r.To);
        Assert.Equal("Fwd: 季度报告", r.Subject);
        Assert.Contains("转发的邮件", r.QuotedText);
        Assert.Contains("发件人：Alice <alice@example.com>", r.QuotedText);
        Assert.Contains("主题：季度报告", r.QuotedText);
        Assert.Null(r.InReplyTo);
    }
}

public class ComposeViewModelSendingTests
{
    private static ComposeAccountOption Account(string provider = "imap.generic", string address = "me@qq.com")
        => new(Guid.NewGuid(), $"我 <{address}>", provider, address);

    [Fact]
    public async Task Send_Builds_Reply_Message_With_Quote_And_Headers()
    {
        var send = new RecordingSendService();
        var acct = Account();
        var request = new ComposeRequest(ComposeMode.Reply, acct.Id, "alice@example.com", "", "Re: 你好", "",
            InReplyTo: "orig@x", References: ["orig@x"], QuotedHtml: "<blockquote>原文</blockquote>", QuotedText: "\n\n> 原文");
        var vm = new ComposeViewModel(send, [acct], request) { Body = "收到 <谢谢>" };

        await vm.SendCommand.ExecuteAsync(null);

        var m = Assert.Single(send.Sent);
        Assert.Equal(acct.Id.ToString("D"), m.AccountId);
        Assert.Equal("orig@x", m.InReplyTo);
        Assert.Equal(["orig@x"], m.References);
        Assert.StartsWith("收到 <谢谢>", m.BodyText);
        Assert.EndsWith("> 原文", m.BodyText);
        Assert.Contains("&lt;谢谢&gt;", m.BodyHtml);   // 用户输入被转义
        Assert.Contains("<blockquote>原文</blockquote>", m.BodyHtml);
        Assert.Equal("", vm.ErrorMessage);
    }

    [Fact]
    public async Task Send_Raises_Sent_And_Deletes_Draft()
    {
        var send = new RecordingSendService();
        var drafts = new MemoryDraftStore();
        var acct = Account();
        var vm = new ComposeViewModel(send, [acct], ComposeRequest.New(acct.Id), drafts) { To = "a@b.com", Subject = "s" };
        await vm.FlushDraftAsync();
        Assert.Single(drafts.Drafts);
        var raised = false;
        vm.Sent += (_, _) => raised = true;

        await vm.SendCommand.ExecuteAsync(null);

        Assert.True(raised);
        Assert.Empty(drafts.Drafts);
        await vm.FlushDraftAsync(); // 发送后不会再写草稿
        Assert.Empty(drafts.Drafts);
    }

    [Fact]
    public async Task Invalid_Recipient_Does_Not_Enqueue()
    {
        var send = new RecordingSendService();
        var acct = Account();
        var vm = new ComposeViewModel(send, [acct], ComposeRequest.New(acct.Id)) { To = "good@b.com; nope", Subject = "s" };

        await vm.SendCommand.ExecuteAsync(null);

        Assert.Empty(send.Sent);
        Assert.Contains("nope", vm.ErrorMessage);
    }

    [Fact]
    public async Task Draft_Is_Autosaved_After_The_Debounce()
    {
        var drafts = new MemoryDraftStore();
        var acct = Account();
        var vm = new ComposeViewModel(new RecordingSendService(), [acct], ComposeRequest.New(acct.Id), drafts)
        {
            DraftDelay = TimeSpan.FromMilliseconds(50)
        };

        vm.Subject = "草稿主题";
        vm.Body = "正文";

        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (drafts.Drafts.Count == 0 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(25);
        }

        var d = Assert.Single(drafts.Drafts.Values);
        Assert.Equal("草稿主题", d.Subject);
        Assert.Equal("正文", d.Body);
        Assert.Equal(acct.Id, d.AccountId);
    }

    [Fact]
    public async Task Empty_Compose_Window_Saves_No_Draft()
    {
        var drafts = new MemoryDraftStore();
        var acct = Account();
        var vm = new ComposeViewModel(new RecordingSendService(), [acct], ComposeRequest.New(acct.Id), drafts);

        await vm.FlushDraftAsync();

        Assert.Empty(drafts.Drafts);
    }

    [Fact]
    public async Task Discard_Deletes_The_Draft_And_Stops_Autosave()
    {
        var drafts = new MemoryDraftStore();
        var acct = Account();
        var vm = new ComposeViewModel(new RecordingSendService(), [acct], ComposeRequest.New(acct.Id), drafts) { Subject = "x" };
        await vm.FlushDraftAsync();

        await vm.DiscardAsync();
        await vm.FlushDraftAsync();

        Assert.Empty(drafts.Drafts);
    }

    [Fact]
    public void Attachment_Over_Limit_Warns_But_Still_Attaches()
    {
        var file = Path.Combine(Path.GetTempPath(), $"big-{Guid.NewGuid():N}.bin");
        using (var fs = File.Create(file))
        {
            fs.SetLength(AttachmentLimits.GraphInlineLimit + 1024);
        }

        try
        {
            var graph = Account("microsoft.graph", "me@outlook.com");
            var vm = new ComposeViewModel(new RecordingSendService(), [graph], ComposeRequest.New(graph.Id));

            Assert.True(vm.AddAttachment(file));

            Assert.Single(vm.Attachments);
            Assert.NotEmpty(vm.AttachmentWarning);
            Assert.Contains("1 个附件", vm.AttachmentSummary);
            vm.RemoveAttachment(vm.Attachments[0]);
            Assert.Equal("", vm.AttachmentWarning);
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public void Same_Attachment_Is_Not_Added_Twice_And_Missing_File_Reports_Error()
    {
        var file = Path.Combine(Path.GetTempPath(), $"a-{Guid.NewGuid():N}.txt");
        File.WriteAllText(file, "x");
        try
        {
            var acct = Account();
            var vm = new ComposeViewModel(new RecordingSendService(), [acct], ComposeRequest.New(acct.Id));

            Assert.True(vm.AddAttachment(file));
            Assert.False(vm.AddAttachment(file));
            Assert.Single(vm.Attachments);

            Assert.False(vm.AddAttachment(file + ".missing"));
            Assert.NotEmpty(vm.ErrorMessage);
            Assert.Equal(file, vm.BuildMessage().AttachmentPaths.Single());
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public void Switching_To_Graph_Account_Applies_The_Smaller_Limit()
    {
        var file = Path.Combine(Path.GetTempPath(), $"mid-{Guid.NewGuid():N}.bin");
        using (var fs = File.Create(file))
        {
            fs.SetLength(5 * 1024 * 1024);
        }

        try
        {
            var qq = Account();
            var graph = Account("microsoft.graph", "me@outlook.com");
            var vm = new ComposeViewModel(new RecordingSendService(), [qq, graph], ComposeRequest.New(qq.Id));
            vm.AddAttachment(file);
            Assert.Equal("", vm.AttachmentWarning);

            vm.SelectedAccount = graph;

            Assert.NotEmpty(vm.AttachmentWarning);
        }
        finally
        {
            File.Delete(file);
        }
    }
}

public class OutboxViewModelTests
{
    [Fact]
    public async Task Lists_Failed_Item_With_Error_And_Retry_Delete_Actions()
    {
        await using var fx = await SendFixture.CreateAsync();
        var id = await Seed(fx, OutboxStatus.Failed, error: "登录失败：授权码不正确");
        var vm = new OutboxViewModel(fx.Db, () => SendFixture.T0);

        await vm.ReloadAsync();

        var item = Assert.Single(vm.Items);
        Assert.False(vm.IsEmpty);
        Assert.Equal("发送失败", item.StatusText);
        Assert.True(item.IsFailed);
        Assert.True(item.HasError);
        Assert.Contains("授权码", item.ErrorText);
        Assert.True(item.CanRetry);
        Assert.True(item.CanDelete);
        Assert.Contains("bob@example.com", item.RecipientsLine);

        await vm.RetryAsync(id);

        var after = Assert.Single(vm.Items);
        Assert.Equal(OutboxStatus.Pending, after.Status);
        Assert.False(after.IsFailed);

        await vm.DeleteAsync(id);
        Assert.Empty(vm.Items);
        Assert.True(vm.IsEmpty);
    }

    [Fact]
    public async Task Sending_Item_Cannot_Be_Deleted()
    {
        await using var fx = await SendFixture.CreateAsync();
        await Seed(fx, OutboxStatus.Sending);
        var vm = new OutboxViewModel(fx.Db, () => SendFixture.T0);

        await vm.ReloadAsync();

        var item = Assert.Single(vm.Items);
        Assert.Equal("发送中…", item.StatusText);
        Assert.False(item.CanDelete);
        Assert.False(item.CanRetry);
    }

    [Fact]
    public async Task Pending_Retry_Shows_Next_Attempt_Time()
    {
        await using var fx = await SendFixture.CreateAsync();
        await Seed(fx, OutboxStatus.Pending, attempts: 2, next: SendFixture.T0.AddMinutes(10));
        var vm = new OutboxViewModel(fx.Db, () => SendFixture.T0);

        await vm.ReloadAsync();

        var item = Assert.Single(vm.Items);
        Assert.Contains("第 2 次发送失败", item.StatusText);
        Assert.True(item.CanRetry);
    }

    [Fact]
    public async Task Outbox_Node_Only_Shows_When_Outbox_Is_Not_Empty()
    {
        await using var fx = await SendFixture.CreateAsync();
        var tree = new MailboxTreeViewModel(fx.Db);
        await tree.LoadAsync();
        Assert.DoesNotContain(tree.Favorites, n => n.Kind == MailboxNodeKind.Outbox);

        var id = await Seed(fx, OutboxStatus.Failed);
        await tree.RefreshOutboxNodeAsync();
        var node = Assert.Single(tree.Favorites, n => n.Kind == MailboxNodeKind.Outbox);
        Assert.Equal(1, node.UnreadCount);

        await fx.Db.DeleteOutboxAsync(id);
        await tree.RefreshOutboxNodeAsync();
        Assert.DoesNotContain(tree.Favorites, n => n.Kind == MailboxNodeKind.Outbox);
    }

    private static async Task<string> Seed(SendFixture fx, OutboxStatus status, string? error = null, int attempts = 0, DateTimeOffset? next = null)
    {
        var id = Guid.NewGuid().ToString("N");
        var json = System.Text.Json.JsonSerializer.Serialize(fx.Outgoing());
        await fx.Db.EnqueueOutboxAsync(new OutboxEntity(id, fx.AccountId, json, status, attempts, error, SendFixture.T0, SendFixture.T0, next));
        return id;
    }
}

public class MessageActionsTests
{
    private static async Task<(SendFixture Fx, MessageActions Actions, List<ActionNotice> Notices)> Make()
    {
        var fx = await SendFixture.CreateAsync();
        var tree = new MailboxTreeViewModel(fx.Db);
        await tree.LoadAsync();
        var actions = new MessageActions(fx.Db, tree);
        var notices = new List<ActionNotice>();
        actions.Notice += (_, n) => notices.Add(n);
        return (fx, actions, notices);
    }

    [Fact]
    public async Task Read_And_Flag_Update_Locally_And_Queue()
    {
        var (fx, actions, _) = await Make();
        await using var _ = fx;
        var m = await fx.AddMessageAsync("INBOX", 1);

        await actions.SetReadAsync(m.Id, true);
        await actions.SetFlaggedAsync(m.Id, true);

        var local = (await fx.Db.GetMessageAsync(m.Id))!;
        Assert.True(local.IsRead);
        Assert.True(local.IsFlagged);
        Assert.Equal(2, await fx.Db.CountPendingOpsAsync());
    }

    [Fact]
    public async Task Archive_Moves_To_Archive_Folder()
    {
        var (fx, actions, _) = await Make();
        await using var _ = fx;
        var m = await fx.AddMessageAsync("INBOX", 1);

        Assert.True(await actions.ArchiveAsync(m.Id));

        Assert.Equal(fx.FolderId("Archive"), (await fx.Db.GetMessageAsync(m.Id))!.FolderId);
        var op = Assert.Single(await fx.Db.ListPendingOpsAsync());
        Assert.Equal(PendingOpKind.Move, op.Kind);
    }

    [Fact]
    public async Task Archive_Without_Archive_Folder_Gives_A_Notice()
    {
        await using var fx = await SendFixture.CreateAsync(archiveFolder: false);
        var tree = new MailboxTreeViewModel(fx.Db);
        await tree.LoadAsync();
        var actions = new MessageActions(fx.Db, tree);
        var notices = new List<ActionNotice>();
        actions.Notice += (_, n) => notices.Add(n);
        var m = await fx.AddMessageAsync("INBOX", 1);

        Assert.False(await actions.ArchiveAsync(m.Id));

        Assert.Single(notices);
        Assert.True(notices[0].IsError);
        Assert.Equal(0, await fx.Db.CountPendingOpsAsync());
    }

    [Fact]
    public async Task Delete_Moves_To_Trash_Then_Deletes_Permanently_From_Trash()
    {
        var (fx, actions, _) = await Make();
        await using var _ = fx;
        var m = await fx.AddMessageAsync("INBOX", 1);

        Assert.True(await actions.DeleteAsync(m.Id));
        Assert.Equal(fx.FolderId("Trash"), (await fx.Db.GetMessageAsync(m.Id))!.FolderId);
        Assert.Equal(PendingOpKind.Move, Assert.Single(await fx.Db.ListPendingOpsAsync()).Kind);

        Assert.True(await actions.DeleteAsync(m.Id));
        Assert.Null(await fx.Db.GetMessageAsync(m.Id));
        Assert.Equal(PendingOpKind.Delete, Assert.Single(await fx.Db.ListPendingOpsAsync()).Kind);
    }
}
