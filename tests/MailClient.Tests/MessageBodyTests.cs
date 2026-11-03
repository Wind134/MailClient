using MailClient.Core;
using MailClient.Core.Services;
using MailClient.Providers.Imap;
using MailClient.Storage;
using MailClient.ViewModels;
using MimeKit;

namespace MailClient.Tests;

public sealed class MessageBodyTests
{
    [Fact]
    public void ExtractContent_PrefersHtml_OverPlain()
    {
        var message = new MimeMessage();
        var alt = new MultipartAlternative()
        {
            new TextPart("plain") { Text = "PLAIN ONLY BODY" },
            new TextPart("html") { Text = "<p>HTML <b>FULL</b> BODY with more content than preview</p>" }
        };
        message.Body = alt;

        var content = MimeMessageMapper.ExtractContent(message, previewMaxChars: 20);
        Assert.NotNull(content.BodyHtml);
        Assert.Contains("HTML", content.BodyHtml!, StringComparison.Ordinal);
        Assert.Contains("<b>FULL</b>", content.BodyHtml!, StringComparison.Ordinal);
        // multipart/alternative：纯文本与 HTML 都保留；UI 优先渲染 HTML
        Assert.Equal("PLAIN ONLY BODY", content.BodyText);
        Assert.True(content.Preview.Length <= 21);
    }

    [Fact]
    public void ExtractContent_NestedMultipart_FindsHtml()
    {
        var message = new MimeMessage();
        var alt = new MultipartAlternative()
        {
            new TextPart("plain") { Text = "plain nested" },
            new TextPart("html") { Text = "<html><body><p>nested html body</p></body></html>" }
        };
        var mixed = new Multipart("mixed") { alt };
        var file = new MimePart("application", "pdf")
        {
            Content = new MimeKit.MimeContent(new MemoryStream(new byte[] { 1, 2, 3, 4 })),
            ContentDisposition = new ContentDisposition(ContentDisposition.Attachment),
            FileName = "report.pdf"
        };
        mixed.Add(file);
        message.Body = mixed;

        var content = MimeMessageMapper.ExtractContent(message);
        Assert.NotNull(content.BodyHtml);
        Assert.Contains("nested html", content.BodyHtml!, StringComparison.OrdinalIgnoreCase);
        Assert.False(string.IsNullOrWhiteSpace(content.BodyText));
        Assert.Contains(content.Attachments, a => a.FileName == "report.pdf" && !a.IsInline);
    }

    [Fact]
    public void ExtractContent_Preview_IsNotFullBody()
    {
        var longText = "Hello vmuser, Someone just signed in to your Example VPS Control Panel account. " +
                       "Time: Oct 23, 2026 10:54 AM UTC. If this was you, no action is needed. " +
                       "If this was not you, please reset your password immediately and contact support.";
        var message = new MimeMessage();
        message.Body = new TextPart("plain") { Text = longText };

        var content = MimeMessageMapper.ExtractContent(message, previewMaxChars: 40);
        Assert.Equal(longText, content.BodyText);
        Assert.True(content.Preview.Length < longText.Length);
        Assert.EndsWith("…", content.Preview);
    }

