using MailClient.Auth;
using MailClient.Core;
using MailClient.Providers;
using MailClient.Providers.Imap;

namespace MailClient.Tests;

public class GmailProviderTests
{
    private sealed class FixedTokenSource : IOAuthTokenSource
    {
        public Task<OAuthAccessToken> GetAccessTokenAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(new OAuthAccessToken("me@gmail.com", "tok", DateTimeOffset.UtcNow.AddHours(1)));
    }

    private static GmailAccountConfig Config() => new(Guid.NewGuid(), "me@gmail.com", "Me", "cid");

    [Fact]
    public void Provider_Id_And_Servers()
    {
        var config = Config();
        var provider = new GmailProvider(config, new FixedTokenSource());
        Assert.Equal("gmail.oauth", provider.ProviderId);
        Assert.Equal("gmail.oauth", provider.Imap.ProviderId);
        Assert.Equal(config.AccountId, provider.AccountId);

        var imap = GmailProvider.ToImapConfig(config);
        Assert.Equal("imap.gmail.com", imap.Imap.Host);
        Assert.Equal(993, imap.Imap.Port);
        Assert.Equal(MailSecureSocketOptions.SslOnConnect, imap.Imap.SocketOptions);
        Assert.Equal("smtp.gmail.com", imap.Smtp.Host);
        Assert.Equal(465, imap.Smtp.Port);
        Assert.Equal(MailSecureSocketOptions.SslOnConnect, imap.Smtp.SocketOptions);
    }

    [Fact]
    public void Config_Defaults_Scopes_And_VaultKey()
    {
        var config = Config();
        Assert.Contains("https://mail.google.com/", config.EffectiveScopes);
        Assert.Contains("email", config.EffectiveScopes);
        Assert.Equal($"oauth:gmail:{config.AccountId:D}", config.EffectiveVaultKey);
        Assert.Equal("custom", (config with { CredentialVaultKey = "custom" }).EffectiveVaultKey);
    }

    [Fact]
    public void Generic_Provider_Keeps_Default_Id_For_Password_Mode()
    {
        var imap = GmailProvider.ToImapConfig(Config());
        var provider = new GenericImapProvider(imap, new ImapAccountSecrets("a", "b"));
        Assert.Equal("imap.generic", provider.ProviderId);
    }

    [Theory]
    [InlineData("primary", MailCategory.Primary)]
    [InlineData("purchases", MailCategory.Transactions)]
    [InlineData("updates", MailCategory.Updates)]
    [InlineData("social", MailCategory.Updates)]
    [InlineData("forums", MailCategory.Updates)]
    [InlineData("promotions", MailCategory.Promotions)]
    public void GmailCategory_Maps_To_App_Categories(string gmail, MailCategory expected)
        => Assert.Equal(expected, GmailCategoryMapper.FromGmailCategory(gmail));

    [Fact]
    public void GmailCategory_Unknown_Is_Null()
        => Assert.Null(GmailCategoryMapper.FromGmailCategory("whatever"));

    [Fact]
    public void Resolve_Prefers_Promotions_Then_Purchases_Then_Updates_And_Defaults_To_Primary()
    {
        var map = new Dictionary<string, HashSet<uint>>(StringComparer.OrdinalIgnoreCase)
        {
            ["promotions"] = [1, 5],
            ["purchases"] = [2, 5],
            ["updates"] = [3],
            ["social"] = [4],
            ["forums"] = []
        };
        Assert.Equal(MailCategory.Promotions, GmailCategoryMapper.Resolve(1, map));
        Assert.Equal(MailCategory.Transactions, GmailCategoryMapper.Resolve(2, map));
        Assert.Equal(MailCategory.Updates, GmailCategoryMapper.Resolve(3, map));
        Assert.Equal(MailCategory.Updates, GmailCategoryMapper.Resolve(4, map));
        Assert.Equal(MailCategory.Promotions, GmailCategoryMapper.Resolve(5, map));
        Assert.Equal(MailCategory.Primary, GmailCategoryMapper.Resolve(99, map));
    }
}
