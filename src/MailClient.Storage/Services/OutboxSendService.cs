using System.Text.Json;
using MailClient.Core;
using MailClient.Core.Sending;
using MailClient.Core.Services;

namespace MailClient.Storage.Services;

/// <summary>
/// ISendService 的本地实现：校验 → 分配 Message-ID（幂等的基础）→ 把附件复制到应用目录 → 把待发邮件序列化后写入 outbox 表（Pending）。
/// 真正的投递由后台 OutboxWorker 完成（订阅 MailDatabase.OutboxChanged，入队后立即被唤醒）。
/// </summary>
public sealed class OutboxSendService : ISendService
{
    private readonly MailDatabase _database;
    private readonly string? _attachmentRoot;

    /// <param name="attachmentRoot">附件副本目录；null = 不复制（测试 / 无附件）。</param>
    public OutboxSendService(MailDatabase database, string? attachmentRoot = null)
    {
        _database = database;
        _attachmentRoot = attachmentRoot;
    }

    public async Task EnqueueAsync(OutgoingMailMessage message, CancellationToken cancellationToken = default)
    {
        var accounts = await _database.ListAccountsAsync(cancellationToken).ConfigureAwait(false);
        var account = Guid.TryParse(message.AccountId, out var requested)
            ? accounts.FirstOrDefault(a => a.Id == requested)
            : accounts.FirstOrDefault(a => a.IsEnabled);
        if (account is null)
        {
            throw new InvalidOperationException("没有可用的发件账号。");
        }

        var id = Guid.NewGuid().ToString("N");
        var domain = account.EmailAddress.Contains('@') ? account.EmailAddress[(account.EmailAddress.IndexOf('@') + 1)..] : "localhost";
        var messageId = string.IsNullOrWhiteSpace(message.MessageId)
            ? $"{Guid.NewGuid():N}@{domain}"
            : message.MessageId!.Trim().Trim('<', '>');

        var paths = message.AttachmentPaths;
        if (_attachmentRoot is not null && paths.Count > 0)
        {
            paths = OutboxAttachmentStore.CopyIn(_attachmentRoot, id, paths);
        }

        var now = DateTimeOffset.UtcNow;
        var payload = JsonSerializer.Serialize(message with
        {
            AccountId = account.Id.ToString("D"),
            MessageId = messageId,
            AttachmentPaths = paths
        }, MailClientJsonContext.Default.OutgoingMailMessage);
        await _database.EnqueueOutboxAsync(
            new OutboxEntity(id, account.Id, payload, OutboxStatus.Pending, 0, null, now, now, RfcMessageId: messageId),
            cancellationToken).ConfigureAwait(false);
    }
}
