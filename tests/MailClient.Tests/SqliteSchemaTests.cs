using MailClient.Core;
using MailClient.Storage;

namespace MailClient.Tests;

public sealed class SqliteSchemaTests
{
    [Fact]
    public async Task Initialize_Insert_And_FtsSearch_Works()
    {
        var path = Path.Combine(Path.GetTempPath(), "MailClientTests", $"mail-{Guid.NewGuid():N}.db");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        await using var db = new MailDatabase(path);
        await db.InitializeAsync();
        Assert.Equal(7, await db.GetSchemaVersionAsync());

        var accountId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        await db.UpsertAccountAsync(new AccountEntity(
            accountId,
            "工作邮箱",
            "demo@gmail.com",
            "gmail",
            AuthKind.OAuth,
            now,
            now,
            null,
            true));

        await db.UpsertCredentialsMetaAsync(new CredentialsMetaEntity(accountId, $"MailClient/{accountId:D}"));

        var folderId = $"{accountId:D}:inbox";
        await db.UpsertFolderAsync(new FolderEntity(
            folderId,
            accountId,
            "收件箱",
            SpecialFolder.Inbox,
            "INBOX",
            UnreadCount: 1,
            TotalCount: 1,
            SortOrder: 0,
            SyncCursor: "1",
            UidValidity: 42));

        var messageId = Guid.NewGuid().ToString("D");
        await db.UpsertMessageAsync(new MessageEntity(
            messageId,
            accountId,
            folderId,
            RemoteUid: 1001,
            RfcMessageId: "<demo@mail.local>",
            ThreadKey: "thread-1",
            Subject: "项目周报",
            FromAddress: "alice@contoso.com",
            FromName: "Alice",
            Preview: "本周进度更新",
            BodyText: "本周完成了同步骨架与 SQLite 表结构。",
            BodyHtml: "<p>本周完成了同步骨架与 SQLite 表结构。</p>",
            ReceivedAt: now,
            SentAt: now.AddMinutes(-5),
            IsRead: false,
            IsFlagged: true,
            IsDraft: false,
            HasAttachments: false,
            Category: MailCategory.Primary,
            SizeBytes: 2048));

        await db.UpsertRecipientAsync(new RecipientEntity(0, messageId, RecipientKind.To, "demo@gmail.com", "Demo"));
        await db.EnqueueOutboxAsync(new OutboxEntity(
            Guid.NewGuid().ToString("D"),
            accountId,
            """{"to":["bob@contoso.com"],"subject":"hello"}""",
            OutboxStatus.Pending,
            Attempts: 0,
            LastError: null,
            now,
            now));

        var accounts = await db.ListAccountsAsync();
        Assert.Single(accounts);
        Assert.Equal("demo@gmail.com", accounts[0].EmailAddress);

        var folders = await db.ListFoldersAsync(accountId);
        Assert.Single(folders);
        Assert.Equal(SpecialFolder.Inbox, folders[0].Special);

        var listed = await db.ListMessagesAsync(folderId);
        Assert.Single(listed);
        Assert.Contains("Alice", listed[0].From, StringComparison.Ordinal);

        var hits = await db.SearchMessagesAsync("周报");
        Assert.True(hits.Count >= 1, "FTS should hit Chinese subject 周报");
        Assert.Equal(messageId, hits[0].Id);

        var bodyHits = await db.SearchMessagesAsync("SQLite");
        Assert.Single(bodyHits);

        var pending = await db.ListPendingOutboxAsync();
        Assert.Single(pending);

        var meta = await db.GetCredentialsMetaAsync(accountId);
        Assert.NotNull(meta);
        Assert.StartsWith("MailClient/", meta!.VaultKey, StringComparison.Ordinal);

        try
        {
            File.Delete(path);
        }
        catch
        {
            // ignore temp cleanup failures on Windows
        }
    }
}
