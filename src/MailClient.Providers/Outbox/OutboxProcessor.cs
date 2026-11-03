using System.Text.Json;
using MailClient.Core;
using MailClient.Core.Sending;
using MailClient.Storage;
using MailClient.Storage.Demo;

namespace MailClient.Providers.Outbox;

public sealed record OutboxResult(OutboxEntity Item, OutgoingMailMessage? Message, bool Sent, FailureInfo? Failure, bool WillRetry);

/// <summary>
/// 发件箱的投递状态机（可用假的 <see cref="IOutgoingTransportFactory"/> 单测，不碰网络）：
/// <code>
/// Pending ──(原子占位, Attempts+1)──► Sending ──成功──► Sent ──(按策略)──► APPEND 到服务器已发送
///    ▲                                  │
///    └── 暂时性失败（指数退避, NextAttemptAt）┤
///                                       └── 永久失败 / 超过最大次数 ──► Failed
/// </code>
/// 幂等：Message-ID 在入队时就写进 outbox；启动时卡在 Sending 的条目被重置为 Pending 并标记 MaybeSent，
/// 再次投递前先查服务器“已发送”：找到 = 已经发出，不重复发送；找不到且可信（Gmail / Graph）才重发；无法确认则让用户决定。
/// </summary>
public sealed class OutboxProcessor
{
    public const int MaxCopyAttempts = 4;

    private readonly MailDatabase _db;
    private readonly IOutgoingTransportFactory _factory;
    private readonly Func<DateTimeOffset> _clock;
    private readonly string? _attachmentRoot;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public OutboxProcessor(MailDatabase db, IOutgoingTransportFactory factory, Func<DateTimeOffset>? clock = null, string? attachmentRoot = null)
    {
        _db = db;
        _factory = factory;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
        _attachmentRoot = attachmentRoot;
    }

    /// <summary>每处理完一个条目（成功 / 永久失败 / 将重试）触发，可能在后台线程。</summary>
    public event EventHandler<OutboxResult>? ItemProcessed;

    /// <summary>应用启动时调用：把崩溃时卡在 Sending 的条目恢复为 Pending（MaybeSent）。</summary>
    public Task<int> RecoverAsync(CancellationToken cancellationToken = default)
        => _db.ResetSendingOutboxAsync(_clock(), cancellationToken);

    /// <summary>处理所有到期的条目，返回处理数量。同一时刻只允许一个调用在运行。</summary>
    public async Task<int> ProcessOnceAsync(bool ignoreBackoff = false, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var processed = 0;
            var due = await _db.ListDueOutboxAsync(_clock(), ignoreBackoff, cancellationToken).ConfigureAwait(false);
            foreach (var item in due)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (await ProcessItemAsync(item, cancellationToken).ConfigureAwait(false))
                {
                    processed++;
                }
            }

