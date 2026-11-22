using System.Collections.Concurrent;
using MailClient.Core;
using MailClient.Core.Auth;
using MailClient.Core.Services;
using MailClient.Providers.Setup;
using MailClient.Providers.Sync;
using MailClient.Storage;

namespace MailClient.Providers.Services;

/// <summary>按需从 IMAP / Graph 拉取正文并写入本地缓存。</summary>
public sealed class MessageBodyService : IMessageBodyLoader
{
    private readonly MailDatabase _db;
    private readonly AccountProviderFactory _providers;
    private readonly ReloginTracker? _relogin;
    private readonly ConcurrentDictionary<string, byte> _garbledRefetched = new(StringComparer.Ordinal);

    /// <param name="relogin">拉正文 / 附件时遇到登录失效，就标记账号需要重新登录。</param>
    public MessageBodyService(MailDatabase database, AccountProviderFactory providers, ReloginTracker? relogin = null)
    {
        _db = database ?? throw new ArgumentNullException(nameof(database));
        _providers = providers ?? throw new ArgumentNullException(nameof(providers));
        _relogin = relogin;
    }

    /// <summary>异常过滤器：认证失败时记一笔，但从不捕获（异常照常抛给调用方）。</summary>
    private bool ReportIfAuthFailure(Guid accountId, Exception ex)
    {
        if (_relogin is not null && ex is not OperationCanceledException && ConnectionFailureClassifier.IsAuthFailure(ex))
        {
            _relogin.ReportAuthFailure(accountId);
        }

        return false;
    }

    public async Task EnsureLoadedAsync(string messageId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(messageId);
        var message = await _db.GetMessageAsync(messageId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("邮件不存在或已删除。");

        // 本地草稿：正文已在库里，补上标记即可（不走服务器、不解析 cid）
        if (messageId.StartsWith("draft:", StringComparison.OrdinalIgnoreCase))
        {
            if (!message.BodyFetched)
            {
                await _db.SaveMessageBodyAsync(
                    messageId,
                    new MessageBodyContent(message.BodyText ?? string.Empty, message.BodyHtml, Array.Empty<MessageAttachmentInfo>()),
                    cancellationToken).ConfigureAwait(false);
            }

            return;
        }

        if (message.BodyFetched)
        {
            // 旧缓存可能仍留着 cid: 内嵌图引用，或升级前被误按 UTF-8 解码成乱码的正文/附件名：自动重拉一次覆盖旧缓存
            var needsCid = CidHtmlRewriter.ContainsUnresolvedCid(message.BodyHtml);
            var needsGarbled = false;
            if (!needsCid)
            {
                var garbled = GarbledText.LooksGarbled(message.BodyText, message.BodyHtml) ||
                              GarbledText.LooksGarbled(message.Preview, null);
                if (!garbled && message.HasAttachments)
                {
                    var cachedAtts = await _db.ListAttachmentsAsync(messageId, cancellationToken).ConfigureAwait(false);
                    garbled = GarbledText.HasGarbledFileName(cachedAtts.Select(a => a.FileName));
                }

                needsGarbled = garbled && _garbledRefetched.TryAdd(messageId, 0);
            }

            if (!needsCid && !needsGarbled)
            {
                return;
            }
        }

        var account = await _db.GetAccountAsync(message.AccountId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("账号不存在。");

        try
        {
            await FetchAndSaveBodyAsync(account, message, messageId, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ReportIfAuthFailure(account.Id, ex))
        {
            throw;
        }
    }

    private async Task FetchAndSaveBodyAsync(AccountEntity account, MessageEntity message, string messageId, CancellationToken cancellationToken)
    {
        await using var handle = await _providers.OpenAsync(account, cancellationToken).ConfigureAwait(false);
        MessageBodyContent content;
        if (handle.Kind == ProviderKind.Imap)
        {
            var imap = handle.Imap;
            var folder = await _db.GetFolderAsync(message.FolderId, cancellationToken).ConfigureAwait(false);
            if (folder?.RemotePath is null || message.RemoteUid is null)
            {
                throw new InvalidOperationException("缺少远端文件夹或 UID，无法拉取正文。");
            }

            content = await imap.FetchBodyAsync(folder.RemotePath, message.RemoteUid.Value, cancellationToken)
                .ConfigureAwait(false);
        }
        else
        {
            var graphId = ExtractGraphId(message.Id, message.AccountId);
            content = await handle.Graph.FetchBodyAsync(graphId, cancellationToken).ConfigureAwait(false);
        }

        if (content.BodyHtml is not null && CidHtmlRewriter.ContainsUnresolvedCid(content.BodyHtml))
        {
            var rewritten = CidHtmlRewriter.Rewrite(content.BodyHtml, content.Attachments);
            content = content with { BodyHtml = rewritten };
        }

        await _db.SaveMessageBodyAsync(messageId, content, cancellationToken).ConfigureAwait(false);
    }

    public async Task DownloadAttachmentAsync(string attachmentId, string destinationPath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(attachmentId);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);

        var attachment = await _db.GetAttachmentAsync(attachmentId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("附件不存在。");

        if (!string.IsNullOrEmpty(attachment.LocalPath) && File.Exists(attachment.LocalPath))
        {
            if (!string.Equals(attachment.LocalPath, destinationPath, StringComparison.OrdinalIgnoreCase))
            {
                File.Copy(attachment.LocalPath, destinationPath, overwrite: true);
            }

            return;
        }

        var message = await _db.GetMessageAsync(attachment.MessageId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("附件所属邮件不存在。");
        var account = await _db.GetAccountAsync(message.AccountId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("账号不存在。");

        try
        {
            await DownloadFromServerAsync(account, message, attachment, destinationPath, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ReportIfAuthFailure(account.Id, ex))
        {
            throw;
        }

        await _db.SetAttachmentLocalPathAsync(attachmentId, destinationPath, cancellationToken).ConfigureAwait(false);
    }

    private async Task DownloadFromServerAsync(
        AccountEntity account, MessageEntity message, AttachmentEntity attachment, string destinationPath, CancellationToken cancellationToken)
    {
        await using var handle = await _providers.OpenAsync(account, cancellationToken).ConfigureAwait(false);
        if (handle.Kind == ProviderKind.Imap)
        {
            var imap = handle.Imap;
            var folder = await _db.GetFolderAsync(message.FolderId, cancellationToken).ConfigureAwait(false);
            if (folder?.RemotePath is null || message.RemoteUid is null)
            {
                throw new InvalidOperationException("缺少远端文件夹或 UID，无法下载附件。");
            }

            await imap.DownloadAttachmentAsync(folder.RemotePath, message.RemoteUid.Value, attachment.FileName, destinationPath, cancellationToken)
                .ConfigureAwait(false);
        }
        else
        {
            if (string.IsNullOrEmpty(attachment.RemoteId))
            {
                throw new InvalidOperationException("缺少 Graph 附件 Id，请先重新打开邮件加载附件列表。");
            }

            var graphId = ExtractGraphId(message.Id, message.AccountId);
            await handle.Graph.DownloadAttachmentAsync(graphId, attachment.RemoteId, destinationPath, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    internal static string ExtractGraphId(string localMessageId, Guid accountId)
    {
        var prefix = accountId.ToString("D") + ":";
        if (!localMessageId.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("本地邮件 Id 与账号不匹配。");
        }

        return localMessageId[prefix.Length..];
    }
}
