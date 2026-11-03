using MailClient.Core;
using MailClient.Storage;

namespace MailClient.Tests;

/// <summary>发送 / 写回测试共用的数据：一个非演示账号 + 收件箱 / 已发送 / 归档 / 已删除文件夹 + 几封邮件。</summary>
internal sealed class SendFixture : IAsyncDisposable
{
    public static readonly DateTimeOffset T0 = new(2026, 10, 31, 12, 0, 0, TimeSpan.Zero);

    private SendFixture(TempDb tmp, Guid accountId, string providerId)
    {
        Tmp = tmp;
        AccountId = accountId;
        ProviderId = providerId;
    }

    public TempDb Tmp { get; }
    public MailDatabase Db => Tmp.Database;
    public Guid AccountId { get; }
    public string ProviderId { get; }
    public string Address => "me@example.com";

    public string FolderId(string name) => $"{AccountId:D}:{name}";

    public static async Task<SendFixture> CreateAsync(string providerId = "imap.generic", bool archiveFolder = true)
    {
        var tmp = new TempDb();
        await tmp.Database.InitializeAsync();
        var f = new SendFixture(tmp, Guid.NewGuid(), providerId);
        await tmp.Database.UpsertAccountAsync(new AccountEntity(
            f.AccountId, "我", f.Address, providerId, AuthKind.Password, T0, T0, null, true));
        var order = 0;
        foreach (var (name, special) in new[]
                 {
                     ("INBOX", SpecialFolder.Inbox), ("Sent", SpecialFolder.Sent), ("Archive", SpecialFolder.Archive),
                     ("Trash", SpecialFolder.Trash), ("Drafts", SpecialFolder.Drafts)
                 })
        {
            if (special == SpecialFolder.Archive && !archiveFolder)
            {
                continue;
            }

            await tmp.Database.UpsertFolderAsync(new FolderEntity(
                f.FolderId(name), f.AccountId, name, special, name, 0, 0, order++, null, 777));
        }

        return f;
    }

    public MessageEntity Message(string folder, long uid, bool read = false, bool flagged = false, string? rfcId = null)
    {
        var folderId = FolderId(folder);
        return new MessageEntity(
            $"{folderId}:{uid}", AccountId, folderId, uid, rfcId ?? $"m{uid}@remote.example", null,
            $"主题 {uid}", "alice@example.com", "Alice", "预览", "正文", "<p>正文</p>",
            T0, T0, read, flagged, false, false, null, 100);
    }

    public async Task<MessageEntity> AddMessageAsync(string folder, long uid, bool read = false, bool flagged = false, string? rfcId = null)
    {
        var m = Message(folder, uid, read, flagged, rfcId);
        await Db.UpsertMessagesAsync([m]);
        await Db.RecalculateFolderCountsAsync();
        return m;
    }

    public OutgoingMailMessage Outgoing(string to = "bob@example.com", string subject = "你好")
        => new(AccountId.ToString("D"), [to], [], [], subject, "<p>内容</p>", []);

    public ValueTask DisposeAsync() => Tmp.DisposeAsync();
}
