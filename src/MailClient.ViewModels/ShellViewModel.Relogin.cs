using MailClient.Core.Auth;
using MailClient.Providers.Setup;
using MailClient.Providers.Sync;
using MailClient.Core;

namespace MailClient.ViewModels;

/// <summary>主窗口 VM：登录失效提醒、连接检查与重新登录。</summary>
public sealed partial class ShellViewModel
{
    private readonly ReloginTracker? _relogin;
    private readonly IAccountAuthService? _accountAuth;

    /// <summary>有账号刚变成“登录已失效”：界面应在空闲时调用 <see cref="PendingReloginPrompts"/> 弹提醒（在 UI 线程触发）。</summary>
    public event EventHandler? ReloginPromptRequested;

    /// <summary>是否支持重新登录（接上了认证服务）。</summary>
    public bool CanRelogin => _accountAuth is not null;

    /// <summary>该弹“登录已失效”提醒的账号（只含左栏里还存在的账号）。</summary>
    public IReadOnlyList<Guid> PendingReloginPrompts
        => _relogin is null
            ? Array.Empty<Guid>()
            : _relogin.PendingPrompts.Where(id => Tree.Accounts.Any(a => a.AccountId == id)).ToList();

    public bool NeedsRelogin(Guid accountId) => _relogin?.NeedsRelogin(accountId) == true;

    /// <summary>界面准备弹提醒：返回 true 才弹（同一次失效只弹一次）。</summary>
    public bool TryBeginReloginPrompt(Guid accountId) => _relogin?.TryBeginPrompt(accountId) == true;

    /// <summary>提醒里点了“稍后”：重启应用或重新登录成功前不再弹。</summary>
    public void SnoozeReloginPrompt(Guid accountId) => _relogin?.Snooze(accountId);

    public string AccountDisplayName(Guid accountId)
        => Tree.Accounts.FirstOrDefault(a => a.AccountId == accountId)?.Title ?? "该账号";

    public string? AccountEmail(Guid accountId)
        => Tree.Accounts.FirstOrDefault(a => a.AccountId == accountId)?.EmailAddress;

    private void OnReloginStateChanged(Guid accountId, bool needsRelogin)
    {
        foreach (var node in Tree.Accounts.Where(a => a.AccountId == accountId))
        {
            node.NeedsRelogin = needsRelogin;
        }

        if (needsRelogin)
        {
            ReloginPromptRequested?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>右键“重新登录”的第一步：轻量连接检查，并把结果记到失效跟踪里。</summary>
    public async Task<ConnectionCheckResult> CheckAccountConnectionAsync(Guid accountId, CancellationToken cancellationToken = default)
    {
        if (_accountAuth is null)
        {
            return new ConnectionCheckResult(ConnectionStatus.Failed, "未配置账号认证服务。");
        }

        ConnectionCheckResult result;
        try
        {
            result = await Task.Run(() => _accountAuth.CheckConnectionAsync(accountId, cancellationToken), cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            result = new ConnectionCheckResult(ConnectionStatus.Failed, ex.Message);
        }

        if (_relogin is not null)
        {
            ReloginFlow.Apply(_relogin, accountId, result);
        }

        return result;
    }

    public async Task<bool> AccountRequiresPasswordAsync(Guid accountId)
        => _accountAuth is not null && await _accountAuth.RequiresPasswordAsync(accountId, CancellationToken.None);

    /// <summary>
    /// 重新登录（不删账号、不动本地邮件）。成功后清掉失效标记并立即同步该账号；失败把原因返回给界面显示。
    /// </summary>
    public async Task<ReauthResult> ReauthenticateAccountAsync(Guid accountId, string? password = null, CancellationToken cancellationToken = default)
    {
        if (_accountAuth is null)
        {
            return new ReauthResult(false, "未配置账号认证服务。");
        }

        // 登录流程里有浏览器回环监听 / MSAL：放到线程池，免得在 UI 线程上同步等待
        var result = await Task.Run(() => _accountAuth.ReauthenticateAsync(accountId, password, cancellationToken), CancellationToken.None);
        if (result.Success)
        {
            _relogin?.ReportSuccess(accountId);
            foreach (var node in Tree.Accounts.Where(a => a.AccountId == accountId))
            {
                node.NeedsRelogin = false;
            }

            ShowStatus(ReloginTexts.Succeeded(AccountDisplayName(accountId)));
            _ = SyncAsync(accountId, userInitiated: true);
        }

        return result;
    }

    /// <summary>启动时把每个真实账号的连接检查一遍（并行、不弹浏览器），认证失败的进入同一套提醒。</summary>
    public async Task CheckAllAccountsAsync(CancellationToken cancellationToken = default)
    {
        if (_accountAuth is null || _relogin is null)
        {
            return;
        }

        var accounts = (await _database.ListAccountsAsync(cancellationToken))
            .Where(a => a.IsEnabled && !DemoAccounts.IsDemoAccount(a.Id))
            .ToList();
        await Task.WhenAll(accounts.Select(async a =>
        {
            try
            {
                var result = await Task.Run(() => _accountAuth.CheckConnectionAsync(a.Id, cancellationToken), cancellationToken);
                ReloginFlow.Apply(_relogin, a.Id, result);
            }
            catch (Exception)
            {
                // 启动检查只是提前发现问题；失败不打扰用户，后台同步会再试
            }
        }));
    }

    private string DescribeSyncFailure(Exception ex, Guid? accountId)
    {
        if (!ConnectionFailureClassifier.IsAuthFailure(ex))
        {
            return ex.Message;
        }

        return accountId is { } id
            ? $"「{AccountDisplayName(id)}」{ReloginTexts.SyncAuthFailed}"
            : ReloginTexts.SyncAuthFailed;
    }
}
