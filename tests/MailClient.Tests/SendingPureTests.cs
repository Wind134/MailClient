using System.Net.Sockets;
using MailClient.Core;
using MailClient.Core.Sending;
using MailClient.Providers.Outbox;
using MailClient.Providers.Setup;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Net.Smtp;
using Microsoft.Kiota.Abstractions;
using MimeKit;

namespace MailClient.Tests;

public class RetryPolicyTests
{
    [Theory]
    [InlineData(1, 30)]
    [InlineData(2, 120)]
    [InlineData(3, 480)]
    [InlineData(4, 1920)]
    [InlineData(5, 3600)] // 封顶 1 小时
    [InlineData(12, 3600)]
    public void Delay_Grows_Exponentially_And_Is_Capped(int attempts, int seconds)
        => Assert.Equal(TimeSpan.FromSeconds(seconds), RetryPolicy.Delay(attempts));

    [Fact]
    public void Delay_Treats_Zero_As_First_Attempt() => Assert.Equal(TimeSpan.FromSeconds(30), RetryPolicy.Delay(0));

    [Fact]
    public void Max_Attempts_Is_Within_The_Requested_Range() => Assert.InRange(RetryPolicy.MaxAttempts, 3, 5);
}

public class SendFailureClassifierTests
{
    public static IEnumerable<object[]> Cases()
    {
        yield return [new MailKit.Security.AuthenticationException("bad"), FailureKind.Auth];
        yield return [new MailKit.Security.SaslException("PLAIN", MailKit.Security.SaslErrorCode.MissingChallenge, "x"), FailureKind.Auth];
        yield return [new SmtpCommandException(SmtpErrorCode.UnexpectedStatusCode, SmtpStatusCode.AuthenticationRequired, "auth"), FailureKind.Auth];
        yield return [new SmtpCommandException(SmtpErrorCode.RecipientNotAccepted, SmtpStatusCode.MailboxUnavailable, MailboxAddress.Parse("a@b.com"), "unknown"), FailureKind.RecipientRejected];
        yield return [new SmtpCommandException(SmtpErrorCode.RecipientNotAccepted, SmtpStatusCode.MailboxBusy, MailboxAddress.Parse("a@b.com"), "busy"), FailureKind.Transient];
        yield return [new SmtpCommandException(SmtpErrorCode.SenderNotAccepted, SmtpStatusCode.MailboxUnavailable, MailboxAddress.Parse("a@b.com"), "no"), FailureKind.MessageRejected];
        yield return [new SmtpCommandException(SmtpErrorCode.MessageNotAccepted, SmtpStatusCode.ExceededStorageAllocation, "size"), FailureKind.MessageRejected];
        yield return [new SmtpCommandException(SmtpErrorCode.UnexpectedStatusCode, SmtpStatusCode.ServiceNotAvailable, "later"), FailureKind.Transient];
        yield return [new SmtpProtocolException("eof"), FailureKind.Transient];
        yield return [new TimeoutException(), FailureKind.Transient];
        yield return [new SocketException(), FailureKind.Transient];
        yield return [new HttpRequestException("dns"), FailureKind.Transient];
        yield return [new IOException("reset"), FailureKind.Transient];
        yield return [new ServiceNotConnectedException(), FailureKind.Transient];
        yield return [new FileNotFoundException("gone", "C:\\x\\a.pdf"), FailureKind.MessageRejected];
        yield return [new ClassifiedFailureException(FailureKind.Configuration, "没有 ClientId"), FailureKind.Configuration];
        yield return [new ApiException { ResponseStatusCode = 401 }, FailureKind.Auth];
        yield return [new ApiException { ResponseStatusCode = 404 }, FailureKind.NotFound];
        yield return [new ApiException { ResponseStatusCode = 400 }, FailureKind.MessageRejected];
        yield return [new ApiException { ResponseStatusCode = 429 }, FailureKind.Transient];
        yield return [new ApiException { ResponseStatusCode = 503 }, FailureKind.Transient];
        yield return [new InvalidOperationException("找不到账号的登录凭据"), FailureKind.Configuration];
        yield return [new InvalidOperationException("some odd bug"), FailureKind.Transient];
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void Classifies_Exceptions(Exception error, FailureKind expected)
        => Assert.Equal(expected, SendFailureClassifier.Classify(error).Kind);

    [Fact]
    public void Only_Transient_Is_Retryable()
    {
        Assert.False(new FailureInfo(FailureKind.Transient, "x").IsPermanent);
        foreach (var kind in Enum.GetValues<FailureKind>().Where(k => k != FailureKind.Transient))
        {
            Assert.True(new FailureInfo(kind, "x").IsPermanent, kind.ToString());
        }
    }

    [Fact]
    public void Unwraps_Single_Inner_Aggregate()
        => Assert.Equal(FailureKind.Auth, SendFailureClassifier.Classify(new AggregateException(new MailKit.Security.AuthenticationException("x"))).Kind);

    [Fact]
    public void Messages_Are_Chinese_And_Never_Empty()
    {
        var info = SendFailureClassifier.Classify(new MailKit.Security.AuthenticationException("x"));
        Assert.Contains("登录", info.Message);
    }
}

public class SentCopyPolicyTests
{
    [Theory]
    [InlineData("imap.generic", SetupProviderKind.Qq, true)]
    [InlineData("imap.generic", SetupProviderKind.NetEase, true)]
    [InlineData("imap.generic", SetupProviderKind.OtherImap, true)]
    [InlineData("gmail.oauth", SetupProviderKind.Gmail, false)]
    [InlineData("microsoft.graph", SetupProviderKind.Outlook, false)]
    public void Default_Per_Provider(string providerId, SetupProviderKind kind, bool expected)
        => Assert.Equal(expected, SentCopyPolicy.DefaultFor(providerId, kind));

