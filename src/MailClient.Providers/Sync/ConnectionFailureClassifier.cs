using System.Net.Sockets;
using MailClient.Auth;
using MailClient.Core.Auth;
using MailClient.Core.Sending;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Identity.Client;
using Microsoft.Kiota.Abstractions;

namespace MailClient.Providers.Sync;

/// <summary>
/// 把同步 / IDLE / 加载正文 / 连接检查里抛出的异常分成：登录失效（<see cref="ConnectionStatus.AuthFailed"/>，需要用户重新登录）、
/// 暂时性问题（<see cref="ConnectionStatus.Transient"/>：网络、TLS、超时）和其他（<see cref="ConnectionStatus.Failed"/>）。
/// 纯函数，可单测。只有 AuthFailed 会触发“登录已失效”提醒。
/// </summary>
public static class ConnectionFailureClassifier
{
    public static bool IsAuthFailure(Exception exception) => Classify(exception) == ConnectionStatus.AuthFailed;

    public static ConnectionStatus Classify(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        return ClassifyCore(exception, depth: 0);
    }

    private static ConnectionStatus ClassifyCore(Exception ex, int depth)
    {
        if (depth > 8)
        {
            return ConnectionStatus.Failed;
        }

        switch (ex)
        {
            // ── 登录失效 ──
            case GmailAuthRequiredException:
            case MsalUiRequiredException:
            case MailKit.Security.AuthenticationException:      // IMAP / SMTP 用户名、密码、授权码或 XOAUTH2 令牌被拒（含 SaslException）
                return ConnectionStatus.AuthFailed;

            case GoogleOAuthException google:
                if (google.IsInvalidGrant)
                {
                    return ConnectionStatus.AuthFailed;
                }

                return google.Error is "temporarily_unavailable" or "server_error"
                    ? ConnectionStatus.Transient
                    : ConnectionStatus.Failed;      // invalid_client / access_denied 等：配置问题或用户取消

            case MsalServiceException msal:
                if (msal.ErrorCode is "invalid_grant" or "interaction_required" or "login_required" or "consent_required" or "bad_token")
                {
                    return ConnectionStatus.AuthFailed;
                }

                if (msal.StatusCode >= 500 || msal.StatusCode == 429 ||
                    msal.ErrorCode is "service_not_available" or "request_timeout" or "temporarily_unavailable")
                {
                    return ConnectionStatus.Transient;
                }

                return ex.InnerException is { } msalInner ? ClassifyCore(msalInner, depth + 1) : ConnectionStatus.Failed;

            case MsalClientException msalClient:
                if (msalClient.ErrorCode is "request_timeout" or "service_not_available")
                {
                    return ConnectionStatus.Transient;
                }

                return ex.InnerException is { } clientInner ? ClassifyCore(clientInner, depth + 1) : ConnectionStatus.Failed;

            case ClassifiedFailureException classified:
                return classified.Kind switch
                {
                    FailureKind.Auth => ConnectionStatus.AuthFailed,
                    FailureKind.Transient => ConnectionStatus.Transient,
                    _ => ConnectionStatus.Failed
                };

            case ImapCommandException imap when LooksLikeLoginRejected(imap.Message):
                return ConnectionStatus.AuthFailed;

            case SmtpCommandException smtp when smtp.StatusCode is SmtpStatusCode.AuthenticationInvalidCredentials or SmtpStatusCode.AuthenticationRequired:
                return ConnectionStatus.AuthFailed;

            case ApiException api when api.ResponseStatusCode == 401:
                return ConnectionStatus.AuthFailed;

            case ApiException api when api.ResponseStatusCode is 429 or >= 500:
                return ConnectionStatus.Transient;

            // 凭据库里找不到 IMAP 密码：同样需要重新输入
            case InvalidOperationException op when op.Message.StartsWith("凭据库中找不到密钥", StringComparison.Ordinal):
                return ConnectionStatus.AuthFailed;

            // ── 暂时性问题 ──
            case SslHandshakeException:
            case System.Security.Authentication.AuthenticationException:      // TLS 握手失败（不是账号密码）
            case TimeoutException:
            case OperationCanceledException:                                   // HttpClient 超时会抛 TaskCanceledException
            case SocketException:
            case HttpRequestException:
            case ServiceNotConnectedException:
            case ServiceNotAuthenticatedException:
            case ProtocolException:
                return ConnectionStatus.Transient;

            case IOException io:
                return io.InnerException is { } ioInner && ClassifyCore(ioInner, depth + 1) == ConnectionStatus.AuthFailed
                    ? ConnectionStatus.AuthFailed
                    : ConnectionStatus.Transient;

            case AggregateException aggregate when aggregate.InnerExceptions.Count > 0:
                // 任一内部异常是登录失效就算登录失效
                var statuses = aggregate.InnerExceptions.Select(e => ClassifyCore(e, depth + 1)).ToList();
                return statuses.Contains(ConnectionStatus.AuthFailed) ? ConnectionStatus.AuthFailed
                    : statuses.All(s => s == ConnectionStatus.Transient) ? ConnectionStatus.Transient
                    : ConnectionStatus.Failed;
        }

        // 包装异常（例如 MicrosoftGraphTokenAcquirer 把 MSAL 错误包成 InvalidOperationException）：看内部原因
        return ex.InnerException is { } inner ? ClassifyCore(inner, depth + 1) : ConnectionStatus.Failed;
    }

    private static bool LooksLikeLoginRejected(string message)
        => message.Contains("AUTHENTICATIONFAILED", StringComparison.OrdinalIgnoreCase) ||
           message.Contains("Invalid credentials", StringComparison.OrdinalIgnoreCase) ||
           message.Contains("LOGIN failed", StringComparison.OrdinalIgnoreCase) ||
           message.Contains("authentication failed", StringComparison.OrdinalIgnoreCase);
}
