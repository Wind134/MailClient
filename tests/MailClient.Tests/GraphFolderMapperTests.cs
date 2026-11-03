using MailClient.Core;
using MailClient.Providers.Graph;

namespace MailClient.Tests;

public class GraphFolderMapperTests
{
    [Theory]
    [InlineData("Inbox", null, SpecialFolder.Inbox)]
    [InlineData("收件箱", null, SpecialFolder.Inbox)]
    [InlineData("Sent Items", null, SpecialFolder.Sent)]
    [InlineData("已发送", null, SpecialFolder.Sent)]
    [InlineData("Drafts", null, SpecialFolder.Drafts)]
    [InlineData("Deleted Items", null, SpecialFolder.Trash)]
    [InlineData("Projects", null, SpecialFolder.Custom)]
    [InlineData("Anything", "inbox", SpecialFolder.Inbox)]
    [InlineData("Anything", "sentitems", SpecialFolder.Sent)]
    [InlineData("Anything", "deleteditems", SpecialFolder.Trash)]
    [InlineData("Anything", "drafts", SpecialFolder.Drafts)]
    [InlineData("Anything", "junkemail", SpecialFolder.Junk)]
    [InlineData("Anything", "archive", SpecialFolder.Archive)]
    [InlineData("Anything", "outbox", SpecialFolder.Outbox)]
    [InlineData("Junk Email", null, SpecialFolder.Junk)]
    [InlineData("Archive", null, SpecialFolder.Archive)]
    public void Map_display_and_wellknown(string? display, string? wellKnown, SpecialFolder expected)
    {
        Assert.Equal(expected, GraphFolderMapper.Map(display, wellKnown));
    }
}
