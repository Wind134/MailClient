using MailClient.Core;
using MailClient.Core.Services;
using MailClient.Providers.Setup;
using MailClient.Storage;

namespace MailClient.Providers.Services;

/// <summary>按需从 IMAP / Graph 拉取正文并写入本地缓存。</summary>
public sealed class MessageBodyService : IMessageBodyLoader
{
    private readonly MailDatabase _db;
    private readonly AccountProviderFactory _providers;

    public MessageBodyService(MailDatabase database, AccountProviderFactory providers)
    {
        _db = database ?? throw new ArgumentNullException(nameof(database));
        _providers = providers ?? throw new ArgumentNullException(nameof(providers));
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
            // 旧缓存可能仍留着 cid: 内嵌图引用：再拉一次以改写成 data URI
            if (!CidHtmlRewriter.ContainsUnresolvedCid(message.BodyHtml))
            {
                return;
            }
        }

        var account = await _db.GetAccountAsync(message.AccountId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("账号不存在。");

        await using var handle = await _providers.OpenAsync(account, cancellationToken).ConfigureAwait(false);
        MessageBodyContent content;
        if (handle.Imap is { } imap)
        {
            var folder = await _db.GetFolderAsync(message.FolderId, cancellationToken).ConfigureAwait(false);
            if (folder?.RemotePath is null || message.RemoteUid is null)
            {
                throw new InvalidOperationException("缺少远端文件夹或 UID，无法拉取正文。");
            }

            content = await imap.FetchBodyAsync(folder.RemotePath, message.RemoteUid.Value, cancellationToken)
                .ConfigureAwait(false);
        }
        else if (handle.Graph is { } graph)
        {
            var graphId = ExtractGraphId(message.Id, message.AccountId);
            content = await graph.FetchBodyAsync(graphId, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            throw new InvalidOperationException("此账号类型暂不支持拉取正文。");
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

        await using var handle = await _providers.OpenAsync(account, cancellationToken).ConfigureAwait(false);
        if (handle.Imap is { } imap)
        {
            var folder = await _db.GetFolderAsync(message.FolderId, cancellationToken).ConfigureAwait(false);
            if (folder?.RemotePath is null || message.RemoteUid is null)
            {
                throw new InvalidOperationException("缺少远端文件夹或 UID，无法下载附件。");
            }

            await imap.DownloadAttachmentAsync(folder.RemotePath, message.RemoteUid.Value, attachment.FileName, destinationPath, cancellationToken)
                .ConfigureAwait(false);
        }
        else if (handle.Graph is { } graph)
        {
            if (string.IsNullOrEmpty(attachment.RemoteId))
            {
                throw new InvalidOperationException("缺少 Graph 附件 Id，请先重新打开邮件加载附件列表。");
            }

            var graphId = ExtractGraphId(message.Id, message.AccountId);
            await graph.DownloadAttachmentAsync(graphId, attachment.RemoteId, destinationPath, cancellationToken)
                .ConfigureAwait(false);
        }
        else
        {
            throw new InvalidOperationException("此账号类型暂不支持下载附件。");
        }

        await _db.SetAttachmentLocalPathAsync(attachmentId, destinationPath, cancellationToken).ConfigureAwait(false);
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
