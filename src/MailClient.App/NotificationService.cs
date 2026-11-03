using System.Security.Cryptography;
using System.Text;
using MailClient.Core.Notifications;
using Microsoft.Windows.AppNotifications;
using Microsoft.Windows.AppNotifications.Builder;

namespace MailClient_App;

/// <summary>
/// Windows 系统通知（Windows App SDK 的 AppNotificationManager）：登记 / 注销、弹新邮件通知、测试通知、
/// 以及把点击回调还原成 <see cref="ToastAction"/>。
/// 打包版靠 Package.appxmanifest 里的 toastNotificationActivation + comServer 让“应用没在运行时点通知”也能把应用拉起来。
/// </summary>
internal sealed class NotificationService
{
    private const string Group = "mail";
    private bool _registered;

    /// <summary>通知被点击（正文或按钮）。在非 UI 线程上触发。</summary>
    public event Action<ToastAction>? ActionInvoked;

    public bool IsRegistered => _registered;

    /// <summary>登记失败的原因（界面上显示），成功时为 null。</summary>
    public string? RegistrationError { get; private set; }

    /// <summary>登记通知。必须在读取激活参数（冷启动点通知）之前调用。</summary>
    public void Register()
    {
        try
        {
            var manager = AppNotificationManager.Default;
            manager.NotificationInvoked += OnNotificationInvoked;
            manager.Register();
            if (!AppIdentity.IsPackaged)
            {
                try { AppIdentity.EnsureAumidRegistration(); } catch { /* 只影响通知标题和图标 */ }
                // Register() 会在后台线程再改写一次，稍后再确认几次
                _ = Task.Run(async () =>
                {
                    foreach (var delay in new[] { 500, 1500, 4000, 10000 })
                    {
                        await Task.Delay(delay).ConfigureAwait(false);
                        try { AppIdentity.EnsureAumidRegistration(); } catch { }
                    }
                });
            }
            _registered = true;
        }
        catch (Exception ex)
        {
            RegistrationError = ex.Message;
            _registered = false;
        }
    }

    public void Unregister()
    {
        if (!_registered)
        {
            return;
        }

        try
        {
            AppNotificationManager.Default.NotificationInvoked -= OnNotificationInvoked;
            // 免打包（exe 安装版）时 Unregister 会撤销 COM 激活注册，之后点操作中心里的旧通知就拉不起应用了，所以只对 MSIX 注销。
            if (AppIdentity.IsPackaged)
            {
                AppNotificationManager.Default.Unregister();
            }
        }
        catch
        {
            // 退出时的失败没有意义
        }

        _registered = false;
    }

    /// <summary>Windows 自己的通知开关（系统设置 / 专注助手）是否允许本应用弹通知。</summary>
    public bool IsEnabledInWindows
    {
        get
        {
            try
            {
                return AppNotificationManager.Default.Setting == AppNotificationSetting.Enabled;
            }
            catch
            {
                return false;
            }
        }
    }

    /// <summary>把 Windows 返回的设置状态翻译成给用户看的一句话。</summary>
    public string WindowsStatusText
    {
        get
        {
            if (!_registered)
            {
                return "通知服务没有启动" + (RegistrationError is null ? "。" : $"：{RegistrationError}");
            }

            try
            {
                return AppNotificationManager.Default.Setting switch
                {
                    AppNotificationSetting.Enabled => "Windows 允许本应用发送通知。",
                    AppNotificationSetting.DisabledForApplication => "你在 Windows 设置里关闭了本应用的通知。",
                    AppNotificationSetting.DisabledForUser => "你在 Windows 设置里关闭了通知（或当前用户禁用了通知）。",
                    AppNotificationSetting.DisabledByGroupPolicy => "通知被组织的组策略禁用了。",
                    AppNotificationSetting.DisabledByManifest => "应用清单里没有启用通知。",
                    _ => "Windows 当前不允许本应用发送通知。"
                };
            }
            catch (Exception ex)
            {
                return $"无法读取 Windows 通知设置：{ex.Message}";
            }
        }
    }

    private void OnNotificationInvoked(AppNotificationManager sender, AppNotificationActivatedEventArgs args)
    {
        var action = ParseArguments(args.Arguments);
        if (action is not null)
        {
            ActionInvoked?.Invoke(action);
        }
    }

    /// <summary>从激活参数里取出通知动作（冷启动点通知时由 App.OnLaunched 调用）。</summary>
    public static ToastAction? ParseArguments(IDictionary<string, string>? arguments)
        => ToastAction.TryParse(arguments);