            await _db.PruneSentOutboxAsync(_clock().AddDays(-1), cancellationToken).ConfigureAwait(false);
            await SweepOrphanAttachmentsAsync(cancellationToken).ConfigureAwait(false);
            return processed;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<bool> ProcessItemAsync(OutboxEntity listed, CancellationToken ct)
    {
        var item = listed;
        if (item.Status == OutboxStatus.Sent)
        {
            // 只剩“把副本追加到已发送”这一步
            var message = TryParse(item.PayloadJson);
            var account = await FindAccountAsync(item.AccountId, ct).ConfigureAwait(false);
            if (message is null || account is null)
            {
                await _db.UpdateOutboxAsync(item with { SentCopyPending = false, UpdatedAt = _clock() }, ct).ConfigureAwait(false);
                OutboxAttachmentStore.Delete(_attachmentRoot, item.Id);
                return true;
            }

            await AppendCopyAsync(item, message, account, ct).ConfigureAwait(false);
            return true;
        }

        if (!await _db.TryMarkOutboxSendingAsync(item.Id, _clock(), ct).ConfigureAwait(false))
        {
            return false; // 被别的线程 / 用户操作抢先了
        }

        item = item with { Status = OutboxStatus.Sending, Attempts = item.Attempts + 1 };

        var payload = TryParse(item.PayloadJson);
        if (payload is null)
        {
            await FailAsync(item, null, new FailureInfo(FailureKind.Configuration, "待发邮件数据已损坏，无法发送。"), ct).ConfigureAwait(false);
            return true;
        }

        var acct = await FindAccountAsync(item.AccountId, ct).ConfigureAwait(false);
        if (acct is null || !acct.IsEnabled)
        {
            await FailAsync(item, payload, new FailureInfo(FailureKind.Configuration, "发件账号已被移除或停用。"), ct).ConfigureAwait(false);
            return true;
        }

        if (DemoDataSeeder.IsDemoAccount(acct.Id))
        {
            await FailAsync(item, payload, new FailureInfo(FailureKind.Configuration, "演示账号不会真正发送邮件，请添加真实账号后再发送。"), ct).ConfigureAwait(false);
            return true;
        }

        var date = _clock();
        try
        {
            await using var transport = await _factory.CreateAsync(acct, ct).ConfigureAwait(false);
            var sentFolder = await FindSentFolderAsync(acct.Id, ct).ConfigureAwait(false);

            var alreadySent = false;
            if (item.MaybeSent)
            {
                SentLookup lookup;
                try
                {
                    lookup = await transport.FindInSentAsync(payload, item.CreatedAt.AddMinutes(-1), sentFolder?.RemotePath, ct).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    lookup = SentLookup.Unknown;
                }

                if (lookup == SentLookup.Found)
                {
                    alreadySent = true;
                }
                else if (!(lookup == SentLookup.NotFound && transport.SentLookupIsAuthoritative))
                {
                    await FailAsync(item, payload,
                        new FailureInfo(FailureKind.MessageRejected, "上次发送被中断，无法确认是否已发出。请先查看“已发送”，确认没有发出后再点“重试”。"), ct).ConfigureAwait(false);
                    return true;
                }
            }

            if (!alreadySent)
            {
                await transport.SendAsync(payload, acct.EmailAddress, acct.DisplayName, date, ct).ConfigureAwait(false);
            }

            var appendCopy = _factory.ShouldAppendSentCopy(acct);
            item = item with
            {
                Status = OutboxStatus.Sent,
                LastError = null,
                IsPermanent = false,
                MaybeSent = false,
                NextAttemptAt = null,
                SentCopyPending = appendCopy,
                UpdatedAt = _clock()
            };
            await _db.UpdateOutboxAsync(item, CancellationToken.None).ConfigureAwait(false);
            try
            {
                await InsertLocalCopyAsync(item, payload, acct, sentFolder, date, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // 邮件已经发出：本地副本写入失败不能让它被当成发送失败而重发；下次同步会从服务器带回
            }

            if (!appendCopy)
            {
                OutboxAttachmentStore.Delete(_attachmentRoot, item.Id);
            }

            Raise(new OutboxResult(item, payload, Sent: true, null, WillRetry: false));

            if (appendCopy)
            {
                await AppendCopyAsync(item, payload, acct, ct).ConfigureAwait(false);
            }

            return true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // 应用正在退出：条目保持 Sending，下次启动时由 RecoverAsync 处理
            throw;
        }
        catch (Exception ex)
        {
            var unknownOutcome = ex is SendOutcomeUnknownException;
            var failure = SendFailureClassifier.Classify(ex is SendOutcomeUnknownException { InnerException: { } inner } ? inner : ex);
            if (unknownOutcome)
            {
                failure = new FailureInfo(FailureKind.Transient, "发送过程中连接中断，稍后会先确认是否已发出再决定是否重发。");
            }

            if (failure.IsPermanent || item.Attempts >= RetryPolicy.MaxAttempts)
            {
                var text = failure.IsPermanent
                    ? failure
                    : new FailureInfo(failure.Kind, $"{failure.Message}（已重试 {item.Attempts} 次，放弃）");
                await FailAsync(item, payload, text, CancellationToken.None).ConfigureAwait(false);
            }
            else
            {
                var next = _clock() + RetryPolicy.Delay(item.Attempts);
                item = item with
                {
                    Status = OutboxStatus.Pending,
                    LastError = failure.Message,
                    NextAttemptAt = next,
                    MaybeSent = item.MaybeSent || unknownOutcome,
                    UpdatedAt = _clock()
                };
                await _db.UpdateOutboxAsync(item, CancellationToken.None).ConfigureAwait(false);
                Raise(new OutboxResult(item, payload, Sent: false, failure, WillRetry: true));
            }

            return true;
        }
    }

    /// <summary>用户在发件箱里删除条目后，遗留的附件副本目录（没有对应 outbox 行且超过 1 小时）一并清理。</summary>
    private async Task SweepOrphanAttachmentsAsync(CancellationToken ct)
    {
        if (_attachmentRoot is null || !Directory.Exists(_attachmentRoot))
        {
            return;
        }

        var live = (await _db.ListOutboxItemsAsync(ct).ConfigureAwait(false)).Select(i => i.Id)
            .Concat((await _db.ListDueOutboxAsync(_clock(), true, ct).ConfigureAwait(false)).Select(i => i.Id))
            .ToHashSet(StringComparer.Ordinal);
        foreach (var dir in Directory.EnumerateDirectories(_attachmentRoot))
        {
            var id = Path.GetFileName(dir);
            if (!live.Contains(id) && Directory.GetLastWriteTimeUtc(dir) < DateTime.UtcNow.AddHours(-1))
            {
                OutboxAttachmentStore.Delete(_attachmentRoot, id);
            }
        }
    }

    private async Task AppendCopyAsync(OutboxEntity item, OutgoingMailMessage message, AccountEntity account, CancellationToken ct)
    {
        try
        {
            var sentFolder = await FindSentFolderAsync(account.Id, ct).ConfigureAwait(false);
            await using var transport = await _factory.CreateAsync(account, ct).ConfigureAwait(false);
            var uid = await transport.AppendToSentAsync(
                message, account.EmailAddress, account.DisplayName, item.UpdatedAt, sentFolder?.RemotePath, ct).ConfigureAwait(false);
            if (uid is not null)
            {
                await _db.SetMessageRemoteUidAsync(LocalCopyId(item.Id), uid, CancellationToken.None).ConfigureAwait(false);
            }

            await _db.UpdateOutboxAsync(item with { SentCopyPending = false, NextAttemptAt = null, LastError = null, UpdatedAt = _clock() }, CancellationToken.None)
                .ConfigureAwait(false);
            OutboxAttachmentStore.Delete(_attachmentRoot, item.Id);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            var failure = SendFailureClassifier.Classify(ex);
            var attempts = item.CopyAttempts + 1;
            if (failure.IsPermanent || attempts >= MaxCopyAttempts)
            {
                // 邮件已经发出了；追加副本失败只记录原因，不再重试（本地已发送里仍有一份）
                await _db.UpdateOutboxAsync(item with
                {
                    SentCopyPending = false,
                    CopyAttempts = attempts,
                    LastError = $"邮件已发送，但保存到服务器“已发送”失败：{failure.Message}",
                    NextAttemptAt = null,
                    UpdatedAt = _clock()
                }, CancellationToken.None).ConfigureAwait(false);
                OutboxAttachmentStore.Delete(_attachmentRoot, item.Id);
            }
            else
            {
                await _db.UpdateOutboxAsync(item with
                {
                    CopyAttempts = attempts,
                    NextAttemptAt = _clock() + RetryPolicy.Delay(attempts),
                    LastError = failure.Message,
                    UpdatedAt = _clock()
                }, CancellationToken.None).ConfigureAwait(false);
            }
        }
    }

    private async Task FailAsync(OutboxEntity item, OutgoingMailMessage? payload, FailureInfo failure, CancellationToken ct)
    {
        item = item with
        {
            Status = OutboxStatus.Failed,
            LastError = failure.Message,
            IsPermanent = failure.IsPermanent,
            NextAttemptAt = null,
            UpdatedAt = _clock()
        };
        await _db.UpdateOutboxAsync(item, ct).ConfigureAwait(false);
        Raise(new OutboxResult(item, payload, Sent: false, failure, WillRetry: false));
    }

    private async Task InsertLocalCopyAsync(OutboxEntity item, OutgoingMailMessage message, AccountEntity account, FolderEntity? sentFolder, DateTimeOffset date, CancellationToken ct)
    {
        if (sentFolder is null)
        {
            return;
        }

        var text = message.BodyText ?? OutgoingMimeBuilder.HtmlToText(message.BodyHtml ?? string.Empty);
        var preview = text.Replace('\r', ' ').Replace('\n', ' ').Trim();
        if (preview.Length > 140)
        {
            preview = preview[..140];
        }

        var id = LocalCopyId(item.Id);
        var entity = new MessageEntity(
            id, account.Id, sentFolder.Id, null, item.RfcMessageId ?? message.MessageId, null,
            message.Subject, account.EmailAddress, account.DisplayName, preview, text, message.BodyHtml,
            date, date, true, false, false, message.AttachmentPaths.Count > 0, null, (message.BodyHtml ?? string.Empty).Length);
        var recipients = new List<RecipientEntity>();
        recipients.AddRange(message.To.Select(a => new RecipientEntity(0, id, RecipientKind.To, a, null)));
        recipients.AddRange(message.Cc.Select(a => new RecipientEntity(0, id, RecipientKind.Cc, a, null)));
        recipients.AddRange(message.Bcc.Select(a => new RecipientEntity(0, id, RecipientKind.Bcc, a, null)));
        var attachments = message.AttachmentPaths.Select(p => new AttachmentEntity(Guid.NewGuid().ToString("N"), id, Path.GetFileName(p), "application/octet-stream", FileSize(p), null, false, null));
        await _db.UpsertLocalSentCopyAsync(entity, recipients, attachments, ct).ConfigureAwait(false);
    }

    private static long FileSize(string path)
    {
        try
        {
            return new FileInfo(path).Length;
        }
        catch (IOException)
        {
            return 0;
        }
    }

    public static string LocalCopyId(string outboxId) => "sent:" + outboxId;

    private async Task<FolderEntity?> FindSentFolderAsync(Guid accountId, CancellationToken ct)
        => (await _db.ListFoldersAsync(accountId, ct).ConfigureAwait(false)).FirstOrDefault(f => f.Special == SpecialFolder.Sent);

    private async Task<AccountEntity?> FindAccountAsync(Guid id, CancellationToken ct)
        => (await _db.ListAccountsAsync(ct).ConfigureAwait(false)).FirstOrDefault(a => a.Id == id);

    private static OutgoingMailMessage? TryParse(string json)
    {
        try
        {
            return JsonSerializer.Deserialize(json, MailClientJsonContext.Default.OutgoingMailMessage);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private void Raise(OutboxResult result) => ItemProcessed?.Invoke(this, result);
}