    [Fact]
    public async Task SaveMessageBody_MarksFetched_And_UpsertPreservesCache()
    {
        await using var tmp = new TempDb();
        await tmp.Database.InitializeAsync();
        Assert.Equal(7, await tmp.Database.GetSchemaVersionAsync());

        var accountId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        await tmp.Database.UpsertAccountAsync(new AccountEntity(
            accountId, "测", "a@b.com", "imap", AuthKind.Password, now, now, null, true));
        var folderId = $"{accountId:D}:inbox";
        await tmp.Database.UpsertFolderAsync(new FolderEntity(
            folderId, accountId, "收件箱", SpecialFolder.Inbox, "INBOX", 0, 0, 0, null, null));

        var messageId = "msg-body-1";
        await tmp.Database.UpsertMessageAsync(new MessageEntity(
            messageId, accountId, folderId, RemoteUid: 9, RfcMessageId: "<1@x>",
            ThreadKey: null, Subject: "告警", FromAddress: "noreply@vps.example", FromName: "Example VPS",
            Preview: "Hello vmuser, Someone just signed in…",
            BodyText: null, BodyHtml: null, ReceivedAt: now, SentAt: now,
            IsRead: false, IsFlagged: false, IsDraft: false, HasAttachments: false,
            Category: MailCategory.Updates, SizeBytes: 100, BodyFetched: false));

        var before = await tmp.Database.GetMessageAsync(messageId);
        Assert.NotNull(before);
        Assert.False(before!.BodyFetched);
        Assert.Null(before.BodyText);

        await tmp.Database.SaveMessageBodyAsync(messageId, new MessageBodyContent(
            "Hello vmuser, Someone just signed in to your Example VPS Control Panel account. Full details here.",
            "<p>Hello vmuser, Someone just signed in to your Example VPS Control Panel account. Full details here.</p>",
            [new MessageAttachmentInfo("note.txt", "text/plain", 12, null, false, null)]));

        var cached = await tmp.Database.GetMessageAsync(messageId);
        Assert.NotNull(cached);
        Assert.True(cached!.BodyFetched);
        Assert.Contains("Full details", cached.BodyText!, StringComparison.Ordinal);
        Assert.Contains("Full details", cached.BodyHtml!, StringComparison.Ordinal);

        var atts = await tmp.Database.ListAttachmentsAsync(messageId);
        Assert.Single(atts);
        Assert.Equal("note.txt", atts[0].FileName);

        // 同步再次 upsert（无正文）不得冲掉缓存
        await tmp.Database.UpsertMessageAsync(new MessageEntity(
            messageId, accountId, folderId, RemoteUid: 9, RfcMessageId: "<1@x>",
            ThreadKey: null, Subject: "告警", FromAddress: "noreply@vps.example", FromName: "Example VPS",
            Preview: "Hello vmuser, Someone just signed in…",
            BodyText: null, BodyHtml: null, ReceivedAt: now, SentAt: now,
            IsRead: true, IsFlagged: false, IsDraft: false, HasAttachments: false,
            Category: MailCategory.Updates, SizeBytes: 100, BodyFetched: false));

        var after = await tmp.Database.GetMessageAsync(messageId);
        Assert.True(after!.BodyFetched);
        Assert.Contains("Full details", after.BodyText!, StringComparison.Ordinal);
        Assert.True(after.IsRead);
    }

