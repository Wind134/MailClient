using MailClient.Auth;
using MailClient.Core;
using MailClient.Core.Notifications;
using MailClient.Core.Services;
using MailClient.Core.Sync;
using MailClient.Providers.Graph;
using MailClient.Providers.Imap;
using MailClient.Storage;
using MailClient.Storage.Demo;

namespace MailClient.Providers.Setup;

/// <summary>
/// 真实账号的同步服务：列文件夹，再按文件夹拉取最近邮件（首次有数量上限，之后按游标增量）。
/// 演示账号会被跳过。进度通过 <see cref="ProgressChanged"/> 报告（可能在后台线程触发）。
/// </summary>
public sealed class AccountSyncService : IIncrementalSyncService
{
    /// <summary>首次同步每个文件夹最多拉取的邮件数（IMAP 逐封取全文生成预览，数量大会很慢）。</summary>
    public static int InitialLimit(SpecialFolder special) => special switch
    {
        SpecialFolder.Inbox => 60,
        SpecialFolder.Sent or SpecialFolder.Drafts or SpecialFolder.Archive => 30,
        SpecialFolder.Junk or SpecialFolder.Trash => 20,
        _ => 0
    };

    public const int IncrementalLimit = 200;

    private readonly MailDatabase _db;
    private readonly ICredentialStore _store;
    private readonly AppSettingsStore _settings;
    private readonly Func<IntPtr>? _windowHandle;
    private readonly AccountConnectionStore _connections;
    private readonly ISyncLog _log;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public AccountSyncService(
        MailDatabase database,
        ICredentialStore store,
        AppSettingsStore settings,
        Func<IntPtr>? windowHandleProvider = null,
        ISyncLog? log = null)
    {
        _log = log ?? NullSyncLog.Instance;
        _db = database ?? throw new ArgumentNullException(nameof(database));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _windowHandle = windowHandleProvider;
        _connections = new AccountConnectionStore(store);
    }

    public event EventHandler<SyncProgress>? ProgressChanged;

    /// <summary>
    /// 一次（非首次）同步之后，收件箱里出现了新的未读邮件（已按 <see cref="NewMailPolicy"/> 过滤：不含首次同步、
    /// 已读、草稿、自己发的邮件，也不含用户关掉通知时）。可能在后台线程触发；订阅者负责弹 Windows 通知。
    /// </summary>
    public event EventHandler<NewMailEventArgs>? NewMailDetected;

    public Task SyncAllAsync(CancellationToken cancellationToken = default)
        => SyncAllAsync(SyncRequest.Default, cancellationToken);

    public Task SyncAccountAsync(Guid accountId, CancellationToken cancellationToken = default)
        => SyncAccountAsync(accountId, SyncRequest.Default, cancellationToken);

