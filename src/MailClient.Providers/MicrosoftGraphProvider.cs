using MailClient.Auth;
using MailClient.Core;
using MailClient.Core.Providers;
using MailClient.Providers.Graph;
using MailClient.Providers.Imap;
using Microsoft.Graph;
using Microsoft.Graph.Me.SendMail;
using Microsoft.Graph.Models;
using FileAttachment = Microsoft.Graph.Models.FileAttachment;
using MailFolder = MailClient.Core.MailFolder;

namespace MailClient.Providers;

/// <summary>
/// Outlook / Microsoft 365 邮件提供商（MSAL + Microsoft Graph）。
/// ProviderId = microsoft.graph
/// </summary>
public sealed class MicrosoftGraphProvider : IMailProvider, IGraphDeltaSource, IAsyncDisposable
{
    private readonly MicrosoftGraphAccountConfig _config;
    private readonly MicrosoftGraphTokenAcquirer _acquirer;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private GraphServiceClient? _client;
    private string? _resolvedEmail;
    private IReadOnlyList<MailFolder>? _folderCache;

    public string ProviderId => "microsoft.graph";

    public Guid AccountId => _config.AccountId;

    public MicrosoftGraphProvider(
        MicrosoftGraphAccountConfig config,
        ICredentialStore credentialStore)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _acquirer = new MicrosoftGraphTokenAcquirer(config, credentialStore);
    }

    /// <summary>测试注入：自定义 token 获取器。</summary>
    public MicrosoftGraphProvider(
        MicrosoftGraphAccountConfig config,
        MicrosoftGraphTokenAcquirer acquirer)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _acquirer = acquirer ?? throw new ArgumentNullException(nameof(acquirer));
    }

    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // 交互拿票并构建 Graph 客户端
            var token = await _acquirer.AcquireAsync(allowInteractive: true, cancellationToken)
                .ConfigureAwait(false);
            _resolvedEmail = token.AccountUsername ?? _config.EmailAddress;
            _client = CreateClient(allowInteractive: true);

            // 轻量探测：读 mailbox 设置失败也不阻断（权限不足时仍可能读邮件）
            try
            {
                var me = await _client.Me.GetAsync(r =>
                {
                    r.QueryParameters.Select = ["mail", "userPrincipalName", "displayName"];
                }, cancellationToken).ConfigureAwait(false);
                _resolvedEmail = me?.Mail ?? me?.UserPrincipalName ?? _resolvedEmail;
            }
            catch
            {
                // ignore probe errors
            }

            _folderCache = null;
        }
        finally
        {
            _gate.Release();
        }
    }

    public Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _client = null;
        _folderCache = null;
        // 保留 MSAL 缓存以便下次静默登录
        return Task.CompletedTask;
    }

    public async Task<IReadOnlyList<MailFolder>> GetFoldersAsync(CancellationToken cancellationToken = default)
    {
        var client = await EnsureClientAsync(cancellationToken).ConfigureAwait(false);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var folders = new List<MailFolder>();
            var page = await client.Me.MailFolders.GetAsync(r =>
            {
                r.QueryParameters.Select =
                [
                    "id", "displayName", "parentFolderId", "childFolderCount",
                    "unreadItemCount", "totalItemCount", "isHidden"
                ];
                r.QueryParameters.Top = 100;
            }, cancellationToken).ConfigureAwait(false);

            while (page is not null)
            {
                foreach (var f in page.Value ?? Enumerable.Empty<Microsoft.Graph.Models.MailFolder>())
                {
                    if (f.IsHidden == true)
                    {
                        continue;
                    }

                    folders.Add(ToMailFolder(f));
                }

                if (string.IsNullOrEmpty(page.OdataNextLink))
                {
                    break;
                }

                page = await client.Me.MailFolders
                    .WithUrl(page.OdataNextLink)
                    .GetAsync(cancellationToken: cancellationToken)
                    .ConfigureAwait(false);
            }

            folders.Sort((a, b) =>
            {
                var cmp = GraphFolderMapper.DefaultSortOrder(a.Special)
                    .CompareTo(GraphFolderMapper.DefaultSortOrder(b.Special));
                return cmp != 0
                    ? cmp
                    : string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
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
        var items = await SyncFolderDetailedAsync(folderId, maxCount: null, deltaLink: null, cancellationToken)
            .ConfigureAwait(false);
        return items.Items.Select(i => i.Summary).ToList();
    }

    /// <summary>
    /// 详细同步。若提供/返回 deltaLink，可做增量；否则按 receivedDateTime 拉取最近一批。
    /// </summary>
    public async Task<GraphFolderSyncResult> SyncFolderDetailedAsync(
        string folderId,
        int? maxCount = null,
        string? deltaLink = null,
        CancellationToken cancellationToken = default)
    {
        var client = await EnsureClientAsync(cancellationToken).ConfigureAwait(false);
        var batch = maxCount ?? _config.SyncBatchSize;

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_config.PreferDeltaSync)
            {
                try
                {
                    return await SyncWithDeltaAsync(client, folderId, batch, deltaLink, cancellationToken)
                        .ConfigureAwait(false);
                }
                catch
                {
                    // delta 不可用时回退列表
                }
            }

            return await SyncWithListAsync(client, folderId, batch, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SendAsync(OutgoingMailMessage message, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        var client = await EnsureClientAsync(cancellationToken).ConfigureAwait(false);
        await SendCoreAsync(client, message, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>后台（发件箱 / 写回）用：只做静默取票，绝不弹出登录窗口；令牌失效时抛认证类错误。</summary>
    public async Task SendInBackgroundAsync(OutgoingMailMessage message, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        var client = await EnsureBackgroundClientAsync(cancellationToken).ConfigureAwait(false);
        await SendCoreAsync(client, message, cancellationToken).ConfigureAwait(false);
    }

    private static async Task SendCoreAsync(GraphServiceClient client, OutgoingMailMessage message, CancellationToken cancellationToken)
    {
        var to = ToRecipients(message.To);
        var cc = ToRecipients(message.Cc);
        var bcc = ToRecipients(message.Bcc);
        if (to.Count + cc.Count + bcc.Count == 0)
        {
            throw new Core.Sending.ClassifiedFailureException(Core.Sending.FailureKind.RecipientRejected, "没有收件人。");
        }

        var graphMessage = new Message
        {
            Subject = message.Subject ?? string.Empty,
            Body = new ItemBody
            {
                ContentType = BodyType.Html,
                Content = message.BodyHtml ?? string.Empty
            },
            ToRecipients = to,
            CcRecipients = cc,
            BccRecipients = bcc
        };

        var attachments = await BuildAttachmentsAsync(message.AttachmentPaths, cancellationToken)
            .ConfigureAwait(false);
        if (attachments.Count > 0)
        {
            graphMessage.Attachments = attachments;
        }

        var body = new SendMailPostRequestBody
        {
            Message = graphMessage,
            SaveToSentItems = true
        };

        await client.Me.SendMail.PostAsync(body, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<GraphServiceClient> EnsureBackgroundClientAsync(CancellationToken cancellationToken)
    {
        if (_client is not null)
        {
            return _client;
        }

        // 先静默取一次票，失败（需要用户重新登录）会抛 MsalUiRequiredException，由调用方归类为“认证失败”
        var token = await _acquirer.AcquireAsync(allowInteractive: false, cancellationToken).ConfigureAwait(false);
        _resolvedEmail = token.AccountUsername ?? _config.EmailAddress;
        _client = CreateClient(allowInteractive: false);
        return _client;
    }

    // ---- 写回（PATCH / move / delete）----

    public async Task SetReadAsync(string graphMessageId, bool isRead, CancellationToken cancellationToken = default)
    {
        var client = await EnsureBackgroundClientAsync(cancellationToken).ConfigureAwait(false);
        await client.Me.Messages[graphMessageId].PatchAsync(new Message { IsRead = isRead }, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task SetFlaggedAsync(string graphMessageId, bool flagged, CancellationToken cancellationToken = default)
    {
        var client = await EnsureBackgroundClientAsync(cancellationToken).ConfigureAwait(false);
        await client.Me.Messages[graphMessageId].PatchAsync(new Message
        {
            Flag = new FollowupFlag { FlagStatus = flagged ? FollowupFlagStatus.Flagged : FollowupFlagStatus.NotFlagged }
        }, cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    /// <summary>移动到目标文件夹（graph 文件夹 id 或 archive / deleteditems 等知名名称），返回新的 Graph 消息 id。</summary>
    public async Task<string?> MoveMessageAsync(string graphMessageId, string destinationFolderId, CancellationToken cancellationToken = default)
    {
        var client = await EnsureBackgroundClientAsync(cancellationToken).ConfigureAwait(false);
        var moved = await client.Me.Messages[graphMessageId].Move.PostAsync(
            new Microsoft.Graph.Me.Messages.Item.Move.MovePostRequestBody { DestinationId = destinationFolderId },
            cancellationToken: cancellationToken).ConfigureAwait(false);
        return moved?.Id;
    }

    public async Task DeleteMessageAsync(string graphMessageId, CancellationToken cancellationToken = default)
    {
        var client = await EnsureBackgroundClientAsync(cancellationToken).ConfigureAwait(false);
        await client.Me.Messages[graphMessageId].DeleteAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    /// <summary>按需拉取 Graph 邮件正文与附件元数据。</summary>
    public async Task<MessageBodyContent> FetchBodyAsync(string graphMessageId, CancellationToken cancellationToken = default)
    {
        var client = await EnsureBackgroundClientAsync(cancellationToken).ConfigureAwait(false);
        var message = await client.Me.Messages[graphMessageId].GetAsync(r =>
        {
            r.QueryParameters.Select = ["id", "body", "uniqueBody", "hasAttachments"];
        }, cancellationToken).ConfigureAwait(false);

        string? html = null;
        string? text = null;
        var body = message?.Body;
        if (body?.Content is { } content)
        {
            if (body.ContentType == BodyType.Html)
            {
                html = content;
                text = MimeMessageMapper.StripHtml(content);
            }
            else
            {
                text = content;
            }
        }

        // HasAttachments 对「仅有内嵌图」的邮件经常是 false；HTML 含 cid: 时也要拉附件列表
        var needAttachments = message?.HasAttachments == true ||
                              (html is not null && CidHtmlRewriter.ContainsUnresolvedCid(html));
        var attachments = new List<MessageAttachmentInfo>();
        if (needAttachments)
        {
            var page = await client.Me.Messages[graphMessageId].Attachments.GetAsync(r =>
            {
                r.QueryParameters.Select = ["id", "name", "contentType", "size", "isInline", "contentId"];
            }, cancellationToken).ConfigureAwait(false);
            foreach (var a in page?.Value ?? [])
            {
                string? contentId = null;
                byte[]? bytes = null;
                if (a is FileAttachment listed)
                {
                    contentId = string.IsNullOrWhiteSpace(listed.ContentId)
                        ? null
                        : listed.ContentId.Trim().Trim('<', '>');
                }

                var isInline = a.IsInline == true || !string.IsNullOrEmpty(contentId);
                // 内嵌图：再取一次完整附件以拿到 ContentBytes（列表接口常不含内容）
                if (isInline && !string.IsNullOrEmpty(contentId) && a.Id is { } attId)
                {
                    try
                    {
                        var full = await client.Me.Messages[graphMessageId].Attachments[attId]
                            .GetAsync(cancellationToken: cancellationToken)
                            .ConfigureAwait(false);
                        if (full is FileAttachment fa)
                        {
                            if (fa.ContentBytes is { Length: > 0 } contentBytes)
                            {
                                bytes = contentBytes;
                            }

                            if (string.IsNullOrWhiteSpace(contentId) && !string.IsNullOrWhiteSpace(fa.ContentId))
                            {
                                contentId = fa.ContentId.Trim().Trim('<', '>');
                            }
                        }
                    }
                    catch
                    {
                        // 单个内嵌图失败不阻断正文；cid 会保留，阅读时仍显示裂图
                    }
                }

                attachments.Add(new MessageAttachmentInfo(
                    FileName: string.IsNullOrWhiteSpace(a.Name) ? "attachment" : a.Name!,
                    ContentType: a.ContentType,
                    SizeBytes: bytes?.LongLength ?? a.Size ?? 0,
                    ContentId: contentId,
                    IsInline: isInline,
                    RemoteId: a.Id,
                    ContentBytes: bytes));
            }
        }

        if (html is not null && CidHtmlRewriter.ContainsUnresolvedCid(html))
        {
            html = CidHtmlRewriter.Rewrite(html, attachments);
        }

        return new MessageBodyContent(text ?? string.Empty, html, attachments);
    }

    /// <summary>下载 Graph 附件到本地路径（优先 ContentBytes，否则走 /$value）。</summary>
    public async Task DownloadAttachmentAsync(string graphMessageId, string attachmentRemoteId, string destinationPath, CancellationToken cancellationToken = default)
    {
        var client = await EnsureBackgroundClientAsync(cancellationToken).ConfigureAwait(false);
        var attachment = await client.Me.Messages[graphMessageId].Attachments[attachmentRemoteId]
            .GetAsync(cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        byte[]? bytes = null;
        if (attachment is FileAttachment file && file.ContentBytes is { Length: > 0 } content)
        {
            bytes = content;
        }
        else
        {
            // 大附件：用原生 HTTP GET …/attachments/{id}/$value
            var token = await _acquirer.AcquireAsync(allowInteractive: false, cancellationToken).ConfigureAwait(false);
            using var http = new HttpClient();
            http.DefaultRequestHeaders.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token.AccessToken);
            var url = $"https://graph.microsoft.com/v1.0/me/messages/{Uri.EscapeDataString(graphMessageId)}/attachments/{Uri.EscapeDataString(attachmentRemoteId)}/$value";
            bytes = await http.GetByteArrayAsync(url, cancellationToken).ConfigureAwait(false);
        }

        var dir = Path.GetDirectoryName(Path.GetFullPath(destinationPath));
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        await File.WriteAllBytesAsync(destinationPath, bytes, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>发送被打断后判断：已发送文件夹里是否已有同主题、且发送时间不早于 <paramref name="notBefore"/> 的邮件（Graph 不允许自定义 Message-ID，只能这样近似判断）。</summary>
    public async Task<bool> SentItemExistsAsync(string subject, DateTimeOffset notBefore, CancellationToken cancellationToken = default)
    {
        var client = await EnsureBackgroundClientAsync(cancellationToken).ConfigureAwait(false);
        var page = await client.Me.MailFolders["sentitems"].Messages.GetAsync(r =>
        {
            r.QueryParameters.Filter = $"sentDateTime ge {notBefore.UtcDateTime:yyyy-MM-ddTHH:mm:ssZ}";
            r.QueryParameters.Select = ["subject", "sentDateTime"];
            r.QueryParameters.Top = 50;
        }, cancellationToken).ConfigureAwait(false);
        return page?.Value?.Any(m => string.Equals(m.Subject ?? string.Empty, subject ?? string.Empty, StringComparison.Ordinal)) == true;
    }

    public ValueTask DisposeAsync()
    {
        _client = null;
        _gate.Dispose();
        return ValueTask.CompletedTask;
    }

    private async Task<GraphServiceClient> EnsureClientAsync(CancellationToken cancellationToken)
    {
        if (_client is not null)
        {
            return _client;
        }

        await ConnectAsync(cancellationToken).ConfigureAwait(false);
        return _client ?? throw new InvalidOperationException("Graph 客户端未初始化。");
    }

    private GraphServiceClient CreateClient(bool allowInteractive)
    {
        var credential = new MsalTokenCredential(_acquirer, allowInteractive);
        return new GraphServiceClient(credential, MicrosoftGraphScopes.Default);
    }

    private static MailFolder ToMailFolder(Microsoft.Graph.Models.MailFolder folder)
    {
        var id = folder.Id ?? folder.DisplayName ?? Guid.NewGuid().ToString("N");
        var name = string.IsNullOrWhiteSpace(folder.DisplayName) ? id : folder.DisplayName!;
        var special = GraphFolderMapper.Map(name, wellKnownOrId: null);
        return new MailFolder(
            Id: id,
            Name: name,
            Special: special,
            RemotePath: id,
            UnreadCount: folder.UnreadItemCount ?? 0);
    }

    /// <summary>
    /// 双向同步用的 delta 查询：保留 @removed、区分完整 / 部分更新；410 抛 <see cref="GraphSyncStateNotFoundException"/>。
    /// 首次（没有 deltaLink）delta 不可用时退回按时间倒序的列表（此时没有 deltaLink，下次仍是首次同步）。
    /// </summary>
    public async Task<GraphDeltaResult> FetchDeltaAsync(
        string remoteFolderId, string? deltaLink, int pageSize, DateTimeOffset? receivedSince, CancellationToken ct)
    {
        var client = await EnsureClientAsync(ct).ConfigureAwait(false);
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            try
            {
                return await FetchDeltaCoreAsync(client, remoteFolderId, deltaLink, pageSize, receivedSince, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException and not GraphSyncStateNotFoundException)
            {
                if (IsSyncStateGone(ex))
                {
                    throw new GraphSyncStateNotFoundException("Graph delta 状态已失效（410）。", ex);
                }

                if (!string.IsNullOrWhiteSpace(deltaLink))
                {
                    throw;
                }
            }

            var list = await SyncWithListAsync(client, remoteFolderId, pageSize, ct).ConfigureAwait(false);
            return new GraphDeltaResult(
                list.Items.Select(i => new GraphDeltaEntry(GraphIdOf(i.Summary.Id), false, i)).ToList(),
                DeltaLink: null);
        }
        finally
        {
            _gate.Release();
        }
    }

    internal static bool IsSyncStateGone(Exception ex)
    {
        for (Exception? e = ex; e is not null; e = e.InnerException)
        {
            if (e is Microsoft.Kiota.Abstractions.ApiException api && api.ResponseStatusCode == 410)
            {
                return true;
            }

            var text = e.Message ?? string.Empty;
            if (text.Contains("syncStateNotFound", StringComparison.OrdinalIgnoreCase) ||
                text.Contains("resyncRequired", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private string GraphIdOf(string localMessageId)
    {
        var prefix = $"{_config.AccountId:D}:";
        return localMessageId.StartsWith(prefix, StringComparison.Ordinal) ? localMessageId[prefix.Length..] : localMessageId;
    }

    private async Task<GraphDeltaResult> FetchDeltaCoreAsync(
        GraphServiceClient client, string folderId, string? deltaLink, int pageSize, DateTimeOffset? receivedSince, CancellationToken ct)
    {
        var entries = new List<GraphDeltaEntry>();
        Microsoft.Graph.Me.MailFolders.Item.Messages.Delta.DeltaGetResponse? page;
        if (!string.IsNullOrWhiteSpace(deltaLink))
        {
            page = await client.Me.MailFolders[folderId].Messages.Delta
                .WithUrl(deltaLink)
                .GetAsDeltaGetResponseAsync(cancellationToken: ct)
                .ConfigureAwait(false);
        }
        else
        {
            page = await client.Me.MailFolders[folderId].Messages.Delta
                .GetAsDeltaGetResponseAsync(r =>
                {
                    r.QueryParameters.Select = GraphMessageMapper.DefaultSelect;
                    if (receivedSince is { } since)
                    {
                        r.QueryParameters.Filter = $"receivedDateTime ge {since.UtcDateTime:yyyy-MM-ddTHH:mm:ssZ}";
                    }

                    r.Headers.Add("Prefer", $"odata.maxpagesize={Math.Clamp(pageSize, 10, 200)}");
                }, ct)
                .ConfigureAwait(false);
        }

        string? nextDelta = null;
        while (page is not null)
        {
            foreach (var msg in page.Value ?? Enumerable.Empty<Message>())
            {
                if (string.IsNullOrEmpty(msg.Id))
                {
                    continue;
                }

                if (msg.AdditionalData is not null && msg.AdditionalData.ContainsKey("@removed"))
                {
                    entries.Add(new GraphDeltaEntry(msg.Id, true, null));
                    continue;
                }

                // 只带了 id + 变化字段的“部分更新”：没有主题 / 收件时间就当部分更新，只取已读和星标
                var full = msg.ReceivedDateTime is not null || msg.Subject is not null || msg.From is not null;
                if (full)
                {
                    entries.Add(new GraphDeltaEntry(msg.Id, false, GraphMessageMapper.ToSyncItem(_config.AccountId, folderId, msg)));
                }
                else
                {
                    entries.Add(new GraphDeltaEntry(
                        msg.Id,
                        false,
                        null,
                        IsRead: msg.IsRead,
                        IsFlagged: msg.Flag is null ? null : msg.Flag.FlagStatus == FollowupFlagStatus.Flagged));
                }
            }

            if (!string.IsNullOrEmpty(page.OdataNextLink))
            {
                page = await client.Me.MailFolders[folderId].Messages.Delta
                    .WithUrl(page.OdataNextLink)
                    .GetAsDeltaGetResponseAsync(cancellationToken: ct)
                    .ConfigureAwait(false);
                continue;
            }

            nextDelta = page.OdataDeltaLink;
            break;
        }

        return new GraphDeltaResult(entries, nextDelta);
    }

    private async Task<GraphFolderSyncResult> SyncWithDeltaAsync(
        GraphServiceClient client,
        string folderId,
        int batch,
        string? deltaLink,
        CancellationToken cancellationToken)
    {
        var items = new List<MailMessageSyncItem>();
        string? nextDelta = null;

        Microsoft.Graph.Me.MailFolders.Item.Messages.Delta.DeltaGetResponse? page;
        if (!string.IsNullOrWhiteSpace(deltaLink))
        {
            page = await client.Me.MailFolders[folderId].Messages.Delta
                .WithUrl(deltaLink)
                .GetAsDeltaGetResponseAsync(cancellationToken: cancellationToken)
                .ConfigureAwait(false);
        }
        else
        {
            page = await client.Me.MailFolders[folderId].Messages.Delta
                .GetAsDeltaGetResponseAsync(r =>
                {
                    r.QueryParameters.Select = GraphMessageMapper.DefaultSelect;
                    r.QueryParameters.Top = batch;
                }, cancellationToken)
                .ConfigureAwait(false);
        }

        while (page is not null)
        {
            foreach (var msg in page.Value ?? Enumerable.Empty<Message>())
            {
                if (string.IsNullOrEmpty(msg.Id))
                {
                    continue;
                }

                // 删除变更：AdditionalData 含 @removed
                if (msg.AdditionalData is not null &&
                    msg.AdditionalData.ContainsKey("@removed"))
                {
                    continue;
                }

                items.Add(GraphMessageMapper.ToSyncItem(_config.AccountId, folderId, msg));
                if (items.Count >= batch && string.IsNullOrEmpty(deltaLink))
                {
                    // 首次同步限制数量；仍尽量拿到 deltaLink
                }
            }

            if (!string.IsNullOrEmpty(page.OdataNextLink))
            {
                page = await client.Me.MailFolders[folderId].Messages.Delta
                    .WithUrl(page.OdataNextLink)
                    .GetAsDeltaGetResponseAsync(cancellationToken: cancellationToken)
                    .ConfigureAwait(false);
                continue;
            }

            nextDelta = page.OdataDeltaLink;
            break;
        }

        if (items.Count > batch)
        {
            items = items
                .OrderByDescending(i => i.Summary.ReceivedAt)
                .Take(batch)
                .ToList();
        }

        return new GraphFolderSyncResult(items, nextDelta);
    }

    private async Task<GraphFolderSyncResult> SyncWithListAsync(
        GraphServiceClient client,
        string folderId,
        int batch,
        CancellationToken cancellationToken)
    {
        var page = await client.Me.MailFolders[folderId].Messages.GetAsync(r =>
        {
            r.QueryParameters.Select = GraphMessageMapper.DefaultSelect;
            r.QueryParameters.Top = batch;
            r.QueryParameters.Orderby = ["receivedDateTime desc"];
        }, cancellationToken).ConfigureAwait(false);

        var items = new List<MailMessageSyncItem>();
        foreach (var msg in page?.Value ?? Enumerable.Empty<Message>())
        {
            if (string.IsNullOrEmpty(msg.Id))
            {
                continue;
            }

            items.Add(GraphMessageMapper.ToSyncItem(_config.AccountId, folderId, msg));
        }

        return new GraphFolderSyncResult(items, DeltaLink: null);
    }

    private static List<Recipient> ToRecipients(IReadOnlyList<string>? addresses)
    {
        var list = new List<Recipient>();
        if (addresses is null)
        {
            return list;
        }

        foreach (var raw in addresses)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                continue;
            }

            list.Add(new Recipient
            {
                EmailAddress = new EmailAddress { Address = raw.Trim() }
            });
        }

        return list;
    }

    private static async Task<List<Attachment>> BuildAttachmentsAsync(
        IReadOnlyList<string>? paths,
        CancellationToken cancellationToken)
    {
        var list = new List<Attachment>();
        long total = 0;
        if (paths is null || paths.Count == 0)
        {
            return list;
        }

        foreach (var path in paths)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                continue;
            }

            if (!File.Exists(path))
            {
                throw new Core.Sending.ClassifiedFailureException(
                    Core.Sending.FailureKind.MessageRejected, $"附件文件不存在：{Path.GetFileName(path)}");
            }

            total += new FileInfo(path).Length;
            if (total > Core.Sending.AttachmentLimits.GraphInlineLimit)
            {
                throw new Core.Sending.ClassifiedFailureException(
                    Core.Sending.FailureKind.MessageRejected,
                    "Outlook 账号目前只支持总大小不超过 3 MB 的附件（大附件上传会话尚未实现），请减少附件或改用其他账号发送。");
            }

            var bytes = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
            list.Add(new FileAttachment
            {
                OdataType = "#microsoft.graph.fileAttachment",
                Name = Path.GetFileName(path),
                ContentBytes = bytes
            });
        }

        return list;
    }
}

/// <summary>Graph 文件夹同步结果（含可选 deltaLink）。</summary>
public sealed record GraphFolderSyncResult(
    IReadOnlyList<MailMessageSyncItem> Items,
    string? DeltaLink);
