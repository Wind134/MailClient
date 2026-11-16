using MailClient.Core.Auth;
using MailClient.ViewModels;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace MailClient_App;

/// <summary>主窗口：登录失效提醒、账号右键“重新登录”。</summary>
public sealed partial class MainWindow
{
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _reloginTimer;
    private bool _reloginPromptLoop;

    /// <summary>失效提醒：有新失效时立即尝试；对话框被占用 / 窗口在托盘时，由定时器和窗口激活时补弹。</summary>
    private void StartReloginPrompts()
    {
        ViewModel.ReloginPromptRequested += (_, _) => _ = ShowPendingReloginPromptsAsync();
        Activated += (_, args) =>
        {
            if (args.WindowActivationState != WindowActivationState.Deactivated)
            {
                _ = ShowPendingReloginPromptsAsync();
            }
        };
        _reloginTimer = DispatcherQueue.CreateTimer();
        _reloginTimer.Interval = TimeSpan.FromSeconds(5);
        _reloginTimer.Tick += (_, _) =>
        {
            if (ViewModel.PendingReloginPrompts.Count > 0)
            {
                _ = ShowPendingReloginPromptsAsync();
            }
        };
        _reloginTimer.Start();
    }

    /// <summary>左栏账号行：登录失效时显示红色感叹号和提示。</summary>
    private static void ApplyReloginIndicator(NavigationViewItem item, MailboxNodeViewModel node)
    {
        if (node.NeedsRelogin)
        {
            item.InfoBadge = CreateReloginBadge();
            ToolTipService.SetToolTip(item, ReloginTexts.SidebarTooltip);
        }
        else
        {
            item.InfoBadge = null;
            ToolTipService.SetToolTip(item, null);
        }
    }

