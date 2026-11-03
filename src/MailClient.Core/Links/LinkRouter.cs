namespace MailClient.Core.Links;

/// <summary>点邮件里的链接时用哪个浏览器打开。</summary>
public enum LinkOpenMode
{
    /// <summary>系统默认浏览器（默认）。</summary>
    System,

    /// <summary>软件内置浏览器（应用内的 WebView2 窗口）。</summary>
    Internal
}

public enum LinkAction
{
    /// <summary>不是用户点的链接（例如正文自己的加载），放行。</summary>
    Allow,

    /// <summary>取消导航，交给系统默认浏览器。</summary>
    OpenSystem,

    /// <summary>取消导航，在软件内置浏览器窗口里打开。</summary>
    OpenInternal,

    /// <summary>mailto:：取消导航，打开写信窗口。</summary>
    Compose,

    /// <summary>不安全 / 不支持的协议（javascript: file: data: …）：取消导航，什么都不打开。</summary>
    Block
}

public sealed record LinkRoute(LinkAction Action, Uri? Uri = null, MailtoData? Mailto = null, string? BlockedScheme = null);

/// <summary>mailto: 里带的预填内容。</summary>
public sealed record MailtoData(string To, string Cc, string Bcc, string Subject, string Body);

/// <summary>
/// 阅读窗格里的链接路由（纯函数）：
/// http / https → 按设置走系统浏览器或内置浏览器；mailto → 写信；其余协议（javascript: file: data: vbscript: ftp: ms-… 等）一律拦截。
/// </summary>
public static class LinkRouter
{
    public static LinkRoute Route(string? url, LinkOpenMode mode, bool isUserInitiated = true)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return new LinkRoute(isUserInitiated ? LinkAction.Block : LinkAction.Allow);
        }

        // mailto: 先于 Uri 解析处理：System.Uri 对 mailto:a@x.com,b@y.com 这类多收件人 / 带编码的地址会解析失败
        if (IsMailto(url))
        {
            var text = url.Trim();
            Uri.TryCreate(text, UriKind.Absolute, out var mailUri);
            return new LinkRoute(LinkAction.Compose, mailUri, ParseMailto(text));
        }

        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri))
        {
            // 相对链接 / 锚点 / 无法解析：用户点的就拦掉，否则放行
            return new LinkRoute(isUserInitiated ? LinkAction.Block : LinkAction.Allow);
        }

        var scheme = uri.Scheme.ToLowerInvariant();
        switch (scheme)
        {
            case "http":
            case "https":
                return new LinkRoute(mode == LinkOpenMode.Internal ? LinkAction.OpenInternal : LinkAction.OpenSystem, uri);
            case "about":
            case "data":
                // 正文自身的加载（NavigateToString → data:/about:blank）：非用户点击时放行；用户点的 data: 链接拦截
                return new LinkRoute(isUserInitiated ? LinkAction.Block : LinkAction.Allow, null, null, isUserInitiated ? scheme : null);
            default:
                return new LinkRoute(LinkAction.Block, null, null, scheme);
        }
    }

    public static LinkOpenMode ParseMode(string? value)
        => string.Equals(value, nameof(LinkOpenMode.Internal), StringComparison.OrdinalIgnoreCase)
            ? LinkOpenMode.Internal
            : LinkOpenMode.System;

    public static string Serialize(LinkOpenMode mode) => mode.ToString();

    /// <summary>应用内浏览器自己页面里的导航：只允许 http/https；mailto 走写信；其他拦截。</summary>
    public static LinkRoute RouteInternalBrowser(string? url)
    {
        if (IsMailto(url))
        {
            var text = url!.Trim();
            Uri.TryCreate(text, UriKind.Absolute, out var mailUri);
            return new LinkRoute(LinkAction.Compose, mailUri, ParseMailto(text));
        }

        if (!Uri.TryCreate(url?.Trim(), UriKind.Absolute, out var uri))
        {
            return new LinkRoute(LinkAction.Block);
        }

        return uri.Scheme.ToLowerInvariant() switch
        {
            "http" or "https" => new LinkRoute(LinkAction.Allow, uri),
            "about" when string.Equals(uri.AbsoluteUri, "about:blank", StringComparison.OrdinalIgnoreCase)
                => new LinkRoute(LinkAction.Allow, uri),
            var other => new LinkRoute(LinkAction.Block, null, null, other)
        };
    }

    /// <summary>解析 mailto:a@x.com,b@y.com?cc=…&amp;bcc=…&amp;subject=…&amp;body=…（RFC 6068）。</summary>
    public static MailtoData ParseMailto(Uri uri) => ParseMailto(uri.OriginalString);

    private static bool IsMailto(string? url)
        => url is not null && url.AsSpan().TrimStart().StartsWith("mailto:", StringComparison.OrdinalIgnoreCase);

    /// <summary>同上，直接从原始字符串解析（不经过 System.Uri）。</summary>
    public static MailtoData ParseMailto(string raw)
    {
        raw = raw.Trim();
        var afterScheme = raw.Length > 7 ? raw[7..] : string.Empty; // 去掉 "mailto:"
        var query = string.Empty;
        var q = afterScheme.IndexOf('?', StringComparison.Ordinal);
        if (q >= 0)
        {
            query = afterScheme[(q + 1)..];
            afterScheme = afterScheme[..q];
        }

        var to = new List<string>();
        var cc = new List<string>();
        var bcc = new List<string>();
        var subject = string.Empty;
        var body = string.Empty;

        AddAddresses(to, Decode(afterScheme));
        foreach (var pair in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = pair.IndexOf('=', StringComparison.Ordinal);
            var key = (eq < 0 ? pair : pair[..eq]).Trim().ToLowerInvariant();
            var value = eq < 0 ? string.Empty : Decode(pair[(eq + 1)..]);
            switch (key)
            {
                case "to":
                    AddAddresses(to, value);
                    break;
                case "cc":
                    AddAddresses(cc, value);
                    break;
                case "bcc":
                    AddAddresses(bcc, value);
                    break;
                case "subject":
                    subject = value.Replace("\r", " ", StringComparison.Ordinal).Replace("\n", " ", StringComparison.Ordinal);
                    break;
                case "body":
                    body = value;
                    break;
            }
        }

        return new MailtoData(string.Join(", ", to), string.Join(", ", cc), string.Join(", ", bcc), subject, body);
    }

    private static string Decode(string value)
    {
        try
        {
            // mailto 里的 + 是加号，不是空格（RFC 6068），所以用 UnescapeDataString
            return Uri.UnescapeDataString(value);
        }
        catch (UriFormatException)
        {
            return value;
        }
    }

    private static void AddAddresses(List<string> target, string value)
    {
        foreach (var part in value.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            // 防止 CR/LF 头注入
            var clean = part.Replace("\r", string.Empty, StringComparison.Ordinal).Replace("\n", string.Empty, StringComparison.Ordinal);
            if (clean.Length > 0)
            {
                target.Add(clean);
            }
        }
    }
}
