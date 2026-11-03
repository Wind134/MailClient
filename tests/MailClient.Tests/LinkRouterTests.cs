using MailClient.Core.Links;

namespace MailClient.Tests;

public class LinkRouterTests
{
    [Theory]
    [InlineData("http://example.com/a?b=1")]
    [InlineData("https://example.com")]
    [InlineData("HTTPS://EXAMPLE.COM/X")]
    public void Http_Follows_Setting(string url)
    {
        var system = LinkRouter.Route(url, LinkOpenMode.System);
        Assert.Equal(LinkAction.OpenSystem, system.Action);
        Assert.NotNull(system.Uri);
        var inApp = LinkRouter.Route(url, LinkOpenMode.Internal);
        Assert.Equal(LinkAction.OpenInternal, inApp.Action);
        Assert.Equal(system.Uri, inApp.Uri);
    }

    [Theory]
    [InlineData("javascript:alert(1)")]
    [InlineData("JavaScript:void(0)")]
    [InlineData("file:///C:/Windows/System32/calc.exe")]
    [InlineData("vbscript:msgbox(1)")]
    [InlineData("ftp://example.com/x")]
    [InlineData("ms-settings:notifications")]
    [InlineData("ms-msdt:/id PCWDiagnostic")]
    [InlineData("search-ms:query=x")]
    [InlineData("data:text/html,<script>alert(1)</script>")]
    [InlineData("about:blank")]
    [InlineData("/relative/path")]
    [InlineData("#anchor")]
    [InlineData("")]
    public void Dangerous_Or_Unknown_Is_Blocked_When_User_Clicks(string url)
    {
        foreach (var mode in new[] { LinkOpenMode.System, LinkOpenMode.Internal })
        {
            Assert.Equal(LinkAction.Block, LinkRouter.Route(url, mode).Action);
        }
    }

    [Theory]
    [InlineData("javascript:alert(1)")]
    [InlineData("file:///C:/x")]
    [InlineData("ms-settings:privacy")]
    public void Dangerous_Is_Blocked_Even_When_Not_User_Initiated(string url)
        => Assert.Equal(LinkAction.Block, LinkRouter.Route(url, LinkOpenMode.System, isUserInitiated: false).Action);

    [Theory]
    [InlineData("about:blank")]
    [InlineData("data:text/html;charset=utf-8,%3Chtml%3E")]
    public void Body_Own_Load_Is_Allowed_When_Not_User_Initiated(string url)
        => Assert.Equal(LinkAction.Allow, LinkRouter.Route(url, LinkOpenMode.System, isUserInitiated: false).Action);

    [Fact]
    public void Blocked_Route_Carries_Scheme_For_Message()
    {
        var route = LinkRouter.Route("javascript:alert(1)", LinkOpenMode.System);
        Assert.Equal("javascript", route.BlockedScheme);
        Assert.Null(route.Uri);
    }

    [Fact]
    public void Mailto_Goes_To_Compose_In_Both_Modes()
    {
        foreach (var mode in new[] { LinkOpenMode.System, LinkOpenMode.Internal })
        {
            var route = LinkRouter.Route("mailto:a@x.com", mode);
            Assert.Equal(LinkAction.Compose, route.Action);
            Assert.Equal("a@x.com", route.Mailto!.To);
        }
    }

    [Fact]
    public void Mailto_Parses_Fields_Per_Rfc6068()
    {
        var m = LinkRouter.ParseMailto("mailto:a@x.com,b@y.com?cc=c@z.com&bcc=d@z.com&subject=Hello%20World%21&body=Line1%0D%0ALine2");
        Assert.Equal("a@x.com, b@y.com", m.To);
        Assert.Equal("c@z.com", m.Cc);
        Assert.Equal("d@z.com", m.Bcc);
        Assert.Equal("Hello World!", m.Subject);
        Assert.Equal("Line1\r\nLine2", m.Body);
    }

    [Fact]
    public void Route_Handles_Mailto_That_System_Uri_Cannot_Parse()
    {
        var route = LinkRouter.Route("mailto:a@x.com,b@y.com?subject=Hi%20there", LinkOpenMode.Internal);
        Assert.Equal(LinkAction.Compose, route.Action);
        Assert.Equal("a@x.com, b@y.com", route.Mailto!.To);
        Assert.Equal("Hi there", route.Mailto.Subject);
        Assert.Equal(LinkAction.Compose, LinkRouter.RouteInternalBrowser("MAILTO:a@x.com,b@y.com").Action);
    }

    [Fact]
    public void Mailto_Plus_Is_Literal_And_Unicode_Decodes()
    {
        var m = LinkRouter.ParseMailto("mailto:me+tag@x.com?subject=1+1%3D2%20%E4%BD%A0%E5%A5%BD");
        Assert.Equal("me+tag@x.com", m.To);
        Assert.Equal("1+1=2 你好", m.Subject);
    }

    [Fact]
    public void Mailto_Strips_Header_Injection()
    {
        var m = LinkRouter.ParseMailto("mailto:a@x.com%0D%0ABcc:evil@x.com?subject=Hi%0D%0ABcc:evil@x.com");
        Assert.DoesNotContain('\r', m.To);
        Assert.DoesNotContain('\n', m.To);
        Assert.DoesNotContain('\r', m.Subject);
        Assert.DoesNotContain('\n', m.Subject);
    }

    [Fact]
    public void Mailto_Without_Address_Or_With_Semicolons()
    {
        var onlySubject = LinkRouter.ParseMailto("mailto:?subject=x&to=a@x.com;b@x.com");
        Assert.Equal("a@x.com, b@x.com", onlySubject.To);
        Assert.Equal("x", onlySubject.Subject);
    }

    [Theory]
    [InlineData("https://a.com", LinkAction.Allow)]
    [InlineData("http://a.com/x", LinkAction.Allow)]
    [InlineData("about:blank", LinkAction.Allow)]
    [InlineData("mailto:a@x.com", LinkAction.Compose)]
    [InlineData("javascript:alert(1)", LinkAction.Block)]
    [InlineData("file:///C:/x", LinkAction.Block)]
    [InlineData("data:text/html,x", LinkAction.Block)]
    [InlineData("about:srcdoc", LinkAction.Block)]
    [InlineData("not a url", LinkAction.Block)]
    [InlineData(null, LinkAction.Block)]
    public void InternalBrowser_Navigation_Rules(string? url, LinkAction expected)
        => Assert.Equal(expected, LinkRouter.RouteInternalBrowser(url).Action);

    [Theory]
    [InlineData(null, LinkOpenMode.System)]
    [InlineData("", LinkOpenMode.System)]
    [InlineData("garbage", LinkOpenMode.System)]
    [InlineData("System", LinkOpenMode.System)]
    [InlineData("Internal", LinkOpenMode.Internal)]
    [InlineData("internal", LinkOpenMode.Internal)]
    public void Mode_Parses_With_System_As_Default(string? value, LinkOpenMode expected)
        => Assert.Equal(expected, LinkRouter.ParseMode(value));

    [Fact]
    public void Mode_Serialization_Roundtrips()
    {
        foreach (var mode in new[] { LinkOpenMode.System, LinkOpenMode.Internal })
        {
            Assert.Equal(mode, LinkRouter.ParseMode(LinkRouter.Serialize(mode)));
        }
    }
}
