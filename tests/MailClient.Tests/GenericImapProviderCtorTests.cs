using MailClient.Auth;
using MailClient.Core;
using MailClient.Providers;

namespace MailClient.Tests;

public class GenericImapProviderCtorTests
{
    private static GenericImapAccountConfig SampleConfig()
        => new(
            AccountId: Guid.NewGuid(),
            EmailAddress: "user@example.com",
            DisplayName: "User",
            Imap: new ImapServerSettings("imap.example.com", 993, MailSecureSocketOptions.SslOnConnect),
            Smtp: new SmtpServerSettings("smtp.example.com", 465, MailSecureSocketOptions.SslOnConnect),
            CredentialVaultKey: "vault:test");

    [Fact]
    public void ProviderId_IsGeneric()
    {
        var provider = new GenericImapProvider(
            SampleConfig(),
            new ImapAccountSecrets("user@example.com", "secret"));
        Assert.Equal("imap.generic", provider.ProviderId);
    }

    [Fact]
    public async Task CredentialStore_ResolvesUsernamePasswordLine()
    {
        var config = SampleConfig();
        var store = new InMemoryCredentialStore();
        await store.SaveSecretAsync(config.CredentialVaultKey!, "alice@qq.com\napp-password-here");
        var provider = new GenericImapProvider(config, store);
        Assert.Equal(config.AccountId, provider.AccountId);
        // 不连真实服务器；仅验证可构造
        await provider.DisconnectAsync();
    }

    [Fact]
    public void RequiresSecretsOrStore()
    {
        Assert.Throws<ArgumentException>(() =>
            new GenericImapProvider(SampleConfig(), credentialStore: null, explicitSecrets: null));
    }
}