    [Fact]
    public async Task ReadingPane_DoesNotTreatPreviewAsBody_WhileLoading()
    {
        await using var tmp = new TempDb();
        await tmp.Database.InitializeAsync();
        var accountId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        await tmp.Database.UpsertAccountAsync(new AccountEntity(
            accountId, "测", "a@b.com", "imap", AuthKind.Password, now, now, null, true));
        var folderId = $"{accountId:D}:inbox";
        await tmp.Database.UpsertFolderAsync(new FolderEntity(
            folderId, accountId, "收件箱", SpecialFolder.Inbox, "INBOX", 1, 1, 0, null, null));
        var messageId = "msg-preview-only";
        const string preview = "Hello vmuser, Someone just signed in to your Example VPS…";
        await tmp.Database.UpsertMessageAsync(new MessageEntity(
            messageId, accountId, folderId, RemoteUid: 1, RfcMessageId: "<p@x>",
            ThreadKey: null, Subject: "Sign-in alert", FromAddress: "noreply@vps.example", FromName: "Example VPS",
            Preview: preview, BodyText: null, BodyHtml: null, ReceivedAt: now, SentAt: now,
            IsRead: false, IsFlagged: false, IsDraft: false, HasAttachments: false,
            Category: MailCategory.Updates, SizeBytes: 50, BodyFetched: false));

        var tree = new MailboxTreeViewModel(tmp.Database);
        await tree.LoadAsync();
        var loader = new FakeBodyLoader(tmp.Database, delayMs: 80, fullText: "COMPLETE BODY TEXT FROM SERVER");
        var reading = new ReadingPaneViewModel(tmp.Database, tree, bodyLoader: loader);

        var loadTask = reading.LoadAsync(messageId);
        // 给 UI 调度一丁点时间让同步部分跑完
        await Task.Delay(20);
        Assert.True(reading.IsBodyLoading);
        Assert.Equal(string.Empty, reading.BodyText);
        Assert.NotEqual(preview, reading.BodyText);

        await loadTask;
        // EnsureBody 是 fire-and-forget；等它完成
        var deadline = DateTime.UtcNow.AddSeconds(3);
        while (reading.IsBodyLoading && DateTime.UtcNow < deadline)
        {
            await Task.Delay(30);
        }

        Assert.False(reading.IsBodyLoading);
        Assert.Null(reading.BodyLoadError);
        Assert.Equal("COMPLETE BODY TEXT FROM SERVER", reading.BodyText);
        Assert.True(loader.CallCount >= 1);

        // 缓存命中：再次打开不应再请求服务器
        var before = loader.CallCount;
        await reading.LoadAsync(messageId);
        await Task.Delay(50);
        Assert.False(reading.IsBodyLoading);
        Assert.Equal(before, loader.CallCount);
        Assert.Equal("COMPLETE BODY TEXT FROM SERVER", reading.BodyText);
    }

    [Fact]
    public async Task ReadingPane_WithoutLoader_ShowsPreviewWithError()
    {
        await using var tmp = new TempDb();
        await tmp.Database.InitializeAsync();
        var accountId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        await tmp.Database.UpsertAccountAsync(new AccountEntity(
            accountId, "测", "a@b.com", "imap", AuthKind.Password, now, now, null, true));
        var folderId = $"{accountId:D}:inbox";
        await tmp.Database.UpsertFolderAsync(new FolderEntity(
            folderId, accountId, "收件箱", SpecialFolder.Inbox, "INBOX", 0, 1, 0, null, null));
        var messageId = "msg-no-loader";
        const string preview = "短预览文字";
        await tmp.Database.UpsertMessageAsync(new MessageEntity(
            messageId, accountId, folderId, null, null, null, "主题", "a@b.com", "A",
            preview, null, null, now, now, true, false, false, false, MailCategory.Primary, 10, false));

        var tree = new MailboxTreeViewModel(tmp.Database);
        await tree.LoadAsync();
        var reading = new ReadingPaneViewModel(tmp.Database, tree, bodyLoader: null);
        await reading.LoadAsync(messageId);

        var deadline = DateTime.UtcNow.AddSeconds(2);
        while (reading.IsBodyLoading && DateTime.UtcNow < deadline)
        {
            await Task.Delay(20);
        }

        Assert.False(reading.IsBodyLoading);
        Assert.True(reading.HasBodyLoadError);
        Assert.Equal(preview, reading.BodyText);
    }

    private sealed class FakeBodyLoader : IMessageBodyLoader
    {
        private readonly MailDatabase _db;
        private readonly int _delayMs;
        private readonly string _fullText;

        public FakeBodyLoader(MailDatabase db, int delayMs, string fullText)
        {
            _db = db;
            _delayMs = delayMs;
            _fullText = fullText;
        }

        public int CallCount { get; private set; }

        public async Task EnsureLoadedAsync(string messageId, CancellationToken cancellationToken = default)
        {
            CallCount++;
            await Task.Delay(_delayMs, cancellationToken);
            await _db.SaveMessageBodyAsync(messageId, new MessageBodyContent(_fullText, $"<p>{_fullText}</p>", Array.Empty<MessageAttachmentInfo>()), cancellationToken);
        }

        public Task DownloadAttachmentAsync(string attachmentId, string destinationPath, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }
}
