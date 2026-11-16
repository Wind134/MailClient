using System.Net;
using System.Net.Sockets;
using System.Text;

namespace MailClient.Tests;

/// <summary>
/// 本机回环上的极简 IMAP 服务器（明文，无 TLS），只够测试“登录被拒 / 登录成功 + 打开收件箱”。
/// 密码等于 <see cref="AcceptedPassword"/> 时 LOGIN 成功，否则回 NO [AUTHENTICATIONFAILED]。
/// </summary>
internal sealed class FakeImapServer : IAsyncDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _loop;
    private int _logins;

    public FakeImapServer(string? acceptedPassword = null)
    {
        AcceptedPassword = acceptedPassword;
        _listener.Start();
        _loop = Task.Run(() => AcceptLoopAsync(_cts.Token));
    }

    public string? AcceptedPassword { get; set; }

    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    public int LoginAttempts => Volatile.Read(ref _logins);

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(ct);
            }
            catch (Exception)
            {
                return;
            }

            _ = Task.Run(() => HandleAsync(client, ct));
        }
    }

    private async Task HandleAsync(TcpClient client, CancellationToken ct)
    {
        using var _ = client;
        try
        {
            var stream = client.GetStream();
            var reader = new StreamReader(stream, Encoding.ASCII);
            var writer = new StreamWriter(stream, new UTF8Encoding(false)) { NewLine = "\r\n", AutoFlush = true };
            await writer.WriteLineAsync("* OK [CAPABILITY IMAP4rev1] fake ready");
            while (!ct.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync(ct);
                if (line is null)
                {
                    return;
                }

                var parts = line.Split(' ', 3);
                if (parts.Length < 2)
                {
                    continue;
                }

                var tag = parts[0];
                var command = parts[1].ToUpperInvariant();
                var args = parts.Length > 2 ? parts[2] : string.Empty;
                switch (command)
                {
                    case "CAPABILITY":
                        await writer.WriteLineAsync("* CAPABILITY IMAP4rev1");
                        await writer.WriteLineAsync($"{tag} OK CAPABILITY completed");
                        break;
                    case "LOGIN":
                    {
                        Interlocked.Increment(ref _logins);
                        var password = ParseLastAtom(args);
                        if (AcceptedPassword is not null && password == AcceptedPassword)
                        {
                            await writer.WriteLineAsync($"{tag} OK [CAPABILITY IMAP4rev1] LOGIN completed");
                        }
                        else
                        {
                            await writer.WriteLineAsync($"{tag} NO [AUTHENTICATIONFAILED] Invalid credentials");
                        }

                        break;
                    }
                    case "LIST" when args.Trim() == "\"\" \"\"":
                        await writer.WriteLineAsync("* LIST (\\Noselect) \"/\" \"\"");
                        await writer.WriteLineAsync($"{tag} OK LIST completed");
                        break;
                    case "LIST":
                        await writer.WriteLineAsync("* LIST (\\HasNoChildren) \"/\" INBOX");
                        await writer.WriteLineAsync($"{tag} OK LIST completed");
                        break;
                    case "SELECT":
                    case "EXAMINE":
                        await writer.WriteLineAsync("* FLAGS (\\Seen \\Flagged \\Deleted)");
                        await writer.WriteLineAsync("* 0 EXISTS");
                        await writer.WriteLineAsync("* 0 RECENT");
                        await writer.WriteLineAsync("* OK [UIDVALIDITY 1] ok");
                        await writer.WriteLineAsync("* OK [UIDNEXT 1] ok");
                        await writer.WriteLineAsync($"{tag} OK [{(command == "EXAMINE" ? "READ-ONLY" : "READ-WRITE")}] done");
                        break;
                    case "LOGOUT":
                        await writer.WriteLineAsync("* BYE bye");
                        await writer.WriteLineAsync($"{tag} OK LOGOUT completed");
                        return;
                    default:
                        await writer.WriteLineAsync($"{tag} OK {command} completed");
                        break;
                }
            }
        }
        catch (Exception)
        {
            // 客户端断开
        }
    }

    private static string ParseLastAtom(string args)
    {
        var text = args.Trim();
        if (text.EndsWith('"'))
        {
            var start = text.LastIndexOf('"', text.Length - 2);
            return text.Substring(start + 1, text.Length - start - 2).Replace("\\\"", "\"").Replace("\\\\", "\\");
        }

        var space = text.LastIndexOf(' ');
        return space >= 0 ? text[(space + 1)..] : text;
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        _listener.Stop();
        try
        {
            await _loop;
        }
        catch (Exception)
        {
        }

        _cts.Dispose();
    }
}
