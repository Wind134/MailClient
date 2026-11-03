using MailClient.Core;
using MailClient.Providers.Imap;
using MimeKit;

namespace MailClient.Tests;

public class HtmlPreviewTests
{
    private const string GrokStyleFirst = """
        <html><head><style type="text/css">
        <!--
        table { border-collapse: separate; table-layout: fixed; mso-table-lspace: 0pt; mso-table-rspace: 0pt; }
        -->
        </style></head><body>
        <h1>要闻推送任务 is ready</h1>
        <p>今日要点：Cursor 市场恶意扩展事件，影响约 5000 用户。</p>
        </body></html>
        """;

    private const string ExchangeOutlookCss = """
        <html><head><style>
        #outlook a { padding:0; }
        body { margin:0;padding:0;-webkit-text-size-adjust:100%; }
        </style></head><body>
        <p>【重要提醒】关于安全事件进展的情况说明，我们已完成排查。</p>
        </body></html>
        """;

    private const string ChatGptBgcolorCss = """
        <html><head><style>
        body.generic[bgcolor], .wrapperMod[bgcolor], .email-shell[bgcolor] { background:#fff !important; }
        </style></head><body>
        <p>Halloween costumes for your whole crew — shop the collection today.</p>
        </body></html>
        """;

    [Fact]
    public void HtmlToPreview_GrokNewsletter_IsProseNotCss()
    {
        var preview = HtmlPreview.HtmlToPreview(GrokStyleFirst, maxChars: 160);
        Assert.Contains("要闻推送", preview);
        Assert.Contains("Cursor", preview);
        Assert.DoesNotContain("border-collapse", preview);
        Assert.DoesNotContain("mso-table", preview);
        Assert.False(HtmlPreview.LooksLikeCss(preview));
    }

    [Fact]
    public void HtmlToPreview_ExchangeLike_IsChineseProse()
    {
        var preview = HtmlPreview.HtmlToPreview(ExchangeOutlookCss, maxChars: 160);
        Assert.Contains("重要提醒", preview);
        Assert.Contains("安全事件", preview);
        Assert.DoesNotContain("#outlook", preview);
        Assert.DoesNotContain("-webkit-text-size-adjust", preview);
    }

    [Fact]
    public void HtmlToPreview_ChatGptLike_IsEnglishProse()
    {
        var preview = HtmlPreview.HtmlToPreview(ChatGptBgcolorCss, maxChars: 160);
        Assert.Contains("Halloween", preview);
        Assert.DoesNotContain("body.generic", preview);
        Assert.DoesNotContain("wrapperMod", preview);
    }

    [Fact]
    public void BuildPreview_PrefersPlainText_OverHtml()
    {
        var message = new MimeMessage();
        var alt = new Multipart("alternative");
        alt.Add(new TextPart("plain") { Text = "纯文本预览内容，不应出现样式。" });
        alt.Add(new TextPart("html") { Text = GrokStyleFirst });
        message.Body = alt;
        var preview = MimeMessageMapper.BuildPreview(message, maxChars: 80);
        Assert.Contains("纯文本预览", preview);
        Assert.DoesNotContain("border-collapse", preview);
    }

    [Fact]
    public void BuildPreview_HtmlOnly_StripsStyle()
    {
        var message = new MimeMessage();
        message.Body = new TextPart("html") { Text = ExchangeOutlookCss };
        var preview = MimeMessageMapper.BuildPreview(message);
        Assert.Contains("重要提醒", preview);
        Assert.False(HtmlPreview.LooksLikeCss(preview));
    }

    [Fact]
    public void ExtractContent_Preview_NotCss()
    {
        var message = new MimeMessage();
        message.Body = new TextPart("html") { Text = GrokStyleFirst };
        var content = MimeMessageMapper.ExtractContent(message, previewMaxChars: 120);
        Assert.Contains("要闻", content.Preview);
        Assert.False(HtmlPreview.LooksLikeCss(content.Preview));
        Assert.False(HtmlPreview.LooksLikeCss(content.BodyText));
        Assert.NotNull(content.BodyHtml);
    }

    [Fact]
    public void LooksLikeCss_DetectsKnownPolluters()
    {
        Assert.True(HtmlPreview.LooksLikeCss("-- table { border-collapse: separate; mso-table-lspace: 0pt;"));
        Assert.True(HtmlPreview.LooksLikeCss("#outlook a { padding:0; } body { margin:0;padding:0;"));
        Assert.True(HtmlPreview.LooksLikeCss("body.generic[bgcolor], .wrapperMod[bgcolor] { background:#fff; }"));
        Assert.False(HtmlPreview.LooksLikeCss("今日要点：Cursor 市场恶意扩展事件"));
    }

    [Fact]
    public void ResolveDisplayName_FallsBackToLocalPart()
    {
        Assert.Equal("工作邮箱", HtmlPreview.ResolveDisplayName(" 工作邮箱 ", "a@b.com"));
        Assert.Equal("alice", HtmlPreview.ResolveDisplayName("  ", "alice@contoso.com"));
        Assert.Equal("bob", HtmlPreview.ResolveDisplayName(null, "bob@example.com"));
        Assert.Equal("未命名账号", HtmlPreview.ResolveDisplayName("", ""));
        Assert.Equal("onlylocal", HtmlPreview.ResolveDisplayName(null, "onlylocal"));
    }
}
