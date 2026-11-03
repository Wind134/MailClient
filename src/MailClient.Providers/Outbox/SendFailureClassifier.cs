using System.Net.Sockets;
using MailClient.Core.Sending;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Kiota.Abstractions;

namespace MailClient.Providers.Outbox;

/// <summary>
/// 把发送 / 写回过程中抛出的异常分成“永久失败”（认证错误、收件人被拒、邮件被拒……重试没意义）
/// 与“暂时失败”（网络、超时、4xx、限流……按退避重试）。纯函数，可单测。
/// </summary>
public static class SendFailureClassifier
{
    public static FailureInfo Classify(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        switch (exception)
        {
            case ClassifiedFailureException c:
                return new FailureInfo(c.Kind, c.Message);
            case AggregateException { InnerExceptions.Count: 1 } agg:
                return Classify(agg.InnerExceptions[0]);
            case AuthenticationException: // 含 SaslException
                return new FailureInfo(FailureKind.Auth, "登录失败：用户名、密码或授权码不正确（或授权已失效），请在账号设置里更新后重试。");
            case SmtpCommandException smtp:
                return ClassifySmtp(smtp);
            case SmtpProtocolException:
                return new FailureInfo(FailureKind.Transient, "SMTP 通信中断，稍后自动重试。");
            case ImapCommandException imap:
                return ClassifyImap(imap);
            case ImapProtocolException:
                return new FailureInfo(FailureKind.Transient, "IMAP 通信中断，稍后自动重试。");
            case ServiceNotConnectedException:
            case ServiceNotAuthenticatedException:
                return new FailureInfo(FailureKind.Transient, "与服务器的连接已断开，稍后自动重试。");
            case SslHandshakeException:
                return new FailureInfo(FailureKind.Transient, "与服务器建立加密连接失败（证书或网络问题），稍后自动重试。");
            case ApiException api:
                return ClassifyApi(api);
            case FileNotFoundException f:
                return new FailureInfo(FailureKind.MessageRejected, $"附件文件不存在：{Path.GetFileName(f.FileName)}");
            case TimeoutException:
            case OperationCanceledException:
                return new FailureInfo(FailureKind.Transient, "连接超时，稍后自动重试。");
            case SocketException:
            case IOException { InnerException: SocketException }:
            case HttpRequestException:
            case IOException:
                return new FailureInfo(FailureKind.Transient, "网络连接失败，网络恢复后会自动重试。");
            case MimeKit.ParseException:
            case FormatException:
            case ArgumentException:
                return new FailureInfo(FailureKind.MessageRejected, $"邮件内容无效：{exception.Message}");
        }

        var name = exception.GetType().Name;
        if (name.Contains("MsalUiRequired", StringComparison.Ordinal) ||
            name.Contains("GmailAuthRequired", StringComparison.Ordinal) ||
            name.Contains("MsalServiceException", StringComparison.Ordinal) ||
            name.Contains("TokenResponseException", StringComparison.Ordinal))
        {
            return new FailureInfo(FailureKind.Auth, "账号授权已失效，请重新登录该账号后重试。");
        }

        if (exception is InvalidOperationException ioe &&
            (ioe.Message.Contains("ClientId", StringComparison.OrdinalIgnoreCase) || ioe.Message.Contains("找不到", StringComparison.Ordinal) ||
             ioe.Message.Contains("缺少", StringComparison.Ordinal) || ioe.Message.Contains("尚未配置", StringComparison.Ordinal)))
        {
            return new FailureInfo(FailureKind.Configuration, ioe.Message);
        }

        // 不认识的异常：当作暂时性失败，由最大重试次数兜底
        return new FailureInfo(FailureKind.Transient, $"发送出错：{exception.Message}");
    }

