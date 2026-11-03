using MailClient.Providers.Imap;
using MimeKit;

namespace MailClient.Tests;

public class MimeMessageMapperTests
{
    [Fact]
    public void BuildPreview_FromTextBody_Truncates()
    {
        var message = new MimeMessage();
        message.Body = new TextPart("plain") { Text = new string('啊', 50) + new string('a', 200) };
        var preview = MimeMessageMapper.BuildPreview(message, maxChars: 20);
        Assert.True(preview.Length <= 21); // 20 + ellipsis
        Assert.EndsWith("…", preview);
    }

    [Fact]
    public void BuildPreview_FallsBackToHtml()
    {
        var message = new MimeMessage();
        message.Body = new TextPart("html") { Text = "<p>Hello&nbsp;<b>World</b></p>" };
        var preview = MimeMessageMapper.BuildPreview(message);
        Assert.Contains("Hello", preview);
        Assert.Contains("World", preview);
        Assert.DoesNotContain("<", preview);
    }

    [Fact]
    public void GetFrom_And_FormatDisplay()
    {
        var message = new MimeMessage();
        message.From.Add(new MailboxAddress("张三", "zhang@example.com"));
        var (address, name) = MimeMessageMapper.GetFrom(message);
        Assert.Equal("zhang@example.com", address);
        Assert.Equal("张三", name);
        Assert.Equal("张三 <zhang@example.com>", MimeMessageMapper.FormatFromDisplay(address, name));
    }

    [Fact]
    public void TruncatePreview_Empty()
    {
        Assert.Equal(string.Empty, MimeMessageMapper.TruncatePreview("   "));
    }
}
