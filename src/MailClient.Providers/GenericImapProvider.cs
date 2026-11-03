using MailClient.Auth;
using MailClient.Core;
using MailClient.Core.Providers;
using MailClient.Providers.Imap;
using MailKit;
using MailFolder = MailClient.Core.MailFolder;
using SpecialFolder = MailClient.Core.SpecialFolder;
using MailKit.Net.Imap;
using MailKit.Net.Smtp;
using MailKit.Search;
using MailKit.Security;
using MimeKit;

namespace MailClient.Providers;

/// <summary>
/// 通用 IMAP/SMTP 提供商（MailKit）。支持用户名+密码/授权码，
/// 也支持由 <see cref="IOAuthTokenSource"/> 提供访问令牌的 XOAUTH2（<see cref="GmailProvider"/> 复用）。
/// </summary>
public sealed partial class GenericImapProvider : IMailProvider, IAsyncDisposable
{
    private readonly GenericImapAccountConfig _config;
    private readonly ICredentialStore? _credentialStore;
    private readonly ImapAccountSecrets? _explicitSecrets;
    private readonly IOAuthTokenSource? _tokenSource;
    private readonly string _providerId = "imap.generic";
    private readonly bool _useGmailCategories;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private ImapClient? _imap;
    private bool _quickResyncEnabled;
    private IReadOnlyList<MailFolder>? _folderCache;

    public string ProviderId => _providerId;

    public Guid AccountId => _config.AccountId;

    public GenericImapProvider(
        GenericImapAccountConfig config,
        ICredentialStore credentialStore)
        : this(config, credentialStore, explicitSecrets: null)
    {
    }

    /// <summary>测试或调用方已持有明文凭据时使用。</summary>
    public GenericImapProvider(
        GenericImapAccountConfig config,
        ImapAccountSecrets explicitSecrets)
        : this(config, credentialStore: null, explicitSecrets)
    {
    }

    public GenericImapProvider(
        GenericImapAccountConfig config,
        ICredentialStore? credentialStore,
        ImapAccountSecrets? explicitSecrets)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _credentialStore = credentialStore;
        _explicitSecrets = explicitSecrets;

