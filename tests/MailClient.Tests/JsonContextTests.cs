using System.Text.Json;
using MailClient.Auth;
using MailClient.Core;
using MailClient.Providers.Setup;

namespace MailClient.Tests;

public sealed class JsonContextTests
{
    [Fact]
    public void OutgoingMailMessage_roundtrips_via_source_gen()
    {
        var msg = new OutgoingMailMessage(
            AccountId: Guid.NewGuid().ToString("D"),
            To: new[] { "a@example.com" },
            Cc: Array.Empty<string>(),
            Bcc: Array.Empty<string>(),
            Subject: "主题",
            BodyHtml: "<p>hi</p>",
            AttachmentPaths: new[] { @"C:\tmp\a.txt" },
            BodyText: "hi");
        var json = JsonSerializer.Serialize(msg, MailClientJsonContext.Default.OutgoingMailMessage);
        var back = JsonSerializer.Deserialize(json, MailClientJsonContext.Default.OutgoingMailMessage);
        Assert.NotNull(back);
        Assert.Equal(msg.Subject, back!.Subject);
        Assert.Equal(msg.To[0], back.To[0]);
        Assert.Equal(msg.AttachmentPaths[0], back.AttachmentPaths[0]);
    }

    [Fact]
    public void GmailTokenSet_roundtrips_via_source_gen()
    {
        var set = new GmailTokenSet("access", "refresh", DateTimeOffset.UtcNow.AddHours(1), "u@gmail.com", "https://mail.google.com/");
        var json = JsonSerializer.Serialize(set, GmailAuthJsonContext.Default.GmailTokenSet);
        var back = JsonSerializer.Deserialize(json, GmailAuthJsonContext.Default.GmailTokenSet);
        Assert.NotNull(back);
        Assert.Equal(set.AccessToken, back!.AccessToken);
        Assert.Equal(set.RefreshToken, back.RefreshToken);
        Assert.Equal(set.Email, back.Email);
    }

    [Fact]
    public void AccountConnection_roundtrips_string_enums()
    {
        var conn = new AccountConnection(
            SetupProviderKind.Gmail,
            "u@gmail.com",
            new ImapServerSettings("imap.gmail.com", 993, MailSecureSocketOptions.SslOnConnect),
            new SmtpServerSettings("smtp.gmail.com", 465, MailSecureSocketOptions.SslOnConnect));
        var json = JsonSerializer.Serialize(conn, AccountConnectionJsonContext.Default.AccountConnection);
        Assert.Contains("Gmail", json, StringComparison.Ordinal);
        Assert.Contains("SslOnConnect", json, StringComparison.Ordinal);
        var back = JsonSerializer.Deserialize(json, AccountConnectionJsonContext.Default.AccountConnection);
        Assert.NotNull(back);
        Assert.Equal(SetupProviderKind.Gmail, back!.Kind);
        Assert.Equal(993, back.Imap!.Port);
        Assert.Equal(MailSecureSocketOptions.SslOnConnect, back.Imap.SocketOptions);
    }

    [Fact]
    public void DictionaryStringString_roundtrips()
    {
        var map = new Dictionary<string, string> { ["oauth:gmail:x"] = "enc" };
        var json = JsonSerializer.Serialize(map, MailClientJsonContext.Default.DictionaryStringString);
        var back = JsonSerializer.Deserialize(json, MailClientJsonContext.Default.DictionaryStringString);
        Assert.NotNull(back);
        Assert.Equal("enc", back!["oauth:gmail:x"]);
    }
}
