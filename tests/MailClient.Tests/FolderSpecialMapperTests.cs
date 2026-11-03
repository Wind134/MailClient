using MailClient.Providers.Imap;
using MailKit;
using SpecialFolder = MailClient.Core.SpecialFolder;

namespace MailClient.Tests;

public class FolderSpecialMapperTests
{
    [Theory]
    [InlineData("INBOX", "INBOX", SpecialFolder.Inbox)]
    [InlineData("收件箱", "收件箱", SpecialFolder.Inbox)]
    [InlineData("Sent", "Sent", SpecialFolder.Sent)]
    [InlineData("Sent Messages", "Sent Messages", SpecialFolder.Sent)]
    [InlineData("已发送", "已发送", SpecialFolder.Sent)]
    [InlineData("Drafts", "Drafts", SpecialFolder.Drafts)]
    [InlineData("草稿箱", "草稿箱", SpecialFolder.Drafts)]
    [InlineData("Trash", "Trash", SpecialFolder.Trash)]
    [InlineData("Deleted Messages", "Deleted Messages", SpecialFolder.Trash)]
    [InlineData("废纸篓", "废纸篓", SpecialFolder.Trash)]
    [InlineData("Flagged", "Flagged", SpecialFolder.Flagged)]
    [InlineData("星标邮件", "星标邮件", SpecialFolder.Flagged)]
    [InlineData("INBOX.Work", "Work", SpecialFolder.Custom)]
    [InlineData("Junk", "Junk", SpecialFolder.Junk)]
    [InlineData("Junk Email", "Junk Email", SpecialFolder.Junk)]
    [InlineData("垃圾邮件", "垃圾邮件", SpecialFolder.Junk)]
    [InlineData("[Gmail]/Spam", "Spam", SpecialFolder.Junk)]
    [InlineData("Archive", "Archive", SpecialFolder.Archive)]
    [InlineData("归档", "归档", SpecialFolder.Archive)]
    [InlineData("[Gmail]/All Mail", "All Mail", SpecialFolder.Archive)]
    [InlineData("[Google Mail]/All Mail", "All Mail", SpecialFolder.Archive)]
    [InlineData("[Gmail]/Sent Mail", "Sent Mail", SpecialFolder.Sent)]
    [InlineData("[Gmail]/Drafts", "Drafts", SpecialFolder.Drafts)]
    [InlineData("[Gmail]/Trash", "Trash", SpecialFolder.Trash)]
    [InlineData("[Gmail]/Starred", "Starred", SpecialFolder.Flagged)]
    [InlineData("[Gmail]/Important", "Important", SpecialFolder.Custom)]
    public void MapByName_Heuristics(string fullName, string name, SpecialFolder expected)
    {
        Assert.Equal(expected, FolderSpecialMapper.MapByName(fullName, name));
    }

    [Fact]
    public void Map_PrefersSpecialUseAttributes()
    {
        // 名称像自定义，但属性标明 Sent
        var mapped = FolderSpecialMapper.Map(FolderAttributes.Sent, "INBOX.Foo", "Foo");
        Assert.Equal(SpecialFolder.Sent, mapped);
    }

    [Theory]
    [InlineData(FolderAttributes.Archive, SpecialFolder.Archive)]
    [InlineData(FolderAttributes.All, SpecialFolder.Archive)]
    [InlineData(FolderAttributes.Junk, SpecialFolder.Junk)]
    [InlineData(FolderAttributes.Flagged, SpecialFolder.Flagged)]
    [InlineData(FolderAttributes.Trash, SpecialFolder.Trash)]
    [InlineData(FolderAttributes.Drafts, SpecialFolder.Drafts)]
    public void Map_SpecialUse_Attributes_Win_Over_Custom_Names(FolderAttributes attributes, SpecialFolder expected)
    {
        Assert.Equal(expected, FolderSpecialMapper.Map(attributes, "[Gmail]/某个本地化名称", "某个本地化名称"));
    }

    [Fact]
    public void DefaultSortOrder_Archive_And_Junk_Sit_Between_Sent_And_Trash()
    {
        var sent = FolderSpecialMapper.DefaultSortOrder(SpecialFolder.Sent);
        var archive = FolderSpecialMapper.DefaultSortOrder(SpecialFolder.Archive);
        var junk = FolderSpecialMapper.DefaultSortOrder(SpecialFolder.Junk);
        var trash = FolderSpecialMapper.DefaultSortOrder(SpecialFolder.Trash);
        Assert.True(sent < archive && archive < junk && junk < trash && trash < 100);
    }

    [Fact]
    public void DefaultSortOrder_InboxFirst()
    {
        Assert.True(FolderSpecialMapper.DefaultSortOrder(SpecialFolder.Inbox) <
                    FolderSpecialMapper.DefaultSortOrder(SpecialFolder.Trash));
    }
}