    /// <summary>按方案弹通知：少量逐封弹（带“标为已读 / 删除”），多封合并成一条汇总。</summary>
    public int Show(NewMailToastPlan plan)
    {
        if (!_registered || plan.ToastCount == 0)
        {
            return 0;
        }

        var shown = 0;
        if (plan.Summary is { } summary)
        {
            var action = summary.AccountId is { } account
                ? new ToastAction(ToastActionKind.OpenInbox, account)
                : new ToastAction(ToastActionKind.OpenInbox);
            shown += Send(Apply(new AppNotificationBuilder(), action)
                .AddText(summary.Title, new AppNotificationTextProperties().SetMaxLines(1))
                .AddText(summary.Body), "summary:" + Guid.NewGuid().ToString("N"));
            return shown;
        }

        foreach (var m in plan.Individual)
        {
            var builder = Apply(new AppNotificationBuilder(), new ToastAction(ToastActionKind.Open, m.AccountId, m.MessageId))
                .AddText(m.SenderDisplay, new AppNotificationTextProperties().SetMaxLines(1))
                .AddText(m.SubjectDisplay, new AppNotificationTextProperties().SetMaxLines(1));
            if (!string.IsNullOrEmpty(m.PreviewDisplay))
            {
                builder.AddText(m.PreviewDisplay, new AppNotificationTextProperties().SetMaxLines(2));
            }

            builder.AddButton(Button("标为已读", new ToastAction(ToastActionKind.MarkRead, m.AccountId, m.MessageId)));
            builder.AddButton(Button("删除", new ToastAction(ToastActionKind.Delete, m.AccountId, m.MessageId)));
            shown += Send(builder, m.MessageId);
        }

        return shown;
    }

    /// <summary>设置里的“发送测试通知”：不碰任何邮件，只验证通知链路。</summary>
    public bool ShowTest()
    {
        if (!_registered)
        {
            return false;
        }

        var builder = Apply(new AppNotificationBuilder(), new ToastAction(ToastActionKind.OpenInbox))
            .AddText("测试通知", new AppNotificationTextProperties().SetMaxLines(1))
            .AddText("看到这条消息，说明新邮件通知已经可以正常工作。点击它会打开邮件窗口。");
        return Send(builder, "test") > 0;
    }

    /// <summary>首次关到托盘时的一次性提示。</summary>
    public bool ShowTrayHint()
    {
        if (!_registered)
        {
            return false;
        }

        var builder = Apply(new AppNotificationBuilder(), new ToastAction(ToastActionKind.OpenInbox))
            .AddText("邮件仍在后台运行", new AppNotificationTextProperties().SetMaxLines(1))
            .AddText("点托盘里的「邮件」图标可重新打开；右键可选同步或退出。");
        return Send(builder, "tray-hint") > 0;
    }

    /// <summary>窗口藏在托盘时发现新版本：点通知打开更新对话框。</summary>
    public bool ShowUpdateAvailable(string version)
    {
        if (!_registered)
        {
            return false;
        }

        var builder = Apply(new AppNotificationBuilder(), new ToastAction(ToastActionKind.Update))
            .AddText($"邮件有新版本 {version}", new AppNotificationTextProperties().SetMaxLines(1))
            .AddText("点这里查看更新内容并更新。");
        return Send(builder, "update") > 0;
    }

    private static AppNotificationBuilder Apply(AppNotificationBuilder builder, ToastAction action)
    {
        foreach (var (k, v) in action.ToArguments())
        {
            builder.AddArgument(k, v);
        }

        return builder;
    }

    private static AppNotificationButton Button(string text, ToastAction action)
    {
        var button = new AppNotificationButton(text);
        foreach (var (k, v) in action.ToArguments())
        {
            button.AddArgument(k, v);
        }

        return button;
    }

    private int Send(AppNotificationBuilder builder, string tagSource)
    {
        try
        {
            var notification = builder.BuildNotification();
            notification.Group = Group;
            notification.Tag = TagFor(tagSource);
            AppNotificationManager.Default.Show(notification);
            return 1;
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>通知的 Tag 上限 64 字符；邮件 Id 可能很长，所以取哈希。同一封邮件重复弹会替换而不是叠加。</summary>
    private static string TagFor(string source)
        => "m" + Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(source)))[..24];

    /// <summary>邮件被读过 / 删除后，清掉它还留在操作中心里的通知。</summary>
    public async Task RemoveForMessageAsync(string messageId)
    {
        if (!_registered)
        {
            return;
        }

        try
        {
            await AppNotificationManager.Default.RemoveByTagAndGroupAsync(TagFor(messageId), Group);
        }
        catch
        {
            // 清理失败不影响功能
        }
    }
}