    public async Task SyncAllAsync(SyncRequest request, CancellationToken cancellationToken = default)
    {
        var accounts = await _db.ListAccountsAsync(cancellationToken).ConfigureAwait(false);
        foreach (var account in accounts.Where(a => a.IsEnabled && !DemoDataSeeder.IsDemoAccount(a.Id)))
        {
            try
            {
                await SyncAccountAsync(account.Id, request, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // 已通过 Failed 事件报告；继续同步其他账号
            }
        }
    }

    public async Task SyncAccountAsync(Guid accountId, SyncRequest request, CancellationToken cancellationToken = default)
    {
        var account = (await _db.ListAccountsAsync(cancellationToken).ConfigureAwait(false))
            .FirstOrDefault(a => a.Id == accountId);
        if (account is null || DemoDataSeeder.IsDemoAccount(accountId))
        {
            return;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Report(account, request, SyncPhase.Started, "正在连接…");
            var snapshot = await TrySnapshotAsync(account, cancellationToken).ConfigureAwait(false);
            var options = _settings.GetSyncOptions();
            var changed = account.ProviderId switch
            {
                "microsoft.graph" => await SyncGraphAsync(account, request, options, cancellationToken).ConfigureAwait(false),
                GmailEndpoints.ProviderId => await SyncGmailAsync(account, request, options, cancellationToken).ConfigureAwait(false),
                _ => await SyncImapAsync(account, request, options, cancellationToken).ConfigureAwait(false)
            };

            var latest = (await _db.ListAccountsAsync(CancellationToken.None).ConfigureAwait(false))
                .FirstOrDefault(a => a.Id == accountId);
            if (latest is not null)
            {
                await _db.UpsertAccountAsync(latest with { LastSyncAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow }, CancellationToken.None)
                    .ConfigureAwait(false);
            }

            Report(account, request, SyncPhase.Completed, "同步完成", changed: changed);
            await TryRaiseNewMailAsync(account, snapshot).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            Report(account, request, SyncPhase.Failed, "同步已取消", error: "已取消");
            throw;
        }
        catch (Exception ex)
        {
            var kind = ProviderDetector.Detect(account.EmailAddress)?.Kind ?? SetupProviderKind.OtherImap;
            var error = SetupErrorClassifier.Classify(ex, kind, SetupStage.Imap);
            _log.Warn($"{account.EmailAddress}: 同步失败：{ex.Message}");
            Report(account, request, SyncPhase.Failed, "同步失败", error: error.Message);
            throw;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>同步前给收件箱拍快照；没人订阅 / 用户关了通知 / 出错时返回 null（通知永远不能拖垮同步）。</summary>
    private async Task<InboxSnapshot?> TrySnapshotAsync(AccountEntity account, CancellationToken ct)
    {
        if (NewMailDetected is null || !_settings.Snapshot().NotificationsEnabled)
        {
            return null;
        }

        try
        {
            return await _db.SnapshotInboxAsync(account.Id, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.Warn($"{account.EmailAddress}: 新邮件检测快照失败：{ex.Message}");
            return null;
        }
    }

    private async Task TryRaiseNewMailAsync(AccountEntity account, InboxSnapshot? snapshot)
    {
        var handler = NewMailDetected;
        if (snapshot is null || handler is null)
        {
            return;
        }

        try
        {
            var enabled = _settings.Snapshot().NotificationsEnabled;
            var fresh = await _db.FindNewMailAsync(account, snapshot, enabled, DateTimeOffset.UtcNow, CancellationToken.None)
                .ConfigureAwait(false);
            if (fresh.Count > 0)
            {
                handler(this, new NewMailEventArgs(account.Id, account.DisplayName, fresh));
            }
        }
        catch (Exception ex)
        {
            _log.Warn($"{account.EmailAddress}: 新邮件通知失败：{ex.Message}");
        }
    }

    private async Task<bool> SyncImapAsync(AccountEntity account, SyncRequest request, SyncOptions options, CancellationToken ct)
    {
        var connection = await _connections.LoadAsync(account.Id, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException("找不到该账号的服务器设置，请移除后重新添加。");
        if (connection.Imap is null || connection.Smtp is null)
        {
            throw new InvalidOperationException("该账号缺少 IMAP/SMTP 服务器设置，请移除后重新添加。");
        }

        var config = new GenericImapAccountConfig(
            account.Id, account.EmailAddress, account.DisplayName, connection.Imap, connection.Smtp,
            AccountConnectionStore.ImapSecretKey(account.Id));
        await using var provider = new GenericImapProvider(config, _store);
        return await SyncImapProviderAsync(account, request, options, provider, skipArchive: false, ct).ConfigureAwait(false);
    }

    private async Task<bool> SyncGmailAsync(AccountEntity account, SyncRequest request, SyncOptions options, CancellationToken ct)
    {
        var clientId = _settings.GmailClientId;
        if (string.IsNullOrWhiteSpace(clientId))
        {
            throw new InvalidOperationException("还没有填写 Google 的 ClientId。请到“添加账号”里选 Gmail，按页面上的步骤创建并粘贴 ClientId 与 Client secret（或改用 Gmail 应用专用密码，通过“其他邮箱 (IMAP)”添加）。");
        }

        var config = new GmailAccountConfig(account.Id, account.EmailAddress, account.DisplayName, clientId, _settings.GmailClientSecret);
        using var acquirer = new GmailTokenAcquirer(config, _store);
        var gmail = new GmailProvider(config, acquirer);
        await using var provider = gmail.Imap;
        // Gmail 的“所有邮件”（映射为 Archive）几乎等于全部邮件，首次同步跳过。
        return await SyncImapProviderAsync(account, request, options, provider, skipArchive: true, ct).ConfigureAwait(false);
    }

    private async Task<bool> SyncImapProviderAsync(
        AccountEntity account, SyncRequest request, SyncOptions options, GenericImapProvider provider, bool skipArchive, CancellationToken ct)
    {
        var pipeline = new ImapSyncPipeline(_db, options, _log);
        var targets = await ResolveTargetsAsync(
                account, request,
                async () => (await pipeline.SyncFoldersAsync(provider, ct).ConfigureAwait(false))
                    .Select(f => (f.Id, f.Name, f.Special)).ToList(),
                skipArchive, ImapSyncPipeline.BuildFolderId, ct)
            .ConfigureAwait(false);
        var done = 0;
        var anyChange = false;
        foreach (var target in targets)
        {
            ct.ThrowIfCancellationRequested();
            Report(account, request, SyncPhase.Progress, $"正在同步 {target.Name}…", done, targets.Count);
            var limit = target.HasCursor ? IncrementalLimit : InitialLimit(target.Special);
            var report = await pipeline.SyncFolderMessagesAsync(provider, account.Id, target.RemoteId, limit, request.Force, ct)
                .ConfigureAwait(false);
            anyChange |= report.Changed;
            done++;
        }

        await _db.RecalculateFolderCountsAsync(ct).ConfigureAwait(false);
        Report(account, request, SyncPhase.Progress, "即将完成…", done, targets.Count);
        return anyChange;
    }

    private async Task<bool> SyncGraphAsync(AccountEntity account, SyncRequest request, SyncOptions options, CancellationToken ct)
    {
        var clientId = _settings.MicrosoftClientId;
        if (string.IsNullOrWhiteSpace(clientId))
        {
            throw new InvalidOperationException("还没有填写 Microsoft 的应用（客户端）ID。请到“添加账号”里选 Outlook / Microsoft 365，按页面上的步骤在 Microsoft Entra 注册应用并粘贴 ID。");
        }

        var config = new MicrosoftGraphAccountConfig(
            account.Id, account.EmailAddress, account.DisplayName, clientId, _settings.MicrosoftTenantId);
        var acquirer = new MicrosoftGraphTokenAcquirer(config, _store, windowHandleProvider: _windowHandle);
        await using var provider = new MicrosoftGraphProvider(config, acquirer);
        var pipeline = new GraphSyncPipeline(_db, options, _log);
        var targets = await ResolveTargetsAsync(
                account, request,
                async () => (await pipeline.SyncFoldersAsync(provider, ct).ConfigureAwait(false))
                    .Select(f => (f.Id, f.Name, f.Special)).ToList(),
                skipArchive: false, GraphSyncPipeline.BuildFolderId, ct)
            .ConfigureAwait(false);
        var done = 0;
        var anyChange = false;
        foreach (var target in targets)
        {
            ct.ThrowIfCancellationRequested();
            Report(account, request, SyncPhase.Progress, $"正在同步 {target.Name}…", done, targets.Count);
            var limit = target.HasCursor ? IncrementalLimit : InitialLimit(target.Special);
            var report = await pipeline.SyncFolderAsync(provider, account.Id, target.RemoteId, limit, ct).ConfigureAwait(false);
            anyChange |= report.Changed;
            done++;
        }

        await _db.RecalculateFolderCountsAsync(ct).ConfigureAwait(false);
        return anyChange;
    }

    private sealed record SyncTarget(string RemoteId, string Name, SpecialFolder Special, bool HasCursor);

    /// <summary>
    /// 这一轮要同步哪些文件夹。完整同步：先重新列出服务器上的文件夹（顺便清掉已删除的）；
    /// 只同步收件箱（IDLE 触发）：直接用本地库里已有的收件箱，省掉 LIST。
    /// </summary>
    private async Task<IReadOnlyList<SyncTarget>> ResolveTargetsAsync(
        AccountEntity account,
        SyncRequest request,
        Func<Task<List<(string Id, string Name, SpecialFolder Special)>>> listFolders,
        bool skipArchive,
        Func<Guid, string, string> buildFolderId,
        CancellationToken ct)
    {
        var known = (await _db.ListFoldersAsync(account.Id, ct).ConfigureAwait(false)).ToList();
        if (request.InboxOnly)
        {
            var inbox = known.FirstOrDefault(f => f.Special == SpecialFolder.Inbox);
            if (inbox is not null)
            {
                return [new SyncTarget(inbox.RemotePath ?? inbox.Id, inbox.Name, inbox.Special, !string.IsNullOrEmpty(inbox.SyncCursor))];
            }
        }

        var listed = await listFolders().ConfigureAwait(false);
        var existing = (await _db.ListFoldersAsync(account.Id, ct).ConfigureAwait(false))
            .ToDictionary(f => f.Id, StringComparer.Ordinal);
        return listed
            .Where(f => InitialLimit(f.Special) > 0 && !(skipArchive && f.Special == SpecialFolder.Archive))
            .Where(f => !request.InboxOnly || f.Special == SpecialFolder.Inbox)
            .Select(f => new SyncTarget(
                f.Id, f.Name, f.Special,
                existing.TryGetValue(buildFolderId(account.Id, f.Id), out var k) && !string.IsNullOrEmpty(k.SyncCursor)))
            .ToList();
    }

    private void Report(AccountEntity account, SyncRequest request, SyncPhase phase, string message, int done = 0, int total = 0, string? error = null, bool changed = false)
        => ProgressChanged?.Invoke(this, new SyncProgress(account.Id, account.DisplayName, phase, message, done, total, error, request.Background, changed));
}
