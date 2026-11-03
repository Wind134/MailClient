using MailClient.Core;
using MailClient.Core.Sending;
using MailClient.Providers.Imap;
using MailClient.Providers.Outbox;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Net.Smtp;
using MailKit.Search;
using MailKit.Security;
using MimeKit;
using SpecialFolder = MailClient.Core.SpecialFolder;

namespace MailClient.Providers;

// 发信（SMTP）、保存到已发送（APPEND）、查重，以及已读 / 星标 / 移动 / 删除的服务器写回。
public sealed partial class GenericImapProvider
{
    /// <summary>通过 SMTP 发送已构造好的 MIME（每次新建连接；XOAUTH2 或密码认证）。</summary>
    public async Task SendMimeAsync(MimeMessage mime, CancellationToken cancellationToken = default)
    {
        using var smtp = new SmtpClient { Timeout = 1000 * 60 };

        await smtp.ConnectAsync(
                _config.Smtp.Host,
                _config.Smtp.Port,
                MailKitSocketOptions.ToMailKit(_config.Smtp.SocketOptions),
                cancellationToken)
            .ConfigureAwait(false);

        try
        {
            if (_tokenSource is not null)
            {
                var token = await _tokenSource.GetAccessTokenAsync(cancellationToken).ConfigureAwait(false);
                await smtp.AuthenticateAsync(new SaslMechanismOAuth2(token.UserName, token.AccessToken), cancellationToken)
                    .ConfigureAwait(false);
            }
            else
            {
                var secrets = await ResolveSecretsAsync(cancellationToken).ConfigureAwait(false);
                await smtp.AuthenticateAsync(secrets.UserName, secrets.Password, cancellationToken).ConfigureAwait(false);
            }

            try
            {
                await smtp.SendAsync(mime, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (
                !cancellationToken.IsCancellationRequested &&
                (ex is IOException or TimeoutException or SmtpProtocolException or System.Net.Sockets.SocketException or OperationCanceledException))
            {
                // DATA 阶段连接中断：服务器可能已经收下了邮件
                throw new SendOutcomeUnknownException(ex);
            }
        }
        finally
        {
            if (smtp.IsConnected)
            {
                try
                {
                    await smtp.DisconnectAsync(true, CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception)
                {
                    // 邮件已发出后 QUIT 失败不应影响结果
                }
            }
        }
    }

    private async Task<IMailFolder?> FindSentFolderCoreAsync(ImapClient client, string? knownPath, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(knownPath))
        {
            return await client.GetFolderAsync(knownPath, ct).ConfigureAwait(false);
        }

        try
        {
            var special = client.GetFolder(MailKit.SpecialFolder.Sent);
            if (special is not null)
            {
                return special;
            }
        }
        catch (NotSupportedException)
        {
        }

        var personal = client.GetFolder(client.PersonalNamespaces[0]);
        var all = new List<MailClient.Core.MailFolder>();
        await WalkFoldersAsync(personal, all, ct).ConfigureAwait(false);
        var sent = all.FirstOrDefault(f => f.Special == SpecialFolder.Sent);
        return sent is null ? null : await client.GetFolderAsync(sent.RemotePath ?? sent.Id, ct).ConfigureAwait(false);
    }

    /// <summary>把副本追加到服务器“已发送”（带 \Seen）。找不到已发送文件夹时抛 NotFound 类失败。返回新邮件 UID（服务器支持 UIDPLUS 时）。</summary>
    public async Task<long?> AppendToSentAsync(MimeMessage mime, string? sentFolderPath, DateTimeOffset date, CancellationToken cancellationToken = default)
    {
        await EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var client = _imap ?? throw new InvalidOperationException("IMAP 未连接。");
            var sent = await FindSentFolderCoreAsync(client, sentFolderPath, cancellationToken).ConfigureAwait(false)
                ?? throw new ClassifiedFailureException(FailureKind.NotFound, "找不到服务器上的“已发送”文件夹。");
            var uid = await sent.AppendAsync(mime, MessageFlags.Seen, date, cancellationToken).ConfigureAwait(false);
            return uid is { IsValid: true } ? uid.Value.Id : null;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>用 Message-ID 在“已发送”里查这封邮件是否已经存在。</summary>
    public async Task<SentLookup> FindInSentByMessageIdAsync(string rfcMessageId, string? sentFolderPath, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(rfcMessageId))
        {
            return SentLookup.Unknown;
        }

        await EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var client = _imap ?? throw new InvalidOperationException("IMAP 未连接。");
            var sent = await FindSentFolderCoreAsync(client, sentFolderPath, cancellationToken).ConfigureAwait(false);
            if (sent is null)
            {
                return SentLookup.Unknown;
            }

            if (!sent.IsOpen)
            {
                await sent.OpenAsync(FolderAccess.ReadOnly, cancellationToken).ConfigureAwait(false);
            }

            var hits = await sent.SearchAsync(
                SearchQuery.HeaderContains("Message-ID", rfcMessageId.Trim().Trim('<', '>')), cancellationToken).ConfigureAwait(false);
            return hits.Count > 0 ? SentLookup.Found : SentLookup.NotFound;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>当前 UIDVALIDITY（同步管线用来检测服务器重建了文件夹）。</summary>
    public async Task<long?> GetUidValidityAsync(string folderPath, CancellationToken cancellationToken = default)
    {
        await EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var client = _imap ?? throw new InvalidOperationException("IMAP 未连接。");
            var folder = await OpenFolderAsync(client, folderPath, cancellationToken).ConfigureAwait(false);
            return folder.UidValidity;
        }
        finally
        {
            _gate.Release();
        }
    }

    private static async Task<IMailFolder> OpenForWriteAsync(ImapClient client, string path, long? expectedUidValidity, CancellationToken ct)
    {
        IMailFolder folder = string.Equals(path, "INBOX", StringComparison.OrdinalIgnoreCase)
            ? client.Inbox
            : await client.GetFolderAsync(path, ct).ConfigureAwait(false);
        if (!folder.IsOpen || folder.Access != FolderAccess.ReadWrite)
        {
            await folder.OpenAsync(FolderAccess.ReadWrite, ct).ConfigureAwait(false);
        }

        if (expectedUidValidity is { } expected && folder.UidValidity != expected)
        {
            throw new ClassifiedFailureException(
                FailureKind.UidValidityChanged, "服务器上的文件夹已被重建（UIDVALIDITY 变化），旧的本地操作已丢弃。");
        }

        return folder;
    }

    /// <summary>按 UID 设置 \Seen / \Flagged（null = 不改）。需要 expectedUidValidity 时校验。</summary>
    public async Task SetFlagsAsync(
        string folderPath, long uid, long? expectedUidValidity, bool? seen, bool? flagged, CancellationToken cancellationToken = default)
    {
        await EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var client = _imap ?? throw new InvalidOperationException("IMAP 未连接。");
            var folder = await OpenForWriteAsync(client, folderPath, expectedUidValidity, cancellationToken).ConfigureAwait(false);
            var ids = new List<UniqueId> { new((uint)uid) };
            if (seen is { } s)
            {
                if (s)
                {
                    await folder.AddFlagsAsync(ids, MessageFlags.Seen, true, cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    await folder.RemoveFlagsAsync(ids, MessageFlags.Seen, true, cancellationToken).ConfigureAwait(false);
                }
            }

            if (flagged is { } f)
            {
                if (f)
                {
                    await folder.AddFlagsAsync(ids, MessageFlags.Flagged, true, cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    await folder.RemoveFlagsAsync(ids, MessageFlags.Flagged, true, cancellationToken).ConfigureAwait(false);
                }
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// 移动到另一个文件夹：服务器支持 MOVE（RFC 6851）就用 MOVE，否则 COPY + STORE \Deleted + EXPUNGE
    /// （有 UIDPLUS 时只 UID EXPUNGE 这一封，没有则普通 EXPUNGE）。返回目标文件夹里的新 UID（未知则 null）。
    /// </summary>
    public async Task<long?> MoveMessageAsync(
        string sourcePath, long uid, long? expectedUidValidity, string destinationPath, CancellationToken cancellationToken = default)
    {
        await EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var client = _imap ?? throw new InvalidOperationException("IMAP 未连接。");
            var folder = await OpenForWriteAsync(client, sourcePath, expectedUidValidity, cancellationToken).ConfigureAwait(false);
            var destination = string.Equals(destinationPath, "INBOX", StringComparison.OrdinalIgnoreCase)
                ? client.Inbox
                : await client.GetFolderAsync(destinationPath, cancellationToken).ConfigureAwait(false);
            var id = new UniqueId((uint)uid);
            UniqueId? result;
            if (client.Capabilities.HasFlag(ImapCapabilities.Move))
            {
                result = await folder.MoveToAsync(id, destination, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                result = await folder.CopyToAsync(id, destination, cancellationToken).ConfigureAwait(false);
                var ids = new List<UniqueId> { id };
                await folder.AddFlagsAsync(ids, MessageFlags.Deleted, true, cancellationToken).ConfigureAwait(false);
                if (client.Capabilities.HasFlag(ImapCapabilities.UidPlus))
                {
                    await folder.ExpungeAsync(ids, cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    await folder.ExpungeAsync(cancellationToken).ConfigureAwait(false);
                }
            }

            return result is { IsValid: true } ? result.Value.Id : null;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>永久删除：STORE +FLAGS \Deleted，然后 EXPUNGE（有 UIDPLUS 时只清这一封）。</summary>
    public async Task DeleteMessageAsync(string folderPath, long uid, long? expectedUidValidity, CancellationToken cancellationToken = default)
    {
        await EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var client = _imap ?? throw new InvalidOperationException("IMAP 未连接。");
            var folder = await OpenForWriteAsync(client, folderPath, expectedUidValidity, cancellationToken).ConfigureAwait(false);
            var ids = new List<UniqueId> { new((uint)uid) };
            await folder.AddFlagsAsync(ids, MessageFlags.Deleted, true, cancellationToken).ConfigureAwait(false);
            if (client.Capabilities.HasFlag(ImapCapabilities.UidPlus))
            {
                await folder.ExpungeAsync(ids, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await folder.ExpungeAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            _gate.Release();
        }
    }
}
