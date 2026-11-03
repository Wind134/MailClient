using System.Net;
using System.Net.Sockets;
using System.Text;

namespace MailClient.Auth;

/// <summary>浏览器重定向回本机后解析出的参数。</summary>
public sealed record AuthorizationResponse(string? Code, string? State, string? Error, string? ErrorDescription);

/// <summary>
/// 回环重定向监听（http://127.0.0.1:随机端口/）：Google「桌面应用」客户端允许任意回环端口。
/// 用 TcpListener 手写最小 HTTP，直接绑定端口 0，无需 URL ACL / 管理员权限，也没有“先探测再绑定”的竞态。
/// </summary>
public sealed class LoopbackAuthorizationListener : IDisposable
{
    private readonly TcpListener _listener;

    private LoopbackAuthorizationListener(TcpListener listener, int port)
    {
        _listener = listener;
        Port = port;
        RedirectUri = $"http://127.0.0.1:{port}/";
    }

    public int Port { get; }
    public string RedirectUri { get; }

    public static LoopbackAuthorizationListener Start()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return new LoopbackAuthorizationListener(listener, ((IPEndPoint)listener.LocalEndpoint).Port);
    }

    /// <summary>等待带 state 的回调；无关请求（如 favicon）返回 404 后继续等待。</summary>
    public async Task<AuthorizationResponse> WaitForResponseAsync(
        string expectedState,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(timeout);
        try
        {
            while (true)
            {
                using var client = await _listener.AcceptTcpClientAsync(cts.Token).ConfigureAwait(false);
                var response = await HandleAsync(client, expectedState, cts.Token).ConfigureAwait(false);
                if (response is not null)
                {
                    return response;
                }
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("等待浏览器完成 Google 登录超时。");
        }
    }

    private static async Task<AuthorizationResponse?> HandleAsync(TcpClient client, string expectedState, CancellationToken ct)
    {
        var stream = client.GetStream();
        var head = await ReadHeadAsync(stream, ct).ConfigureAwait(false);
        var parsed = ParseRequestLine(head);
        if (parsed is null)
        {
            await WriteAsync(stream, 400, "Bad Request", Page("请求无效", "无法解析该请求。"), ct).ConfigureAwait(false);
            return null;
        }

        var query = ParseQuery(parsed);
        var hasResult = query.ContainsKey("code") || query.ContainsKey("error");
        if (!hasResult)
        {
            await WriteAsync(stream, 404, "Not Found", Page("未找到", string.Empty), ct).ConfigureAwait(false);
            return null;
        }

        query.TryGetValue("state", out var state);
        if (!string.Equals(state, expectedState, StringComparison.Ordinal))
        {
            await WriteAsync(stream, 400, "Bad Request", Page("登录失败", "state 校验未通过，已忽略该请求。"), ct).ConfigureAwait(false);
            return null;
        }

        query.TryGetValue("code", out var code);
        query.TryGetValue("error", out var error);
        query.TryGetValue("error_description", out var description);
        var ok = !string.IsNullOrEmpty(code) && string.IsNullOrEmpty(error);
        await WriteAsync(
                stream,
                200,
                "OK",
                ok ? Page("登录成功", "已完成 Google 授权，可以关闭此页面并回到邮件客户端。")
                   : Page("登录未完成", "授权被取消或失败，请回到邮件客户端重试。"),
                ct)
            .ConfigureAwait(false);
        return new AuthorizationResponse(code, state, error, description);
    }

    /// <summary>解析 "GET /path?query HTTP/1.1" 得到 path+query；非 GET 返回 null。</summary>
    public static string? ParseRequestLine(string head)
    {
        var lineEnd = head.IndexOf("\r\n", StringComparison.Ordinal);
        var line = lineEnd >= 0 ? head[..lineEnd] : head;
        var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length >= 2 && parts[0].Equals("GET", StringComparison.OrdinalIgnoreCase) ? parts[1] : null;
    }

    public static Dictionary<string, string> ParseQuery(string pathAndQuery)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        var q = pathAndQuery.IndexOf('?');
        if (q < 0 || q == pathAndQuery.Length - 1)
        {
            return result;
        }

        foreach (var pair in pathAndQuery[(q + 1)..].Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = pair.IndexOf('=');
            var key = Uri.UnescapeDataString((eq < 0 ? pair : pair[..eq]).Replace('+', ' '));
            var value = eq < 0 ? string.Empty : Uri.UnescapeDataString(pair[(eq + 1)..].Replace('+', ' '));
            result[key] = value;
        }

        return result;
    }

    private static async Task<string> ReadHeadAsync(NetworkStream stream, CancellationToken ct)
    {
        var buffer = new byte[4096];
        var sb = new StringBuilder();
        while (sb.Length < 16 * 1024)
        {
            var read = await stream.ReadAsync(buffer, ct).ConfigureAwait(false);
            if (read <= 0)
            {
                break;
            }

            sb.Append(Encoding.ASCII.GetString(buffer, 0, read));
            if (sb.ToString().Contains("\r\n\r\n", StringComparison.Ordinal))
            {
                break;
            }
        }

        return sb.ToString();
    }

    private static async Task WriteAsync(NetworkStream stream, int status, string reason, string html, CancellationToken ct)
    {
        var body = Encoding.UTF8.GetBytes(html);
        var head = $"HTTP/1.1 {status} {reason}\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: {body.Length}\r\nCache-Control: no-store\r\nConnection: close\r\n\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(head), ct).ConfigureAwait(false);
        await stream.WriteAsync(body, ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);
    }

    private static string Page(string title, string message)
        => $"<!DOCTYPE html><html lang=\"zh-CN\"><head><meta charset=\"utf-8\"><title>{WebUtility.HtmlEncode(title)}</title>" +
           "<style>body{font-family:'Segoe UI','Microsoft YaHei UI',sans-serif;text-align:center;margin-top:18vh;color:#1b1b1b}</style></head>" +
           $"<body><h2>{WebUtility.HtmlEncode(title)}</h2><p>{WebUtility.HtmlEncode(message)}</p></body></html>";

    public void Dispose() => _listener.Stop();
}
