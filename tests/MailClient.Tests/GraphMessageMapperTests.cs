using MailClient.Providers.Graph;
using Microsoft.Graph.Models;

namespace MailClient.Tests;

public class GraphMessageMapperTests
{
    [Fact]
    public void ToSyncItem_maps_core_fields()
    {
        var accountId = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");
        var message = new Message
        {
            Id = "AAMkAGGraphId123",
            Subject = "Hello Graph",
            BodyPreview = "preview text that is long enough to truncate " + new string('x', 300),
            From = new Recipient
            {
                EmailAddress = new EmailAddress { Address = "a@contoso.com", Name = "Alice" }
            },
            ReceivedDateTime = DateTimeOffset.Parse("2026-10-01T08:00:00Z"),
            SentDateTime = DateTimeOffset.Parse("2026-10-01T07:59:00Z"),
            IsRead = false,
            IsDraft = false,
            HasAttachments = true,
            InternetMessageId = "<id@contoso.com>",
            Flag = new FollowupFlag { FlagStatus = FollowupFlagStatus.Flagged }
        };

        var item = GraphMessageMapper.ToSyncItem(accountId, "folder-1", message, previewMaxChars: 40);

        Assert.Equal($"{accountId:D}:AAMkAGGraphId123", item.Summary.Id);
        Assert.Equal("folder-1", item.Summary.FolderId);
        Assert.Equal("Hello Graph", item.Summary.Subject);
        Assert.Equal("Alice <a@contoso.com>", item.Summary.From);
        Assert.False(item.Summary.IsRead);
        Assert.True(item.Summary.IsFlagged);
        Assert.True(item.HasAttachments);
        Assert.Equal("<id@contoso.com>", item.RfcMessageId);
        Assert.Equal("a@contoso.com", item.FromAddress);
        Assert.Equal("Alice", item.FromName);
        Assert.Equal(40, item.Summary.Preview.Length);
        Assert.True(item.RemoteUid > 0);
        Assert.Equal(GraphMessageMapper.StableUid("AAMkAGGraphId123"), item.RemoteUid);
    }

    [Fact]
    public void StableUid_is_deterministic()
    {
        Assert.Equal(GraphMessageMapper.StableUid("abc"), GraphMessageMapper.StableUid("abc"));
        Assert.NotEqual(GraphMessageMapper.StableUid("abc"), GraphMessageMapper.StableUid("abd"));
    }
}
