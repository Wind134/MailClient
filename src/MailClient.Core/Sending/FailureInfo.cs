namespace MailClient.Core.Sending;

public enum FailureKind
{
    /// <summary>网络 / 超时 / 服务器暂时不可用：稍后重试。</summary>
    Transient,
    /// <summary>认证失败（密码 / 授权码 / 令牌失效）：重试没有用，需要用户重新登录。</summary>
    Auth,
    /// <summary>收件人被服务器拒绝。</summary>
    RecipientRejected,
    /// <summary>邮件本身被拒绝（过大 / 格式错误 / 被判垃圾邮件 / 附件缺失）。</summary>
    MessageRejected,
    /// <summary>缺少账号 / 配置（如没有 ClientId、演示账号）。</summary>
    Configuration,
    /// <summary>写回时远程邮件已不存在。</summary>
    NotFound,
    /// <summary>IMAP UIDVALIDITY 已变化，本地保存的 UID 不再有效。</summary>
    UidValidityChanged,
    /// <summary>服务器明确拒绝了该操作（NO / BAD）。</summary>
    ServerRejected
}

/// <param name="Message">面向用户的中文说明（会显示在发件箱里）。</param>
public sealed record FailureInfo(FailureKind Kind, string Message)
{
    /// <summary>永久性失败：自动重试没有意义。</summary>
    public bool IsPermanent => Kind != FailureKind.Transient;
}

/// <summary>发件箱 / 写回队列自行抛出的“已分类”异常（网关层用）。</summary>
public sealed class ClassifiedFailureException : Exception
{
    public ClassifiedFailureException(FailureKind kind, string message, Exception? inner = null)
        : base(message, inner) => Kind = kind;

    public FailureKind Kind { get; }
}

/// <summary>失败后的重试节奏：指数退避，最多 <see cref="MaxAttempts"/> 次。</summary>
public static class RetryPolicy
{
    public const int MaxAttempts = 5;

    public static readonly TimeSpan BaseDelay = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan MaxDelay = TimeSpan.FromHours(1);

    /// <param name="attemptsSoFar">已经尝试的次数（>=1）。第 1 次失败后等 30 秒，之后 2 分、8 分、32 分……</param>
    public static TimeSpan Delay(int attemptsSoFar)
    {
        var n = Math.Max(1, attemptsSoFar);
        var seconds = BaseDelay.TotalSeconds * Math.Pow(4, n - 1);
        return TimeSpan.FromSeconds(Math.Min(seconds, MaxDelay.TotalSeconds));
    }
}
