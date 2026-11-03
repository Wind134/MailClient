using MailClient.Core;
using MailClient.Providers.Imap;
using MimeKit;

namespace MailClient.Tests;

public sealed class CidHtmlRewriterTests
{
    [Fact]
    public void NormalizeContentId_StripsAngleBrackets_And_CidPrefix()
    {
        Assert.Equal("logo@workos", CidHtmlRewriter.NormalizeContentId("<logo@workos>"));
        Assert.Equal("logo@workos", CidHtmlRewriter.NormalizeContentId("cid:logo@workos"));
        Assert.Equal("logo@workos", CidHtmlRewriter.NormalizeContentId("cid:logo%40workos"));
        Assert.Equal("logo@workos", CidHtmlRewriter.NormalizeContentId("  <logo@workos>  "));
    }

    [Fact]
    public void Rewrite_DoubleQuotedSrc_WithAngleBracketContentId()
    {
        var png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==");
        var html = """<img src="cid:logo@workos" width="80">""";
        var result = CidHtmlRewriter.Rewrite(html, [new InlineCidImage("<logo@workos>", "image/png", png)]);
        Assert.DoesNotContain("cid:", result, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("src=\"data:image/png;base64,", result, StringComparison.Ordinal);
        Assert.Contains(Convert.ToBase64String(png), result, StringComparison.Ordinal);
    }

    [Fact]
    public void Rewrite_SingleQuoted_And_Unquoted_Src()
    {
        var bytes = new byte[] { 1, 2, 3, 4 };
        var html = """<img src='cid:a@x'><img src=cid:b@y alt=x>""";
        var result = CidHtmlRewriter.Rewrite(html,
        [
            new InlineCidImage("a@x", "image/gif", bytes),
            new InlineCidImage("b@y", "image/gif", bytes),
        ]);
        Assert.DoesNotContain("cid:", result, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("data:image/gif;base64,", result, StringComparison.Ordinal);
    }

    [Fact]
    public void Rewrite_MultipleRelatedParts_OnlyMatchingReplaced()
    {
        var a = new byte[] { 10 };
        var b = new byte[] { 20 };
        var html = """<img src="cid:one@x"><img src="cid:two@x"><img src="cid:missing@x">""";
        var result = CidHtmlRewriter.Rewrite(html,
        [
            new InlineCidImage("one@x", "image/png", a),
            new InlineCidImage("two@x", "image/jpeg", b),
        ]);
        Assert.Contains("data:image/png;base64," + Convert.ToBase64String(a), result, StringComparison.Ordinal);
        Assert.Contains("data:image/jpeg;base64," + Convert.ToBase64String(b), result, StringComparison.Ordinal);
        Assert.Contains("src=\"cid:missing@x\"", result, StringComparison.Ordinal);
    }

    [Fact]
    public void Rewrite_MissingCid_LeftAlone()
    {
        var html = """<p><img src="cid:nope@x"></p>""";
        var result = CidHtmlRewriter.Rewrite(html, [new InlineCidImage("other@x", "image/png", new byte[] { 1 })]);
        Assert.Equal(html, result);
    }

    [Fact]
    public void Rewrite_FromMessageAttachmentInfo_ContentBytes()
    {
        var bytes = new byte[] { 9, 8, 7 };
        var html = """<img src="cid:logo@workos">""";
        var result = CidHtmlRewriter.Rewrite(html,
        [
            new MessageAttachmentInfo("logo.png", "image/png", bytes.Length, "logo@workos", true, null, bytes)
        ]);
        Assert.Contains("data:image/png;base64,", result, StringComparison.Ordinal);
        Assert.DoesNotContain("cid:", result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ExtractContent_MultipartRelated_RewritesCidToDataUri()
    {
        var png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==");
        var message = new MimeMessage();
        var related = new Multipart("related");
        related.Add(new TextPart("html")
        {
            Text = """<html><body><img src="cid:logo@workos" width="80"><p>完成验证码挑战 894931</p></body></html>"""
        });
        var image = new MimePart("image", "png")
        {
            Content = new MimeKit.MimeContent(new MemoryStream(png)),
            ContentId = "logo@workos",
            ContentDisposition = new ContentDisposition(ContentDisposition.Inline),
            FileName = "logo.png"
        };
        related.Add(image);
        message.Body = related;

        var content = MimeMessageMapper.ExtractContent(message);
        Assert.NotNull(content.BodyHtml);
        Assert.DoesNotContain("cid:", content.BodyHtml!, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("data:image/png;base64,", content.BodyHtml!, StringComparison.Ordinal);
        Assert.Contains("894931", content.BodyHtml!, StringComparison.Ordinal);
        Assert.Contains(content.Attachments, a =>
            a.IsInline &&
            string.Equals(a.ContentId, "logo@workos", StringComparison.OrdinalIgnoreCase) &&
            a.ContentBytes is { Length: > 0 });
    }

    [Fact]
    public void ContainsUnresolvedCid_Detects()
    {
        Assert.True(CidHtmlRewriter.ContainsUnresolvedCid("""<img src="cid:x">"""));
        Assert.False(CidHtmlRewriter.ContainsUnresolvedCid("""<img src="data:image/png;base64,aa">"""));
        Assert.False(CidHtmlRewriter.ContainsUnresolvedCid(null));
    }
}