        if (_credentialStore is null && _explicitSecrets is null)
        {
            throw new ArgumentException("需要 ICredentialStore 或显式 ImapAccountSecrets。");
        }
    }

    /// <summary>
    /// OAuth（XOAUTH2）构造：每次连接 / 发信都向 <paramref name="tokenSource"/> 取最新访问令牌（它负责在过期前刷新）。
    /// </summary>
    public GenericImapProvider(
        GenericImapAccountConfig config,
        IOAuthTokenSource tokenSource,
        string providerId = "imap.generic",
        bool useGmailCategories = false)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _tokenSource = tokenSource ?? throw new ArgumentNullException(nameof(tokenSource));
        ArgumentException.ThrowIfNullOrWhiteSpace(providerId);
        _providerId = providerId;
        _useGmailCategories = useGmailCategories;
    }

    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_imap is { IsConnected: true, IsAuthenticated: true })
            {
                return;
            }

            await DisposeImapAsync().ConfigureAwait(false);

            var client = new ImapClient { Timeout = 1000 * 60 };
            try
            {
                await client.ConnectAsync(
                        _config.Imap.Host,
                        _config.Imap.Port,
                        MailKitSocketOptions.ToMailKit(_config.Imap.SocketOptions),
                        cancellationToken)
                    .ConfigureAwait(false);

                if (_tokenSource is not null)
                {
                    var token = await _tokenSource.GetAccessTokenAsync(cancellationToken).ConfigureAwait(false);
                    await client.AuthenticateAsync(
                            new SaslMechanismOAuth2(token.UserName, token.AccessToken),
                            cancellationToken)
                        .ConfigureAwait(false);
                }
                else
                {
                    var secrets = await ResolveSecretsAsync(cancellationToken).ConfigureAwait(false);
                    await client.AuthenticateAsync(secrets.UserName, secrets.Password, cancellationToken)
                        .ConfigureAwait(false);
                }

                await IdentifyClientAsync(client, cancellationToken).ConfigureAwait(false);
                await EnableQuickResyncAsync(client, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                client.Dispose();
                throw;
            }

            _imap = client;
            _folderCache = null;
            _quickResyncEnabled = client.Capabilities.HasFlag(ImapCapabilities.QuickResync) && _quickResyncPending;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// 登录成功后发送 IMAP ID 命令（RFC 2971）。163 / 126 / yeah.net 等网易邮箱要求客户端先自报家门，
    /// 否则 SELECT 会失败并提示 “Unsafe Login”。其他服务器多数忽略该命令；失败不影响登录结果。
    /// </summary>
    private static async Task IdentifyClientAsync(ImapClient client, CancellationToken cancellationToken)
    {
        if (!client.Capabilities.HasFlag(ImapCapabilities.Id))
        {
            return;
        }

        try
        {
            await client.IdentifyAsync(
                    new ImapImplementation
                    {
                        Name = "MailClient",
                        Version = "1.0.0",
                        Vendor = "MailClient"
                    },
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // best effort
        }
    }

    /// <summary>添加账号向导用：连接 + 登录（含 ID）+ 打开收件箱，确保网易的 “Unsafe Login” 在此处暴露。</summary>
    public async Task VerifyImapAsync(CancellationToken cancellationToken = default)
    {
        await ConnectAsync(cancellationToken).ConfigureAwait(false);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var client = _imap ?? throw new InvalidOperationException("IMAP 未连接。");
            var inbox = client.Inbox;
            await inbox.OpenAsync(FolderAccess.ReadOnly, cancellationToken).ConfigureAwait(false);
            await inbox.CloseAsync(false, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>添加账号向导用：只连接并登录 SMTP，不发信。</summary>
    public async Task VerifySmtpAsync(CancellationToken cancellationToken = default)
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
        }
        finally
        {
            if (smtp.IsConnected)
            {
                try
                {
                    await smtp.DisconnectAsync(true, CancellationToken.None).ConfigureAwait(false);
                }
                catch
                {
                    // ignore
                }
            }
        }
    }

    public async Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await DisposeImapAsync().ConfigureAwait(false);
            _folderCache = null;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<MailFolder>> GetFoldersAsync(CancellationToken cancellationToken = default)
    {
        await EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var client = _imap ?? throw new InvalidOperationException("IMAP 未连接。");
            var personal = client.GetFolder(client.PersonalNamespaces[0]);
            var folders = new List<MailFolder>();
            await WalkFoldersAsync(personal, folders, cancellationToken).ConfigureAwait(false);

            // 确保 INBOX 在列表中（部分服务器 PersonalNamespaces 下列举不到）
            if (!folders.Any(f => f.Special == SpecialFolder.Inbox))
            {
                var inbox = client.Inbox;
                folders.Insert(0, ToMailFolder(inbox));
            }

            folders.Sort((a, b) =>
            {
                var oa = FolderSpecialMapper.DefaultSortOrder(a.Special);
                var ob = FolderSpecialMapper.DefaultSortOrder(b.Special);
                var cmp = oa.CompareTo(ob);
                return cmp != 0 ? cmp : string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
            });

            _folderCache = folders;
            return folders;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<MailMessageSummary>> SyncFolderAsync(
        string folderId,
        CancellationToken cancellationToken = default)
    {
        var items = await SyncFolderDetailedAsync(
                folderId,
                maxCount: null,
                afterUidExclusive: null,
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        return items.Select(i => i.Summary).ToList();
    }

    /// <summary>UID 增量友好的详细同步：返回可落库的 <see cref="MailMessageSyncItem"/>。</summary>
    public async Task<IReadOnlyList<MailMessageSyncItem>> SyncFolderDetailedAsync(
        string folderId,
        int? maxCount = null,
        long? afterUidExclusive = null,
        CancellationToken cancellationToken = default)
    {
        await EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var client = _imap ?? throw new InvalidOperationException("IMAP 未连接。");
            var folder = await OpenFolderAsync(client, folderId, cancellationToken).ConfigureAwait(false);

            IList<UniqueId> uids;
            if (afterUidExclusive is > 0)
            {
                var min = new UniqueId((uint)afterUidExclusive.Value + 1);
                uids = await folder.SearchAsync(SearchQuery.Uids(new UniqueIdRange(min, UniqueId.MaxValue)), cancellationToken)
                    .ConfigureAwait(false);
                // “min:*” 在 min 大于最大 UID 时，服务器会把最后一封邮件也返回来——这里滤掉，只留真正更新的
                uids = uids.Where(u => u.Id > afterUidExclusive.Value).ToList();
            }
            else
            {
                uids = await folder.SearchAsync(SearchQuery.All, cancellationToken).ConfigureAwait(false);
            }

            // 列表通常要最新的：取 UID 末尾一批
            var batch = maxCount ?? _config.SyncBatchSize;
            if (uids.Count > batch)
            {
                uids = uids.Skip(Math.Max(0, uids.Count - batch)).ToList();
            }

            if (uids.Count == 0)
            {
                return Array.Empty<MailMessageSyncItem>();
            }

            var summaries = await folder.FetchAsync(
                    uids,
                    MessageSummaryItems.UniqueId |
                    MessageSummaryItems.Envelope |
                    MessageSummaryItems.Flags |
                    MessageSummaryItems.BodyStructure |
                    MessageSummaryItems.InternalDate |
                    MessageSummaryItems.Size |
                    MessageSummaryItems.Headers |
                    (client.Capabilities.HasFlag(ImapCapabilities.GMailExt1)
                        ? MessageSummaryItems.GMailMessageId | MessageSummaryItems.GMailThreadId
                        : MessageSummaryItems.None),
                    cancellationToken)
                .ConfigureAwait(false);

            var gmailCategories = await TryDetectGmailCategoriesAsync(client, folder, uids, cancellationToken)
                .ConfigureAwait(false);

            var results = new List<MailMessageSyncItem>(summaries.Count);
            foreach (var summary in summaries.OrderByDescending(s => s.UniqueId.Id))
            {
                var item = await ToSyncItemAsync(folder, folderId, summary, cancellationToken)
                    .ConfigureAwait(false);
                if (gmailCategories is not null && gmailCategories.TryGetValue(summary.UniqueId.Id, out var category))
                {
                    item = item with { Category = category };
                }

                results.Add(item);
            }

            return results;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Gmail：用 X-GM-RAW（<c>category:promotions</c> 等）按批判定标签页分类，返回 UID → 分类。
    /// 不支持 X-GM-EXT-1、非收件箱/所有邮件、或任何失败时返回 null（由规则分类器兜底）。
    /// </summary>
    private async Task<Dictionary<uint, MailCategory>?> TryDetectGmailCategoriesAsync(
        ImapClient client,
        IMailFolder folder,
        IList<UniqueId> uids,
        CancellationToken cancellationToken)
    {
        if (!_useGmailCategories || !client.Capabilities.HasFlag(ImapCapabilities.GMailExt1))
        {
            return null;
        }

        var special = FolderSpecialMapper.Map(folder.Attributes, folder.FullName, folder.Name);
        if (special is not (SpecialFolder.Inbox or SpecialFolder.Archive))
        {
            return null;
        }

        try
        {
            var byCategory = new Dictionary<string, HashSet<uint>>(StringComparer.OrdinalIgnoreCase);
            foreach (var name in GmailCategoryMapper.QueriedCategories)
            {
                var query = SearchQuery.Uids(uids).And(SearchQuery.GMailRawSearch($"category:{name}"));
                var hits = await folder.SearchAsync(query, cancellationToken).ConfigureAwait(false);
                byCategory[name] = hits.Select(u => u.Id).ToHashSet();
            }

            var result = new Dictionary<uint, MailCategory>();
            foreach (var uid in uids)
            {
                result[uid.Id] = GmailCategoryMapper.Resolve(uid.Id, byCategory);
            }

            return result;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return null;
        }
    }

    public async Task SendAsync(OutgoingMailMessage message, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);

        using var mime = Outbox.OutgoingMimeBuilder.Build(
            message, _config.EmailAddress, _config.DisplayName, DateTimeOffset.Now);
        await SendMimeAsync(mime, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        await DisconnectAsync().ConfigureAwait(false);
        _gate.Dispose();
    }

    private async Task EnsureConnectedAsync(CancellationToken cancellationToken)
    {
        if (_imap is { IsConnected: true, IsAuthenticated: true })
        {
            return;
        }

        await ConnectAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<ImapAccountSecrets> ResolveSecretsAsync(CancellationToken cancellationToken)
    {
        if (_explicitSecrets is not null)
        {
            return _explicitSecrets;
        }

        var vaultKey = _config.CredentialVaultKey
            ?? throw new InvalidOperationException("未配置 CredentialVaultKey，且无显式凭据。");

        var store = _credentialStore ?? throw new InvalidOperationException("缺少 ICredentialStore。");
        var packed = await store.GetSecretAsync(vaultKey, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrEmpty(packed))
        {
            throw new InvalidOperationException($"凭据库中找不到密钥：{vaultKey}");
        }

        // 格式：username\npassword 或 username:password（首个冒号分割）
        var nl = packed.IndexOf('\n');
        if (nl > 0)
        {
            return new ImapAccountSecrets(packed[..nl].Trim(), packed[(nl + 1)..].Trim());
        }

        var colon = packed.IndexOf(':');
        if (colon > 0)
        {
            return new ImapAccountSecrets(packed[..colon].Trim(), packed[(colon + 1)..].Trim());
        }

        // 仅密码：用户名回落到邮箱地址
        return new ImapAccountSecrets(_config.EmailAddress, packed.Trim());
    }

    private static async Task WalkFoldersAsync(
        IMailFolder root,
        List<MailFolder> sink,
        CancellationToken cancellationToken)
    {
        IList<IMailFolder> children;
        try
        {
            children = await root.GetSubfoldersAsync(false, cancellationToken).ConfigureAwait(false);
        }
        catch (NotSupportedException)
        {
            return;
        }

        foreach (var child in children)
        {
            if (child.Attributes.HasFlag(FolderAttributes.NonExistent))
            {
                continue;
            }

            if (!child.Attributes.HasFlag(FolderAttributes.NoSelect))
            {
                sink.Add(ToMailFolder(child));
            }

            // NoInferiors = 不能有子文件夹；其余情况尝试递归
            if (!child.Attributes.HasFlag(FolderAttributes.NoInferiors))
            {
                await WalkFoldersAsync(child, sink, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private static MailFolder ToMailFolder(IMailFolder folder)
    {
        var special = FolderSpecialMapper.Map(folder.Attributes, folder.FullName, folder.Name);
        var unread = folder.Unread < 0 ? 0 : folder.Unread;
        return new MailFolder(
            Id: folder.FullName,
            Name: string.IsNullOrWhiteSpace(folder.Name) ? folder.FullName : folder.Name,
            Special: special,
            RemotePath: folder.FullName,
            UnreadCount: unread);
    }

    private static async Task<IMailFolder> OpenFolderAsync(
        ImapClient client,
        string folderId,
        CancellationToken cancellationToken)
    {
        IMailFolder folder;
        if (string.Equals(folderId, "INBOX", StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(folderId))
        {
            folder = client.Inbox;
        }
        else
        {
            folder = await client.GetFolderAsync(folderId, cancellationToken).ConfigureAwait(false);
        }

        if (!folder.IsOpen)
        {
            await folder.OpenAsync(FolderAccess.ReadOnly, cancellationToken).ConfigureAwait(false);
        }

        return folder;
    }

    private async Task<MailMessageSyncItem> ToSyncItemAsync(
        IMailFolder folder,
        string folderId,
        IMessageSummary summary,
        CancellationToken cancellationToken)
    {
        var uid = summary.UniqueId.Id;
        var messageId = $"{_config.AccountId:D}:{folderId}:{uid}";
        var envelope = summary.Envelope;
        var subject = envelope?.Subject ?? string.Empty;
        var fromMailbox = envelope?.From?.Mailboxes.FirstOrDefault();
        var fromAddress = fromMailbox?.Address ?? string.Empty;
        var fromName = string.IsNullOrWhiteSpace(fromMailbox?.Name) ? null : fromMailbox!.Name;
        var fromDisplay = MimeMessageMapper.FormatFromDisplay(fromAddress, fromName);

        var flags = summary.Flags ?? MessageFlags.None;
        var isRead = flags.HasFlag(MessageFlags.Seen);
        var isFlagged = flags.HasFlag(MessageFlags.Flagged);
        var isDraft = flags.HasFlag(MessageFlags.Draft);

        var received = summary.InternalDate ?? envelope?.Date ?? DateTimeOffset.UtcNow;
        if (received.Offset == TimeSpan.Zero && received.DateTime.Kind == DateTimeKind.Unspecified)
        {
            received = new DateTimeOffset(DateTime.SpecifyKind(received.DateTime, DateTimeKind.Utc));
        }

        var hasAttachments = summary.Attachments?.Any() == true ||
                             summary.BodyParts?.OfType<BodyPartBasic>().Any(b =>
                                 b.ContentDisposition?.IsAttachment == true) == true;

        var content = await TryFetchContentAsync(folder, summary.UniqueId, cancellationToken)
            .ConfigureAwait(false);

        var mailSummary = new MailMessageSummary(
            Id: messageId,
            FolderId: folderId,
            Subject: subject,
            From: fromDisplay,
            ReceivedAt: received,
            IsRead: isRead,
            IsFlagged: isFlagged,
            Preview: content.Preview);

        return new MailMessageSyncItem(
            Summary: mailSummary,
            RemoteUid: uid,
            RfcMessageId: envelope?.MessageId,
            FromAddress: fromAddress,
            FromName: fromName,
            HasAttachments: hasAttachments || content.Attachments.Any(a => !a.IsInline),
            SizeBytes: summary.Size ?? 0,
            IsDraft: isDraft,
            SentAt: envelope?.Date,
            GmMsgId: summary.GMailMessageId?.ToString(),
            GmThrId: summary.GMailThreadId?.ToString(),
            BodyText: content.Fetched ? content.BodyText : null,
            BodyHtml: content.Fetched ? content.BodyHtml : null,
            BodyFetched: content.Fetched,
            Attachments: content.Fetched ? content.Attachments : null);
    }

    private async Task<(string Preview, string BodyText, string? BodyHtml, IReadOnlyList<MessageAttachmentInfo> Attachments, bool Fetched)> TryFetchContentAsync(
        IMailFolder folder,
        UniqueId uid,
        CancellationToken cancellationToken)
    {
        try
        {
            // BODY.PEEK（GetMessageAsync）避免标记已读；同步时本来就会取整封，顺带缓存正文。
            var mime = await folder.GetMessageAsync(uid, cancellationToken).ConfigureAwait(false);
            var extracted = MimeMessageMapper.ExtractContent(mime, _config.PreviewMaxChars);
            return (extracted.Preview, extracted.BodyText, extracted.BodyHtml, extracted.Attachments, true);
        }
        catch
        {
            return (string.Empty, string.Empty, null, Array.Empty<MessageAttachmentInfo>(), false);
        }
    }

    /// <summary>按需拉取一封邮件的完整正文与附件元数据。</summary>
    public async Task<MessageBodyContent> FetchBodyAsync(string remotePath, long uid, CancellationToken cancellationToken = default)
    {
        await EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var client = _imap ?? throw new InvalidOperationException("IMAP 未连接。");
            var folder = await OpenFolderAsync(client, remotePath, cancellationToken).ConfigureAwait(false);
            var mime = await folder.GetMessageAsync(new UniqueId((uint)uid), cancellationToken).ConfigureAwait(false);
            var extracted = MimeMessageMapper.ExtractContent(mime, _config.PreviewMaxChars);
            return new MessageBodyContent(extracted.BodyText, extracted.BodyHtml, extracted.Attachments);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>按文件名下载附件到目标路径。</summary>
    public async Task DownloadAttachmentAsync(string remotePath, long uid, string fileName, string destinationPath, CancellationToken cancellationToken = default)
    {
        await EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var client = _imap ?? throw new InvalidOperationException("IMAP 未连接。");
            var folder = await OpenFolderAsync(client, remotePath, cancellationToken).ConfigureAwait(false);
            var mime = await folder.GetMessageAsync(new UniqueId((uint)uid), cancellationToken).ConfigureAwait(false);
            var dir = Path.GetDirectoryName(Path.GetFullPath(destinationPath));
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            await using var fs = File.Create(destinationPath);
            if (!MimeMessageMapper.TryWriteAttachment(mime, fileName, fs))
            {
                throw new FileNotFoundException($"邮件中找不到附件：{fileName}");
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task DisposeImapAsync()
    {
        if (_imap is null)
        {
            return;
        }

        try
        {
            if (_imap.IsConnected)
            {
                await _imap.DisconnectAsync(true).ConfigureAwait(false);
            }
        }
        catch
        {
            // ignore disconnect errors
        }

        _imap.Dispose();
        _imap = null;
        _quickResyncEnabled = false;
    }
}
