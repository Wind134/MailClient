using System.Xml;
using System.Xml.Linq;
using MailClient.Core;

namespace MailClient.Providers.Setup;

/// <summary>从 Mozilla ISPDB / autoconfig XML 解析出的服务器设置。</summary>
public sealed record AutoconfigResult(
    string? DisplayName,
    ImapServerSettings Imap,
    SmtpServerSettings Smtp,
    string UserName);

/// <summary>
/// Mozilla ISPDB（Thunderbird autoconfig）解析与查询。
/// 解析是纯函数（<see cref="Parse"/>，带 XXE 防护），查询（<see cref="LookupAsync"/>）为 best-effort：
/// 任何失败（离线 / 404 / 格式异常）都返回 null。
/// </summary>
public static class IspdbAutoconfig
{
    public const string BaseUrl = "https://autoconfig.thunderbird.net/v1.1/";

    public static string? BuildUrl(string? domain)
    {
        if (string.IsNullOrWhiteSpace(domain))
        {
            return null;
        }

        var d = domain.Trim().ToLowerInvariant();
        return d.Length <= 253 && d.Contains('.') && d.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-')
            ? BaseUrl + d
            : null;
    }

    public static AutoconfigResult? Parse(string? xml, string email)
    {
        if (string.IsNullOrWhiteSpace(xml))
        {
            return null;
        }

        XDocument doc;
        try
        {
            using var reader = XmlReader.Create(
                new StringReader(xml),
                new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
            doc = XDocument.Load(reader);
        }
        catch (XmlException)
        {
            return null;
        }

        var provider = doc.Root?.Element("emailProvider");
        if (provider is null)
        {
            return null;
        }

        var domain = ProviderDetector.ExtractDomain(email) ?? string.Empty;
        var local = ProviderDetector.ExtractLocalPart(email) ?? string.Empty;
        var address = email.Trim();

        string Fill(string? text) => (text ?? string.Empty).Trim()
            .Replace("%EMAILADDRESS%", address, StringComparison.OrdinalIgnoreCase)
            .Replace("%EMAILLOCALPART%", local, StringComparison.OrdinalIgnoreCase)
            .Replace("%EMAILDOMAIN%", domain, StringComparison.OrdinalIgnoreCase);

        var imapNode = Pick(provider.Elements("incomingServer").Where(e => Type(e) == "imap"));
        var smtpNode = Pick(provider.Elements("outgoingServer").Where(e => Type(e) == "smtp"));
        if (imapNode is null || smtpNode is null)
        {
            return null;
        }

        var imapHost = Fill(imapNode.Element("hostname")?.Value);
        var smtpHost = Fill(smtpNode.Element("hostname")?.Value);
        if (imapHost.Length == 0 || smtpHost.Length == 0)
        {
            return null;
        }

        var imapSocket = ToSocket(imapNode.Element("socketType")?.Value, imapDefault: true);
        var smtpSocket = ToSocket(smtpNode.Element("socketType")?.Value, imapDefault: false);
        var imapPort = ParsePort(imapNode.Element("port")?.Value) ?? DefaultPort(imapSocket, imap: true);
        var smtpPort = ParsePort(smtpNode.Element("port")?.Value) ?? DefaultPort(smtpSocket, imap: false);

        var userName = Fill(imapNode.Element("username")?.Value);
        if (userName.Length == 0)
        {
            userName = address;
        }

        var displayName = provider.Element("displayName")?.Value?.Trim();
        return new AutoconfigResult(
            string.IsNullOrEmpty(displayName) ? null : displayName,
            new ImapServerSettings(imapHost, imapPort, imapSocket),
            new SmtpServerSettings(smtpHost, smtpPort, smtpSocket),
            userName);
    }

    public static async Task<AutoconfigResult?> LookupAsync(
        HttpClient http,
        string email,
        CancellationToken cancellationToken = default,
        TimeSpan? timeout = null)
    {
        var url = BuildUrl(ProviderDetector.ExtractDomain(email));
        if (url is null)
        {
            return null;
        }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(timeout ?? TimeSpan.FromSeconds(8));
        try
        {
            using var response = await http.GetAsync(url, cts.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            var xml = await response.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);
            return Parse(xml, email);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException or IOException)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return null;
        }
    }

    private static string? Type(XElement e) => (string?)e.Attribute("type");

    /// <summary>同一类型有多个 server 时：SSL 优先，其次 STARTTLS，最后明文。</summary>
    private static XElement? Pick(IEnumerable<XElement> candidates)
        => candidates
            .OrderBy(e => Rank(e.Element("socketType")?.Value))
            .FirstOrDefault();

    private static int Rank(string? socket) => socket?.Trim().ToUpperInvariant() switch
    {
        "SSL" => 0,
        "STARTTLS" => 1,
        _ => 2
    };

    private static MailSecureSocketOptions ToSocket(string? value, bool imapDefault) => value?.Trim().ToUpperInvariant() switch
    {
        "SSL" => MailSecureSocketOptions.SslOnConnect,
        "STARTTLS" => MailSecureSocketOptions.StartTls,
        "PLAIN" => MailSecureSocketOptions.None,
        _ => imapDefault ? MailSecureSocketOptions.SslOnConnect : MailSecureSocketOptions.StartTls
    };

    private static int? ParsePort(string? value)
        => int.TryParse(value?.Trim(), out var p) && p is > 0 and <= 65535 ? p : null;

    private static int DefaultPort(MailSecureSocketOptions socket, bool imap) => (socket, imap) switch
    {
        (MailSecureSocketOptions.SslOnConnect, true) => 993,
        (MailSecureSocketOptions.SslOnConnect, false) => 465,
        (MailSecureSocketOptions.StartTls, true) => 143,
        (MailSecureSocketOptions.StartTls, false) => 587,
        (_, true) => 143,
        _ => 25
    };
}
