using MailClient.Providers.Setup;
using MailClient.ViewModels.AddAccount;

namespace MailClient.Tests;

public sealed class ProviderGuideTests
{
    public static IEnumerable<object[]> Kinds => Enum.GetValues<SetupProviderKind>().Select(k => new object[] { k });

    [Theory]
    [MemberData(nameof(Kinds))]
    public void Every_provider_has_numbered_steps(SetupProviderKind kind)
    {
        var g = ProviderGuide.For(kind);
        Assert.False(string.IsNullOrWhiteSpace(g.Title));
        Assert.False(string.IsNullOrWhiteSpace(g.Intro));
        Assert.True(g.Steps.Count >= 4);
        for (var i = 0; i < g.Steps.Count; i++)
        {
            Assert.Equal((i + 1).ToString(), g.Steps[i].Number);
            Assert.False(string.IsNullOrWhiteSpace(g.Steps[i].Text));
        }
    }

    [Theory]
    [MemberData(nameof(Kinds))]
    public void Guide_text_never_points_at_files_and_links_are_https(SetupProviderKind kind)
    {
        var g = ProviderGuide.For(kind);
        var all = new[] { g.Title, g.Intro }.Concat(g.Steps.Select(s => s.Text)).Concat(g.Notes);
        foreach (var text in all)
        {
            Assert.DoesNotContain("docs\\", text);
            Assert.DoesNotContain(".md", text);
        }

        foreach (var url in g.AllUrls)
        {
            Assert.StartsWith("https://", url);
            Assert.True(Uri.TryCreate(url, UriKind.Absolute, out _), url);
        }

        foreach (var s in g.Steps.Where(s => s.HasLink))
        {
            Assert.False(string.IsNullOrWhiteSpace(s.LinkText));
        }
    }

    [Fact]
    public void Gmail_guide_covers_the_key_steps()
    {
        var g = ProviderGuide.For(SetupProviderKind.Gmail);
        var text = string.Join("\n", g.Steps.Select(s => s.Text));
        Assert.Contains("测试用户", text);
        Assert.Contains("https://mail.google.com/", text);
        Assert.Contains("桌面应用", text);
        Assert.Contains("客户端密钥", text);
        Assert.Contains("未验证", text);
        Assert.Contains(g.AllUrls, u => u.Contains("console.cloud.google.com/projectcreate"));
        Assert.Contains(g.Links, l => l.Url == ProviderGuide.GmailAppPasswordUrl);
    }

    [Fact]
    public void Outlook_guide_lists_the_permissions_the_app_requests()
    {
        var text = string.Join("\n", ProviderGuide.For(SetupProviderKind.Outlook).Steps.Select(s => s.Text));
        foreach (var scope in new[] { "User.Read", "Mail.Read", "Mail.ReadWrite", "Mail.Send", "offline_access" })
        {
            Assert.Contains(scope, text);
        }

        Assert.Contains("http://localhost", text);
        Assert.Contains("个人 Microsoft 账户", text);
    }
    [Fact]
    public void Outlook_guide_lists_broker_redirect_uri()
    {
        var g = ProviderGuide.For(SetupProviderKind.Outlook);
        var blob = string.Join("\n", g.Steps.Select(s => s.Text).Concat(g.Notes));
        Assert.Contains("ms-appx-web://microsoft.aad.brokerplugin", blob);
        Assert.Contains("系统浏览器", blob);
    }


    [Fact]
    public void Qq_and_netease_guides_explain_the_auth_code()
    {
        var qq = ProviderGuide.For(SetupProviderKind.Qq);
        Assert.Contains("授权码", string.Join("\n", qq.Steps.Select(s => s.Text)));
        Assert.Contains("账号与安全", string.Join("\n", qq.Steps.Select(s => s.Text)));
        Assert.Contains("密码", string.Join("\n", qq.Notes));
        var ne = ProviderGuide.For(SetupProviderKind.NetEase);
        Assert.Contains("授权", string.Join("\n", ne.Steps.Select(s => s.Text)));
    }

    [Fact]
    public void Generic_imap_guide_explains_ports_and_security()
    {
        var text = string.Join("\n", ProviderGuide.For(SetupProviderKind.OtherImap).Steps.Select(s => s.Text));
        Assert.Contains("993", text);
        Assert.Contains("465", text);
        Assert.Contains("587", text);
        Assert.Contains("STARTTLS", text);
    }
}