    private static InfoBadge CreateReloginBadge()
    {
        if (Application.Current.Resources.TryGetValue("CriticalIconInfoBadgeStyle", out var style) && style is Style s)
        {
            return new InfoBadge { Style = s };
        }

        return new InfoBadge
        {
            Background = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0xC4, 0x2B, 0x1C)),
            IconSource = new FontIconSource { Glyph = "\uE8C9", Foreground = new SolidColorBrush(Colors.White) }
        };
    }

    /// <summary>依次弹出待提醒的“登录已失效”对话框（每个账号每次失效只弹一次）。</summary>
    private async Task ShowPendingReloginPromptsAsync()
    {
        if (_reloginPromptLoop)
        {
            return;
        }

        _reloginPromptLoop = true;
        try
        {
            while (!_dialogOpen && CanShowDialogNow())
            {
                var pending = ViewModel.PendingReloginPrompts;
                if (pending.Count == 0)
                {
                    return;
                }

                var accountId = pending[0];
                if (!ViewModel.TryBeginReloginPrompt(accountId))
                {
                    return;
                }

                _dialogOpen = true;
                try
                {
                    var name = ViewModel.AccountDisplayName(accountId);
                    var dialog = new ContentDialog
                    {
                        XamlRoot = Content.XamlRoot,
                        Title = ReloginTexts.ExpiredTitle(name),
                        Content = ReloginTexts.ExpiredBody,
                        PrimaryButtonText = ReloginTexts.ButtonRelogin,
                        CloseButtonText = ReloginTexts.ButtonLater,
                        DefaultButton = ContentDialogButton.Primary
                    };
                    if (await dialog.ShowAsync() == ContentDialogResult.Primary)
                    {
                        await RunReauthAsync(accountId);
                    }
                    else
                    {
                        ViewModel.SnoozeReloginPrompt(accountId);
                    }
                }
                catch (Exception ex)
                {
                    ShowReloginError($"无法显示登录提醒：{ex.Message}");
                }
                finally
                {
                    _dialogOpen = false;
                }
            }
        }
        finally
        {
            _reloginPromptLoop = false;
        }
    }

    /// <summary>
    /// 右键“重新登录”：先检查连接（正在检查连接…）。确实失效 → 直接重新登录；连接正常 → 告诉用户无需重新登录；
    /// 网络问题 / 其他错误 → 说明原因，仍可选择重新登录。
    /// </summary>
    private async Task ReloginFromMenuAsync(Guid accountId)
    {
        if (_dialogOpen || Content?.XamlRoot is null)
        {
            return;
        }

        _dialogOpen = true;
        try
        {
            var name = ViewModel.AccountDisplayName(accountId);
            ConnectionCheckResult? check;
            using (var cts = new CancellationTokenSource())
            {
                check = await RunWithBusyDialogAsync(
                    ReloginTexts.PasswordTitle(name), ReloginTexts.Checking, cts,
                    () => ViewModel.CheckAccountConnectionAsync(accountId, cts.Token));
            }

            if (check is null)
            {
                return;      // 用户取消了检查
            }

            switch (ReloginFlow.Decide(check))
            {
                case ReloginCheckDecision.StartReauth:
                    await RunReauthAsync(accountId);
                    break;

                case ReloginCheckDecision.ShowHealthy:
                {
                    var dialog = new ContentDialog
                    {
                        XamlRoot = Content.XamlRoot,
                        Title = ReloginTexts.HealthyTitle(name),
                        Content = WrapText(ReloginTexts.HealthyBody),
                        PrimaryButtonText = ReloginTexts.ButtonSyncNow,
                        SecondaryButtonText = ReloginTexts.ButtonReloginAnyway,
                        CloseButtonText = ReloginTexts.ButtonClose,
                        DefaultButton = ContentDialogButton.Primary
                    };
                    switch (await dialog.ShowAsync())
                    {
                        case ContentDialogResult.Primary:
                            _ = ViewModel.SyncAsync(accountId, userInitiated: true);
                            break;
                        case ContentDialogResult.Secondary:
                            await RunReauthAsync(accountId);
                            break;
                    }

                    break;
                }

                default:
                {
                    var network = ReloginFlow.Decide(check) == ReloginCheckDecision.ShowNetworkProblem;
                    var dialog = new ContentDialog
                    {
                        XamlRoot = Content.XamlRoot,
                        Title = network ? ReloginTexts.NetworkTitle(name) : ReloginTexts.CheckFailedTitle(name),
                        Content = WrapText(network ? ReloginTexts.NetworkBody(check.Message) : ReloginTexts.CheckFailedBody(check.Message)),
                        PrimaryButtonText = ReloginTexts.ButtonReloginAnyway,
                        CloseButtonText = ReloginTexts.ButtonClose,
                        DefaultButton = ContentDialogButton.Close
                    };
                    if (await dialog.ShowAsync() == ContentDialogResult.Primary)
                    {
                        await RunReauthAsync(accountId);
                    }

                    break;
                }
            }
        }
        catch (Exception ex)
        {
            ShowReloginError($"重新登录失败：{ex.Message}");
        }
        finally
        {
            _dialogOpen = false;
        }
    }

    /// <summary>
    /// 真正的重新登录（调用方已占用 <see cref="_dialogOpen"/>）：IMAP 先要密码；Gmail / Outlook 拉起浏览器。
    /// 登录中显示可取消的进度框；失败时说明原因（选错账号时原有登录不受影响），IMAP 可重试。
    /// </summary>
    private async Task RunReauthAsync(Guid accountId)
    {
        var name = ViewModel.AccountDisplayName(accountId);
        var email = ViewModel.AccountEmail(accountId) ?? name;
        var needsPassword = await ViewModel.AccountRequiresPasswordAsync(accountId);
        while (true)
        {
            string? password = null;
            if (needsPassword)
            {
                password = await AskPasswordAsync(name, email);
                if (password is null)
                {
                    return;
                }
            }

            MailClient.Providers.Setup.ReauthResult? result;
            using (var cts = new CancellationTokenSource())
            {
                result = await RunWithBusyDialogAsync(
                    ReloginTexts.PasswordTitle(name),
                    needsPassword ? ReloginTexts.VerifyingPassword : ReloginTexts.SigningIn,
                    cts,
                    () => ViewModel.ReauthenticateAccountAsync(accountId, password, cts.Token));
            }

            if (result is null || result.Success || result.Cancelled)
            {
                return;
            }

            var dialog = new ContentDialog
            {
                XamlRoot = Content.XamlRoot,
                Title = result.AccountMismatch ? ReloginTexts.MismatchTitle : $"「{name}」重新登录失败",
                Content = WrapText(result.Error ?? "未知错误"),
                PrimaryButtonText = "重试",
                CloseButtonText = ReloginTexts.ButtonClose,
                DefaultButton = ContentDialogButton.Primary
            };
            if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            {
                return;
            }
        }
    }

    private async Task<string?> AskPasswordAsync(string name, string email)
    {
        var box = new PasswordBox { PlaceholderText = "密码或授权码", Width = 360 };
        var panel = new StackPanel { Spacing = 12 };
        panel.Children.Add(WrapText(ReloginTexts.PasswordBody(email)));
        panel.Children.Add(box);
        var dialog = new ContentDialog
        {
            XamlRoot = Content.XamlRoot,
            Title = ReloginTexts.PasswordTitle(name),
            Content = panel,
            PrimaryButtonText = ReloginTexts.ButtonRelogin,
            CloseButtonText = ReloginTexts.ButtonCancel,
            DefaultButton = ContentDialogButton.Primary,
            IsPrimaryButtonEnabled = false
        };
        box.PasswordChanged += (_, _) => dialog.IsPrimaryButtonEnabled = box.Password.Length > 0;
        dialog.Opened += (_, _) => box.Focus(FocusState.Programmatic);
        return await dialog.ShowAsync() == ContentDialogResult.Primary ? box.Password : null;
    }

    /// <summary>显示“进度 + 取消”对话框执行 <paramref name="work"/>；用户点取消返回 null（并取消 <paramref name="cts"/>）。</summary>
    private async Task<T?> RunWithBusyDialogAsync<T>(string title, string text, CancellationTokenSource cts, Func<Task<T>> work)
        where T : class
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
        panel.Children.Add(new ProgressRing { IsActive = true, Width = 20, Height = 20 });
        panel.Children.Add(new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap });
        var dialog = new ContentDialog
        {
            XamlRoot = Content.XamlRoot,
            Title = title,
            Content = panel,
            CloseButtonText = ReloginTexts.ButtonCancel
        };

        var shown = dialog.ShowAsync().AsTask();
        var job = work();
        var first = await Task.WhenAny(job, shown);
        if (first == shown && !job.IsCompleted)
        {
            // 用户点了取消（或按了 Esc）
            cts.Cancel();
            try
            {
                await job;
            }
            catch (Exception)
            {
            }

            return null;
        }

        dialog.Hide();
        await shown;
        return await job;
    }

    private static TextBlock WrapText(string text) => new() { Text = text, TextWrapping = TextWrapping.Wrap };

    private void ShowReloginError(string message)
    {
        ViewModel.StatusMessage = message;
        ViewModel.IsStatusError = true;
        ViewModel.IsStatusOpen = true;
    }
}
