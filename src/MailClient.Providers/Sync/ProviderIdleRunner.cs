using MailClient.Core;
using MailClient.Providers.Setup;

namespace MailClient.Providers.Sync;

/// <summary>真实的 IDLE 会话：为账号开一条专用 IMAP 连接，在收件箱上 IDLE（29 分钟换一次 IDLE）。</summary>
public sealed class ProviderIdleRunner : IIdleRunner
{
    /// <summary>RFC 2177 建议客户端至少每 29 分钟重新 IDLE 一次。</summary>
    public static readonly TimeSpan ReIdleInterval = TimeSpan.FromMinutes(29);

    private readonly AccountProviderFactory _factory;

    public ProviderIdleRunner(AccountProviderFactory factory)
    {
        _factory = factory;
    }

    public async Task<IdleOutcome> RunAsync(AccountEntity account, Func<CancellationToken, Task> onChange, CancellationToken cancellationToken)
    {
        await using var handle = await _factory.OpenAsync(account, cancellationToken).ConfigureAwait(false);
        if (handle.Imap is null)
        {
            return IdleOutcome.Unsupported;      // Graph：桌面应用收不到 webhook，只能轮询
        }

        return await handle.Imap.IdleWatchAsync("INBOX", onChange, ReIdleInterval, cancellationToken).ConfigureAwait(false);
    }
}