    private static FailureInfo ClassifySmtp(SmtpCommandException ex)
    {
        var code = (int)ex.StatusCode;
        switch (ex.ErrorCode)
        {
            case SmtpErrorCode.RecipientNotAccepted:
                return code >= 500 || code == 0
                    ? new FailureInfo(FailureKind.RecipientRejected, $"收件人被服务器拒绝：{ex.Mailbox?.Address ?? "未知地址"}（{ex.Message}）")
                    : new FailureInfo(FailureKind.Transient, "服务器暂时无法接收该收件人，稍后自动重试。");
            case SmtpErrorCode.SenderNotAccepted:
                return new FailureInfo(FailureKind.MessageRejected, $"发件人地址被服务器拒绝：{ex.Message}");
        }

        if (code is 530 or 534 or 535 or 538)
        {
            return new FailureInfo(FailureKind.Auth, "登录失败：用户名、密码或授权码不正确，请在账号设置里更新后重试。");
        }

        if (code == 552 || (code == 554 && ex.Message.Contains("size", StringComparison.OrdinalIgnoreCase)))
        {
            return new FailureInfo(FailureKind.MessageRejected, "邮件过大，被服务器拒绝，请减少附件后重试。");
        }

        if (code is >= 400 and < 500)
        {
            return new FailureInfo(FailureKind.Transient, $"服务器暂时拒绝（{code}），稍后自动重试：{ex.Message}");
        }

        if (code >= 500)
        {
            return new FailureInfo(FailureKind.MessageRejected, $"服务器拒绝了这封邮件（{code}）：{ex.Message}");
        }

        return new FailureInfo(FailureKind.Transient, $"发送出错：{ex.Message}");
    }

    private static FailureInfo ClassifyImap(ImapCommandException ex)
    {
        var text = ex.Message ?? string.Empty;
        if (text.Contains("auth", StringComparison.OrdinalIgnoreCase) || text.Contains("login", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("credentials", StringComparison.OrdinalIgnoreCase))
        {
            return new FailureInfo(FailureKind.Auth, "登录失败：用户名、密码或授权码不正确，请在账号设置里更新后重试。");
        }

        if (ex.Response == ImapCommandResponse.No &&
            (text.Contains("NONEXISTENT", StringComparison.OrdinalIgnoreCase) || text.Contains("TRYCREATE", StringComparison.OrdinalIgnoreCase) ||
             text.Contains("no such", StringComparison.OrdinalIgnoreCase) || text.Contains("not exist", StringComparison.OrdinalIgnoreCase)))
        {
            return new FailureInfo(FailureKind.NotFound, "服务器上找不到目标文件夹或邮件。");
        }

        return new FailureInfo(FailureKind.ServerRejected, $"服务器拒绝了该操作：{text}");
    }

    private static FailureInfo ClassifyApi(ApiException api)
    {
        var status = api.ResponseStatusCode;
        var code = (api as Microsoft.Graph.Models.ODataErrors.ODataError)?.Error?.Code ?? string.Empty;
        if (code.Contains("InvalidRecipients", StringComparison.OrdinalIgnoreCase))
        {
            return new FailureInfo(FailureKind.RecipientRejected, "收件人地址无效，被服务器拒绝。");
        }

        return status switch
        {
            401 or 403 => new FailureInfo(FailureKind.Auth, "Microsoft 账号授权已失效或权限不足，请重新登录该账号。"),
            404 => new FailureInfo(FailureKind.NotFound, "服务器上找不到该邮件或文件夹。"),
            400 or 413 or 422 => new FailureInfo(FailureKind.MessageRejected, $"服务器拒绝了这封邮件（{status}）：{api.Message}"),
            429 => new FailureInfo(FailureKind.Transient, "请求过于频繁（限流），稍后自动重试。"),
            >= 500 => new FailureInfo(FailureKind.Transient, $"服务器暂时不可用（{status}），稍后自动重试。"),
            _ => new FailureInfo(FailureKind.Transient, $"服务器返回错误（{status}）：{api.Message}")
        };
    }
}