    [Fact]
    public void User_Can_Turn_Append_Off_For_A_Preset()
        => Assert.False(SentCopyPolicy.ShouldAppend("imap.generic", SetupProviderKind.Qq, false));

    [Fact]
    public void User_Can_Turn_Append_On_For_An_Unknown_Imap_Server()
        => Assert.True(SentCopyPolicy.ShouldAppend("imap.generic", SetupProviderKind.OtherImap, true));

    [Fact]
    public void Null_Override_Uses_Default()
    {
        Assert.True(SentCopyPolicy.ShouldAppend("imap.generic", SetupProviderKind.NetEase, null));
    }

    [Theory]
    [InlineData("gmail.oauth", SetupProviderKind.Gmail)]
    [InlineData("microsoft.graph", SetupProviderKind.Outlook)]
    public void Gmail_And_Graph_Never_Append_Even_If_Overridden(string providerId, SetupProviderKind kind)
        => Assert.False(SentCopyPolicy.ShouldAppend(providerId, kind, true));
}

public class AttachmentLimitsTests
{
    [Fact]
    public void Graph_Is_Limited_To_Inline_Size() => Assert.Equal(3L * 1024 * 1024, AttachmentLimits.For("microsoft.graph"));

    [Fact]
    public void Gmail_Is_About_Eighteen_Megabytes() => Assert.Equal(18L * 1024 * 1024, AttachmentLimits.For("gmail.oauth"));

    [Theory]
    [InlineData("a@qq.com")]
    [InlineData("a@163.com")]
    [InlineData("a@126.com")]
    public void Chinese_Providers_Allow_More(string address)
        => Assert.Equal(30L * 1024 * 1024, AttachmentLimits.For("imap.generic", address));

    [Fact]
    public void Unknown_Imap_Uses_Conservative_Default() => Assert.Equal(18L * 1024 * 1024, AttachmentLimits.For("imap.generic", "me@corp.example"));
}

public class OutgoingMimeBuilderTests
{
    private static readonly DateTimeOffset Date = new(2026, 10, 31, 12, 0, 0, TimeSpan.FromHours(8));

    private static OutgoingMailMessage Msg(
        string[]? to = null, string[]? cc = null, string[]? bcc = null, string html = "<p>你好</p>", string? text = null,
        string? inReplyTo = null, string[]? refs = null, string[]? attachments = null, string? id = null)
        => new("x", to ?? ["bob@example.com"], cc ?? [], bcc ?? [], "测试主题", html, attachments ?? [],
            BodyText: text, MessageId: id, InReplyTo: inReplyTo, References: refs);

    [Fact]
    public void Sets_From_With_Display_Name_And_Recipients()
    {
        var mime = OutgoingMimeBuilder.Build(Msg(to: ["Bob <bob@example.com>"], cc: ["c@example.com"], bcc: ["d@example.com"]), "me@example.com", "李同学", Date);

        Assert.Equal("李同学", ((MailboxAddress)mime.From[0]).Name);
        Assert.Equal("me@example.com", ((MailboxAddress)mime.From[0]).Address);
        Assert.Equal("bob@example.com", ((MailboxAddress)mime.To[0]).Address);
        Assert.Single(mime.Cc);
        Assert.Single(mime.Bcc);
        Assert.Equal("测试主题", mime.Subject);
        Assert.Equal(Date, mime.Date);
    }

    [Fact]
    public void Uses_Stored_Message_Id_Without_Brackets()
    {
        var mime = OutgoingMimeBuilder.Build(Msg(id: "<abc123@example.com>"), "me@example.com", null, Date);
        Assert.Equal("abc123@example.com", mime.MessageId);
    }

