using System.Net.Sockets;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Net.Smtp;
using MailKit.Security;
using MailClient.Auth;
using Microsoft.Identity.Client;

namespace MailClient.Providers.Setup;

/// <summary>把 MailKit / MSAL / Google OAuth / 网络异常翻译成面向用户的中文提示。</summary>
public static class SetupErrorClassifier
{
    public static SetupError Classify(Exception ex, SetupProviderKind kind, SetupStage stage, string? host = null)
    {
        ArgumentNullException.ThrowIfNull(ex);
        var where = stage switch
        {
            SetupStage.Imap => "收信服务器（IMAP）",
            SetupStage.Smtp => "发信服务器（SMTP）",
            SetupStage.Graph => "Microsoft Graph",
            _ => "登录"
        };
        var hostText = string.IsNullOrWhiteSpace(host) ? "服务器" : $"服务器 {host}";
        var authCodeProvider = kind is SetupProviderKind.Qq or SetupProviderKind.NetEase;
        var detail = ex.GetType().Name + ": " + ex.Message;

        SetupError Make(SetupErrorKind k, string message) => new(k, message, detail);

        switch (ex)
        {
            case OperationCanceledException:
                return Make(SetupErrorKind.Cancelled, "已取消。");

            case TimeoutException:
                return Make(SetupErrorKind.Timeout, stage == SetupStage.Login
                    ? "等待登录超时，请重新点击登录按钮重试。"
                    : $"连接{where}超时（{hostText}）。请检查网络、代理 / 防火墙，以及服务器地址与端口是否正确。");

            case SslHandshakeException:
            case System.Security.Authentication.AuthenticationException:
                return Make(SetupErrorKind.TlsError,
                    $"与{where}建立加密连接（TLS/SSL）失败。请确认「安全连接」设置与端口匹配（993/465 用 SSL，143/587 用 STARTTLS），" +
                    "并检查系统时间、杀毒软件的邮件扫描或公司代理是否拦截了证书。");

            case MailKit.Security.AuthenticationException:
                return authCodeProvider
                    ? Make(SetupErrorKind.NeedAuthCode,
                        "登录被拒绝：这类邮箱不能使用网页登录密码，需要「授权码」。" + AuthCodeHint(kind))
                    : Make(SetupErrorKind.AuthFailed, $"{where}认证失败：" + PasswordHint(kind));

            case ImapCommandException imapCommand:
                return ClassifyCommand(imapCommand.Message, kind, authCodeProvider, where, hostText, detail);

            case SmtpCommandException smtpCommand:
                if (smtpCommand.StatusCode is SmtpStatusCode.AuthenticationRequired
                    or SmtpStatusCode.AuthenticationInvalidCredentials
                    or SmtpStatusCode.MailboxUnavailable
                    or SmtpStatusCode.AuthenticationMechanismTooWeak
                    || smtpCommand.Message.Contains("auth", StringComparison.OrdinalIgnoreCase))
                {
                    return authCodeProvider
                        ? Make(SetupErrorKind.NeedAuthCode,
                            "发信服务器拒绝登录：请使用「授权码」而不是网页登录密码，并确认已开启 SMTP 服务。" + AuthCodeHint(kind))
                        : Make(SetupErrorKind.AuthFailed, $"{where}认证失败（{smtpCommand.Message}）：" + PasswordHint(kind));
                }

                return Make(SetupErrorKind.Protocol, $"{where}返回错误：{smtpCommand.Message}");

            case ProtocolException:
                return Make(SetupErrorKind.Protocol,
                    $"{where}返回了无法识别的响应。通常是端口与加密方式不匹配（例如把 SSL 端口 993 配成了 STARTTLS），请在「高级设置」里核对。");

            case ServiceNotConnectedException:
                return Make(SetupErrorKind.ServerUnreachable, $"与{where}的连接已断开，请重试。");

            case SocketException socket:
                return ClassifySocket(socket, kind, where, hostText, detail);

            case GoogleOAuthException google:
                if (string.Equals(google.Error, "access_denied", StringComparison.OrdinalIgnoreCase))
                {
                    return Make(SetupErrorKind.OAuthDenied,
                        "Google 登录被拒绝：你在浏览器里取消了授权，或该 Google 账号不在 OAuth 同意屏幕的「测试用户」名单中（应用处于测试状态时必须先把账号加入名单）。");
                }

                if (string.Equals(google.Error, "invalid_client", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(google.Error, "unauthorized_client", StringComparison.OrdinalIgnoreCase))
                {
                    return Make(SetupErrorKind.MissingClientId,
                        "Google 不认可当前的 ClientId / 客户端密钥。请核对：创建的是「桌面应用」类型的 OAuth 客户端；ClientId 和客户端密钥都完整复制、没有多余空格。步骤见添加账号页的「怎么设置」。");
                }

                return Make(SetupErrorKind.AuthFailed, $"Google 授权失败：{google.Message}");

            case GmailAuthRequiredException gmailRequired:
                return Make(SetupErrorKind.AuthFailed, gmailRequired.Message);

            case MsalClientException msalClient:
                if (msalClient.ErrorCode is "authentication_canceled" or "access_denied")
                {
                    return Make(SetupErrorKind.Cancelled, "已取消 Microsoft 登录。");
                }

                if (MicrosoftGraphTokenAcquirer.IsWamBrokerFailure(msalClient))
                {
                    return Make(SetupErrorKind.AuthFailed,
                        MicrosoftGraphTokenAcquirer.FormatInteractiveFailure(msalClient, clientId: null));
                }

                return Make(SetupErrorKind.AuthFailed, $"Microsoft 登录失败（{msalClient.ErrorCode}）：{FirstLine(msalClient.Message)}");

            case MsalUiRequiredException:
                return Make(SetupErrorKind.AuthFailed, "Microsoft 登录已失效，需要重新登录。");

            case MsalServiceException msalService:
                if (msalService.ErrorCode is "invalid_client" or "unauthorized_client" || msalService.Message.Contains("AADSTS700016"))
                {
                    return Make(SetupErrorKind.MissingClientId,
                        "Microsoft 找不到该应用（应用 ID 不正确，或租户不匹配）。请核对在 Microsoft Entra 注册的应用：复制的是「应用程序(客户端) ID」，「受支持的账户类型」包含个人 Microsoft 账户。步骤见添加账号页的「怎么设置」。");
                }

                if (MicrosoftGraphTokenAcquirer.IsWamBrokerFailure(msalService))
                {
                    return Make(SetupErrorKind.AuthFailed,
                        MicrosoftGraphTokenAcquirer.FormatInteractiveFailure(msalService, clientId: null));
                }

                return Make(SetupErrorKind.AuthFailed, $"Microsoft 登录失败（{msalService.ErrorCode}）：{FirstLine(msalService.Message)}");

            case InvalidOperationException op when MicrosoftGraphTokenAcquirer.IsWamBrokerFailure(op) ||
                (op.InnerException is not null && MicrosoftGraphTokenAcquirer.IsWamBrokerFailure(op.InnerException)) ||
                op.Message.Contains("ms-appx-web://microsoft.aad.brokerplugin", StringComparison.OrdinalIgnoreCase):
                // AcquireAsync 已把 WAM 失败包成带重定向提示的 InvalidOperationException
                return Make(SetupErrorKind.AuthFailed, op.Message);

            case HttpRequestException http:
                return Make(SetupErrorKind.Network,
                    stage == SetupStage.Graph
                        ? $"访问 Microsoft Graph 失败：{FirstLine(http.Message)}。请检查网络；若返回 401/403，请确认应用已被授予 Mail.ReadWrite / Mail.Send 权限。"
                        : $"网络请求失败：{FirstLine(http.Message)}。请检查网络连接。");

            case InvalidOperationException op when op.Message.Contains("不一致"):
                return Make(SetupErrorKind.AccountMismatch, op.Message);

            case InvalidOperationException op when op.Message.Contains("ClientId", StringComparison.OrdinalIgnoreCase):
                return Make(SetupErrorKind.MissingClientId, op.Message);

            case ArgumentException arg when arg.Message.Contains("ClientId", StringComparison.OrdinalIgnoreCase):
                return Make(SetupErrorKind.MissingClientId, "还没有填写 ClientId。请展开添加账号页的「怎么设置」，按步骤创建后粘贴。");

            case AggregateException aggregate when aggregate.InnerException is not null:
                return Classify(aggregate.InnerException, kind, stage, host);

            case IOException io when io.InnerException is not null:
                return Classify(io.InnerException, kind, stage, host);

            case IOException io:
                return Make(SetupErrorKind.ServerUnreachable, $"与{where}通信时连接中断：{FirstLine(io.Message)}。");
        }

        return Make(SetupErrorKind.Unknown, $"{where}出错：{FirstLine(ex.Message)}");
    }

    private static string AuthCodeHint(SetupProviderKind kind) => kind == SetupProviderKind.Qq
        ? "请在网页版 QQ 邮箱「设置 → 账号与安全 → 安全设置」开启 IMAP/SMTP 服务并「生成授权码」，把 16 位授权码填到密码框；改过 QQ 密码的话旧授权码已失效，需要重新生成。"
        : "请在网页版网易邮箱「设置 → POP3/SMTP/IMAP」开启 IMAP/SMTP 服务并「新增授权密码」，把授权码填到密码框；授权码只显示一次，丢了请重新生成。";

    private static string PasswordHint(SetupProviderKind kind) => kind == SetupProviderKind.Gmail
        ? "Gmail 登录失败。请重新点「用 Google 登录」；若改用应用专用密码，需先开启两步验证再生成。"
        : "用户名或密码不正确。Gmail、iCloud、Yahoo 等邮箱需要「应用专用密码」而不是登录密码（先开启两步验证再生成）；另请确认邮箱设置里已开启 IMAP/SMTP。";

    private static SetupError ClassifyCommand(string message, SetupProviderKind kind, bool authCodeProvider, string where, string hostText, string detail)
    {
        if (message.Contains("Unsafe Login", StringComparison.OrdinalIgnoreCase))
        {
            return new SetupError(
                SetupErrorKind.UnsafeLogin,
                "网易邮箱拒绝了登录（Unsafe Login）。请在网页版邮箱「设置 → POP3/SMTP/IMAP」中开启 IMAP/SMTP 服务，使用新生成的「授权码」（不是网页密码）；" +
                "若已确认无误，可能是服务器暂时限制了此客户端，请稍后再试。",
                detail);
        }

        if (message.Contains("login", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("authenticat", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("password", StringComparison.OrdinalIgnoreCase))
        {
            return authCodeProvider
                ? new SetupError(SetupErrorKind.NeedAuthCode, "登录被拒绝：请使用「授权码」而不是网页登录密码，并确认已开启 IMAP/SMTP 服务。" + AuthCodeHint(kind), detail)
                : new SetupError(SetupErrorKind.AuthFailed, $"{where}认证失败：" + PasswordHint(kind), detail);
        }

        return new SetupError(SetupErrorKind.Protocol, $"{where}（{hostText}）拒绝了请求：{FirstLine(message)}", detail);
    }

    private static SetupError ClassifySocket(SocketException socket, SetupProviderKind kind, string where, string hostText, string detail)
    {
        switch (socket.SocketErrorCode)
        {
            case SocketError.HostNotFound:
            case SocketError.NoData:
            case SocketError.TryAgain:
                return new SetupError(SetupErrorKind.HostNotFound,
                    $"找不到{hostText}（域名解析失败）。请检查服务器地址拼写和网络连接。", detail);
            case SocketError.ConnectionRefused:
                return new SetupError(SetupErrorKind.ServerUnreachable,
                    $"{hostText}拒绝了连接。请确认端口是否正确，以及邮箱是否已开启 IMAP/SMTP 服务。", detail);
            case SocketError.TimedOut:
                return new SetupError(SetupErrorKind.Timeout,
                    $"连接{where}超时（{hostText}）。请检查网络、代理 / 防火墙，以及端口是否被封锁。", detail);
            default:
                return new SetupError(SetupErrorKind.ServerUnreachable,
                    $"无法连接到{hostText}（{socket.SocketErrorCode}）。请检查网络连接、服务器地址与端口。", detail);
        }
    }

    private static string FirstLine(string text)
    {
        var line = text.Split('\n', 2)[0].Trim();
        return line.Length > 200 ? line[..200] + "…" : line;
    }
}
