using MailKit;
using MailKit.Net.Imap;

namespace MailClient.Providers;

public enum IdleOutcome
{
    /// <summary>服务器不支持 IDLE（QQ / 163 的部分服务器可能如此）：调用方应退回轮询。</summary>
    Unsupported,

    /// <summary>连接正常结束（被取消除外）。</summary>
    Ended
}

public sealed partial class GenericImapProvider
{
    /// <summary>
    /// 在专用连接上对 <paramref name="folderPath"/>（通常是收件箱）做 IMAP IDLE：
    /// 服务器通知有新邮件 / 标志变化 / 删除时，先结束这轮 IDLE（短暂防抖合并突发通知），调用 <paramref name="onChange"/>，再重新进入 IDLE。
    /// 每 <paramref name="reidleAfter"/>（默认 29 分钟，规范建议不超过 30 分钟）主动 DONE 并重新 IDLE，防止被服务器 / NAT 断开。
    /// 连接断开会抛异常，由调用方（<see cref="Imap.ImapIdleSupervisor"/>）按退避策略重连。
    /// 注意：IDLE 期间连接被占用，所以应该用单独的 provider 实例，不要和同步 / 写回共用。
    /// </summary>
    public async Task<IdleOutcome> IdleWatchAsync(
        string folderPath,
        Func<CancellationToken, Task> onChange,
        TimeSpan reidleAfter,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(onChange);
        await EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);
        var client = _imap ?? throw new InvalidOperationException("IMAP 未连接。");
        if (!client.Capabilities.HasFlag(ImapCapabilities.Idle))
        {
            return IdleOutcome.Unsupported;
        }

        var folder = await GetFolderObjectAsync(client, folderPath, cancellationToken).ConfigureAwait(false);
        if (!folder.IsOpen)
        {
            await folder.OpenAsync(FolderAccess.ReadOnly, cancellationToken).ConfigureAwait(false);
        }

        while (!cancellationToken.IsCancellationRequested)
        {
            if (!client.IsConnected)
            {
                return IdleOutcome.Ended;
            }

            using var done = new CancellationTokenSource(reidleAfter);
            var changed = 0;

            void Notify()
            {
                Interlocked.Exchange(ref changed, 1);
                try
                {
                    done.CancelAfter(TimeSpan.FromMilliseconds(500));
                }
                catch (ObjectDisposedException)
                {
                }
            }

            void OnCount(object? s, EventArgs e) => Notify();
            void OnFlags(object? s, MessageFlagsChangedEventArgs e) => Notify();
            void OnVanished(object? s, MessagesVanishedEventArgs e) => Notify();
            void OnExpunged(object? s, MessageEventArgs e) => Notify();

            folder.CountChanged += OnCount;
            folder.MessageFlagsChanged += OnFlags;
            folder.MessagesVanished += OnVanished;
            folder.MessageExpunged += OnExpunged;
            try
            {
                await client.IdleAsync(done.Token, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                folder.CountChanged -= OnCount;
                folder.MessageFlagsChanged -= OnFlags;
                folder.MessagesVanished -= OnVanished;
                folder.MessageExpunged -= OnExpunged;
            }

            if (Volatile.Read(ref changed) == 1)
            {
                await onChange(cancellationToken).ConfigureAwait(false);
            }
        }

        return IdleOutcome.Ended;
    }
}
