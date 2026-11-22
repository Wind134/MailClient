using System.Text;
using MailClient.Core;
using MailClient.Core.Services;
using MailClient.Providers.Imap;
using MailClient.Storage.Services;
using MailClient.ViewModels;
using MimeKit;

namespace MailClient.Tests;

public class MimeEncodingTests
{
    static MimeEncodingTests()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    [Fact]
    public void ExtractContent_Decodes_Gbk_Html_Without_Charset_Header()
    {
        var gbk = Encoding.GetEncoding("gb18030");
        var htmlBytes = gbk.GetBytes("<html><body><p>尊敬的用户：您的支付宝交易流水明细已生成。</p></body></html>");

        var part = new TextPart("html")
        {
            Content = new MimeKit.MimeContent(new MemoryStream(htmlBytes), ContentEncoding.Default)
        };
        part.ContentType.Charset = null;

        var message = new MimeMessage { Body = part };
        var content = MimeMessageMapper.ExtractContent(message);

        Assert.Contains("支付宝交易流水明细", content.BodyHtml);
        Assert.Contains("支付宝交易流水明细", content.BodyText);
        Assert.Contains("支付宝交易流水明细", content.Preview);
        Assert.DoesNotContain('\uFFFD', content.BodyText);
    }

    [Fact]
    public void ExtractContent_Decodes_Gbk_Html_Mislabelled_As_Utf8()
    {
        var gbk = Encoding.GetEncoding("gb18030");
        var htmlBytes = gbk.GetBytes("<html><head><meta charset=\"gbk\"></head><body>账单金额：128.00 元</body></html>");

        var part = new TextPart("html")
        {
            Content = new MimeKit.MimeContent(new MemoryStream(htmlBytes), ContentEncoding.Default)
        };
        part.ContentType.Charset = "utf-8";

        var message = new MimeMessage { Body = part };
        var content = MimeMessageMapper.ExtractContent(message);

        Assert.Contains("账单金额：128.00 元", content.BodyHtml);
        Assert.Contains("账单金额：128.00 元", content.BodyText);
    }

    [Fact]
    public void ExtractContent_Decodes_Gbk_PlainText_Without_Charset()
    {
        var gbk = Encoding.GetEncoding("gb18030");
        var textBytes = gbk.GetBytes("您好，这是 GBK 编码的纯文本邮件。");

        var part = new TextPart("plain")
        {
            Content = new MimeKit.MimeContent(new MemoryStream(textBytes), ContentEncoding.Default)
        };
        part.ContentType.Charset = null;

        var message = new MimeMessage { Body = part };
        var content = MimeMessageMapper.ExtractContent(message);

        Assert.Equal("您好，这是 GBK 编码的纯文本邮件。", content.BodyText);
    }

    [Fact]
    public void RepairFileName_Fixes_Latin1_Garbled_Gbk_Alipay_Attachment()
    {
        var garbled = "Ö§¸¶±¦½»Ò×Ã÷Ï¸(20260101-20261122).zip";
        var fixedName = MimeMessageMapper.RepairFileName(garbled);

        Assert.Equal("支付宝交易明细(20260101-20261122).zip", fixedName);
    }

    [Theory]
    [InlineData("résumé.pdf")]
    [InlineData("über.txt")]
    [InlineData("正常中文文件名.pdf")]
    [InlineData("report-2026.xlsx")]
    public void RepairFileName_Preserves_Normal_And_European_Names(string fileName)
        => Assert.Equal(fileName, MimeMessageMapper.RepairFileName(fileName));

    [Fact]
    public void TryWriteAttachment_Matches_Repaired_FileName()
    {
        var payload = "zip-bytes"u8.ToArray();
        var attachment = new MimePart("application", "zip")
        {
            Content = new MimeKit.MimeContent(new MemoryStream(payload)),
            ContentDisposition = new ContentDisposition(ContentDisposition.Attachment)
            {
                FileName = "Ö§¸¶±¦½»Ò×Ã÷Ï¸(20260101-20261122).zip"
            }
        };

        var message = new MimeMessage { Body = new Multipart("mixed") { new TextPart("plain") { Text = "hi" }, attachment } };
        var listed = MimeMessageMapper.ListAttachmentInfo(message);
        Assert.Single(listed);
        Assert.Equal("支付宝交易明细(20260101-20261122).zip", listed[0].FileName);

        using var output = new MemoryStream();
        Assert.True(MimeMessageMapper.TryWriteAttachment(message, listed[0].FileName, output));
        Assert.Equal(payload, output.ToArray());
    }

    [Fact]
    public void GarbledText_Detects_Cached_Replacement_Chars_And_Latin1_FileNames()
    {
        Assert.True(GarbledText.LooksGarbled("\uFFFDII\uFFFD\uFFFDû\uFFFDã", null));
        Assert.True(GarbledText.LooksGarbled(null, "<p>\uFFFD\uFFFD乱码</p>"));
        Assert.False(GarbledText.LooksGarbled("正常中文正文", "<p>正常 HTML</p>"));
        Assert.True(GarbledText.LooksGarbledFileName("Ö§¸¶±¦½»Ò×Ã÷Ï¸(20260101-20261122).zip"));
        Assert.False(GarbledText.LooksGarbledFileName("résumé.pdf"));
        Assert.False(GarbledText.LooksGarbledFileName("支付宝交易明细.zip"));
    }
}

public class RefreshScopeAndAccountOrderTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 22, 12, 0, 0, TimeSpan.FromHours(8));

    private sealed class RecordingSyncService : ISyncService
    {
        public int SyncAllCalls { get; private set; }
        public List<Guid> SyncedAccountIds { get; } = [];

        public Task SyncAllAsync(CancellationToken cancellationToken = default)
        {
            SyncAllCalls++;
            return Task.CompletedTask;
        }

        public Task SyncAccountAsync(Guid accountId, CancellationToken cancellationToken = default)
        {
            SyncedAccountIds.Add(accountId);
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task Refresh_Syncs_Only_Selected_Account_And_Syncs_All_In_Favorites()
    {
        await using var tmp = new TempDb();
        await tmp.Database.InitializeAsync();

        var gmailId = Guid.NewGuid();
        var qqId = Guid.NewGuid();
        await tmp.Database.UpsertAccountAsync(new AccountEntity(gmailId, "Gmail", "a@gmail.com", ProviderIds.Gmail, AuthKind.OAuth, Now, Now, null, true));
        await tmp.Database.UpsertAccountAsync(new AccountEntity(qqId, "QQ", "b@qq.com", "imap.generic", AuthKind.Password, Now, Now, null, true));
        await tmp.Database.UpsertFolderAsync(new FolderEntity($"{gmailId:D}:INBOX", gmailId, "收件箱", SpecialFolder.Inbox, "INBOX", 0, 0, 0, null));
        await tmp.Database.UpsertFolderAsync(new FolderEntity($"{qqId:D}:INBOX", qqId, "收件箱", SpecialFolder.Inbox, "INBOX", 0, 0, 0, null));

        var sync = new RecordingSyncService();
        var settings = new AppSettingsStore(path: null);
        var shell = new ShellViewModel(tmp.Database, new OutboxSendService(tmp.Database), () => Now, syncService: sync, settings: settings);
        await shell.InitializeAsync();

        // 1. 默认选中「所有收件箱」：刷新应同步全部账号
        await shell.RefreshCommand.ExecuteAsync(null);
        Assert.Equal(1, sync.SyncAllCalls);
        Assert.Empty(sync.SyncedAccountIds);

        // 2. 切到 Gmail 收件箱：刷新只应同步 Gmail
        var gmailInbox = shell.Tree.FindNode(n => n.AccountId == gmailId && n.Special == SpecialFolder.Inbox);
        Assert.NotNull(gmailInbox);
        shell.Tree.SelectedNode = gmailInbox;

        await shell.RefreshCommand.ExecuteAsync(null);
        Assert.Equal(1, sync.SyncAllCalls);
        Assert.Equal([gmailId], sync.SyncedAccountIds);
    }

    [Fact]
    public async Task MoveAccount_Reorders_Sidebar_And_Persists_In_Settings()
    {
        await using var tmp = new TempDb();
        await tmp.Database.InitializeAsync();

        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var c = Guid.NewGuid();
        await tmp.Database.UpsertAccountAsync(new AccountEntity(a, "A", "a@example.com", ProviderIds.Gmail, AuthKind.OAuth, Now, Now, null, true));
        await tmp.Database.UpsertAccountAsync(new AccountEntity(b, "B", "b@example.com", "imap.generic", AuthKind.Password, Now, Now, null, true));
        await tmp.Database.UpsertAccountAsync(new AccountEntity(c, "C", "c@example.com", ProviderIds.MicrosoftGraph, AuthKind.OAuth, Now, Now, null, true));

        var settings = new AppSettingsStore(path: null);
        var tree = new MailboxTreeViewModel(tmp.Database)
        {
            AccountOrderResolver = settings.GetAccountOrder,
            AccountOrderSaver = settings.SetAccountOrder
        };

        await tree.LoadAsync();
        Assert.Equal([a, b, c], tree.Accounts.Select(x => x.AccountId!.Value).ToArray());

        // 把 C 拖到 A 前面 → [C, A, B]
        var rebuilt = 0;
        tree.Rebuilt += (_, _) => rebuilt++;
        Assert.True(tree.MoveAccount(c, a, insertAfter: false));
        Assert.Equal(1, rebuilt);
        Assert.Equal([c, a, b], tree.Accounts.Select(x => x.AccountId!.Value).ToArray());
        Assert.Equal([c, a, b], settings.GetAccountOrder().ToArray());

        // 把 C 拖到 B 后面 → [A, B, C]
        Assert.True(tree.MoveAccount(c, b, insertAfter: true));
        Assert.Equal([a, b, c], tree.Accounts.Select(x => x.AccountId!.Value).ToArray());

        // 右键上移：把 B 上移一位 → [B, A, C]
        Assert.True(tree.MoveAccountByOffset(b, -1));
        Assert.Equal([b, a, c], tree.Accounts.Select(x => x.AccountId!.Value).ToArray());

        // 模拟重启重新加载：仍保持 [B, A, C]，新增的 D 排在末尾
        var d = Guid.NewGuid();
        await tmp.Database.UpsertAccountAsync(new AccountEntity(d, "D", "d@example.com", "imap.generic", AuthKind.Password, Now, Now, null, true));

        var reloaded = new MailboxTreeViewModel(tmp.Database)
        {
            AccountOrderResolver = settings.GetAccountOrder,
            AccountOrderSaver = settings.SetAccountOrder
        };
        await reloaded.LoadAsync();
        Assert.Equal([b, a, c, d], reloaded.Accounts.Select(x => x.AccountId!.Value).ToArray());
    }
}
