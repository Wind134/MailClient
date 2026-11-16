namespace MailClient.Core.Auth;

/// <summary>一次连接检查 / 一次失败归为哪一类。</summary>
public enum ConnectionStatus
{
    /// <summary>连接正常，登录有效。</summary>
    Ok,

    /// <summary>登录失效：令牌过期 / 被撤销 / 缺失、invalid_grant、MSAL 需要交互、IMAP 密码错误。只有这一类会提示重新登录。</summary>
    AuthFailed,

    /// <summary>暂时性问题：网络、TLS、超时、服务器断开。登录可能仍然有效，稍后自动重试。</summary>
    Transient,

    /// <summary>其他问题（配置缺失、服务器拒绝等），重新登录不一定有用。</summary>
    Failed
}

/// <param name="Message">面向用户的中文说明；Ok 时为空。</param>
public sealed record ConnectionCheckResult(ConnectionStatus Status, string? Message = null)
{
    public static ConnectionCheckResult Healthy { get; } = new(ConnectionStatus.Ok);

    public bool IsOk => Status == ConnectionStatus.Ok;
}

/// <summary>右键“重新登录”时，先检查连接，再决定下一步。</summary>
public enum ReloginCheckDecision
{
    /// <summary>登录确实失效：直接进入重新登录流程。</summary>
    StartReauth,

    /// <summary>连接正常：告诉用户无需重新登录（可立即同步 / 仍要重新登录）。</summary>
    ShowHealthy,

    /// <summary>像是网络问题：提示检查网络（可仍要重新登录）。</summary>
    ShowNetworkProblem,

    /// <summary>其他错误：显示原因（可仍要重新登录）。</summary>
    ShowCheckFailed
}

public static class ReloginFlow
{
    public static ReloginCheckDecision Decide(ConnectionCheckResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        return result.Status switch
        {
            ConnectionStatus.Ok => ReloginCheckDecision.ShowHealthy,
            ConnectionStatus.AuthFailed => ReloginCheckDecision.StartReauth,
            ConnectionStatus.Transient => ReloginCheckDecision.ShowNetworkProblem,
            _ => ReloginCheckDecision.ShowCheckFailed
        };
    }

    /// <summary>把检查结果喂给 <see cref="ReloginTracker"/>：成功清标记，认证失败打标记，其余不改状态。</summary>
    public static void Apply(ReloginTracker tracker, Guid accountId, ConnectionCheckResult result)
    {
        ArgumentNullException.ThrowIfNull(tracker);
        ArgumentNullException.ThrowIfNull(result);
        switch (result.Status)
        {
            case ConnectionStatus.Ok:
                tracker.ReportSuccess(accountId);
                break;
            case ConnectionStatus.AuthFailed:
                tracker.ReportAuthFailure(accountId);
                break;
        }
    }
}
