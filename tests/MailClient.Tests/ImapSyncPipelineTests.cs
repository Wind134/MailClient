using MailClient.Core;
using MailClient.Providers.Imap;

namespace MailClient.Tests;

public class ImapSyncPipelineTests
{
    [Fact]
    public void BuildFolderId_Stable()
    {
        var id = Guid.Parse("11111111-1111-1111-1111-111111111111");
        Assert.Equal("11111111-1111-1111-1111-111111111111:INBOX",
            ImapSyncPipeline.BuildFolderId(id, "INBOX"));
    }

    private static MailMessageSyncItem Item(string subject, string from, MailCategory? category = null)
    {
        var summary = new MailMessageSummary(
            "m", "INBOX", subject, from, DateTimeOffset.UtcNow, IsRead: false, IsFlagged: false, Preview: "");
        return new MailMessageSyncItem(summary, 1, null, from, null, false, 0, false, null, category);
    }

    [Fact]
    public void ToMessageEntity_Uses_Server_Category_When_Present()
    {
        var entity = ImapSyncPipeline.ToMessageEntity(Guid.NewGuid(), "f", Item("你好", "friend@x.com", MailCategory.Promotions));
        Assert.Equal(MailCategory.Promotions, entity.Category);
    }

    [Fact]
    public void ToMessageEntity_Leaves_Category_Null_Without_Server_Category()
    {
        // 非 Gmail：不再跑 MailCategoryClassifier；无服务器分类时保持 null，UI 也不按 Category 过滤。
        var entity = ImapSyncPipeline.ToMessageEntity(Guid.NewGuid(), "f", Item("您的订单已发货", "shop@x.com"));
        Assert.Null(entity.Category);
    }

    [Fact]
    public void ToMessageEntity_MapsFields()
    {
        var accountId = Guid.NewGuid();
        var summary = new MailMessageSummary(
            "msg-1", "INBOX", "主题", "A <a@b.com>", DateTimeOffset.Parse("2024-01-02T03:04:05Z"),
            IsRead: true, IsFlagged: false, Preview: "预览");
        var item = new MailMessageSyncItem(
            summary, RemoteUid: 42, RfcMessageId: "<x@y>", FromAddress: "a@b.com", FromName: "A",
            HasAttachments: true, SizeBytes: 100, IsDraft: false, SentAt: summary.ReceivedAt);

        var entity = ImapSyncPipeline.ToMessageEntity(accountId, "acc:INBOX", item);
        Assert.Equal("msg-1", entity.Id);
        Assert.Equal(accountId, entity.AccountId);
        Assert.Equal("acc:INBOX", entity.FolderId);
        Assert.Equal(42, entity.RemoteUid);
        Assert.Equal("a@b.com", entity.FromAddress);
        Assert.True(entity.HasAttachments);
        Assert.True(entity.IsRead);
    }
}