    [Fact]
    public void Generates_Message_Id_From_Sender_Domain_When_Missing()
    {
        var mime = OutgoingMimeBuilder.Build(Msg(), "me@example.com", null, Date);
        Assert.EndsWith("@example.com", mime.MessageId);
    }

    [Fact]
    public void Builds_Alternative_With_Plain_And_Html()
    {
        var mime = OutgoingMimeBuilder.Build(Msg(html: "<p>第一行<br>第二行</p>"), "me@example.com", null, Date);

        Assert.Contains("第一行", mime.TextBody);
        Assert.Contains("第二行", mime.TextBody);
        Assert.DoesNotContain("<p>", mime.TextBody);
        Assert.Contains("<br>", mime.HtmlBody);
        Assert.IsType<MultipartAlternative>(mime.Body);
    }

    [Fact]
    public void Explicit_Plain_Text_Wins_Over_Derived_Text()
    {
        var mime = OutgoingMimeBuilder.Build(Msg(text: "纯文本版本"), "me@example.com", null, Date);
        Assert.Equal("纯文本版本", mime.TextBody!.Trim());
    }

    [Fact]
    public void Reply_Sets_In_Reply_To_And_References_Chain()
    {
        var mime = OutgoingMimeBuilder.Build(
            Msg(inReplyTo: "<parent@remote.example>", refs: ["<root@remote.example>", "parent@remote.example"]),
            "me@example.com", null, Date);

        Assert.Equal("parent@remote.example", mime.InReplyTo);
        Assert.Equal(["root@remote.example", "parent@remote.example"], mime.References.ToArray());
        Assert.Contains("In-Reply-To: <parent@remote.example>", mime.ToString());
    }

    [Fact]
    public void Attachments_Are_Added_As_Mixed_Parts()
    {
        var file = Path.Combine(Path.GetTempPath(), $"att-{Guid.NewGuid():N}.txt");
        File.WriteAllText(file, "附件内容");
        try
        {
            var mime = OutgoingMimeBuilder.Build(Msg(attachments: [file]), "me@example.com", null, Date);
            var attachment = Assert.Single(mime.Attachments);
            Assert.Equal(Path.GetFileName(file), ((MimePart)attachment).FileName);
            Assert.IsType<Multipart>(mime.Body);
            Assert.Equal("mixed", ((Multipart)mime.Body).ContentType.MediaSubtype);
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public void Missing_Attachment_Is_An_Error_Not_Silently_Dropped()
    {
        var ex = Assert.Throws<ClassifiedFailureException>(() =>
            OutgoingMimeBuilder.Build(Msg(attachments: [Path.Combine(Path.GetTempPath(), "nope-" + Guid.NewGuid().ToString("N"))]), "me@example.com", null, Date));
        Assert.Equal(FailureKind.MessageRejected, ex.Kind);
    }

    [Fact]
    public void Invalid_Recipient_Is_Rejected_Permanently()
    {
        var ex = Assert.Throws<ClassifiedFailureException>(() => OutgoingMimeBuilder.Build(Msg(to: ["not-an-address"]), "me@example.com", null, Date));
        Assert.Equal(FailureKind.RecipientRejected, ex.Kind);
    }

    [Fact]
    public void No_Recipients_Is_Rejected()
    {
        var ex = Assert.Throws<ClassifiedFailureException>(() => OutgoingMimeBuilder.Build(Msg(to: []), "me@example.com", null, Date));
        Assert.Equal(FailureKind.RecipientRejected, ex.Kind);
    }

    [Fact]
    public void Bcc_Only_Message_Is_Valid()
        => Assert.Single(OutgoingMimeBuilder.Build(Msg(to: [], bcc: ["x@example.com"]), "me@example.com", null, Date).Bcc);

    [Theory]
    [InlineData("a@b.com", null)]
    [InlineData("a@b.com; ", null)]
    public void FindInvalidAddress_Accepts_Valid(string a, string? expected)
        => Assert.Equal(expected, OutgoingMimeBuilder.FindInvalidAddress(a.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)));

    [Fact]
    public void FindInvalidAddress_Returns_First_Bad_One()
        => Assert.Equal("bad@", OutgoingMimeBuilder.FindInvalidAddress(["ok@b.com", "bad@", "worse"]));

    [Fact]
    public void HtmlToText_Strips_Tags_And_Keeps_Line_Breaks()
        => Assert.Equal("a\nb & c", OutgoingMimeBuilder.HtmlToText("<div>a</div><p>b &amp; c</p><style>x{}</style>"));

    [Fact]
    public void QuoteText_Prefixes_Each_Line()
        => Assert.Equal("> 一\n> 二", OutgoingMimeBuilder.QuoteText("一\r\n二"));
}
