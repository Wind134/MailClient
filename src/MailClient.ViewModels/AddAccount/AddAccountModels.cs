using CommunityToolkit.Mvvm.ComponentModel;
using MailClient.Providers.Setup;

namespace MailClient.ViewModels.AddAccount;

public enum AddAccountStep
{
    PickProvider,
    Credentials,
    Verify,
    Finish
}

/// <summary>步骤 1 里的一个服务商磁贴。</summary>
public sealed record ProviderTileViewModel(
    SetupProviderKind Kind,
    string Title,
    string Subtitle,
    string Glyph,
    string BrandHex)
{
    /// <summary>列表项的自动化名称（屏幕阅读器 / UI 自动化读到的是标题而不是记录的默认 ToString）。</summary>
    public override string ToString() => Title;
}

/// <summary>授权码获取说明（QQ / 网易）。</summary>
public sealed record AuthCodeHelp(string Title, IReadOnlyList<string> Steps, string LinkText, string LinkUrl)
{
    public static AuthCodeHelp? For(SetupProviderKind kind) => kind switch
    {
        SetupProviderKind.Qq => new AuthCodeHelp(
            "QQ 邮箱需要使用「授权码」登录（不是 QQ 密码）",
            [
                "① 登录网页版 QQ 邮箱（mail.qq.com）。",
                "② 点击「设置」→「账号与安全」（旧版为「账户」）。",
                "③ 在「POP3/IMAP/SMTP 服务」处开启 IMAP/SMTP 服务，按提示用手机验证。",
                "④ 点击「生成授权码」，复制 16 位授权码，粘贴到下面的「授权码」框。"
            ],
            "查看 QQ 邮箱官方说明",
            "https://help.mail.qq.com/detail/0/985"),
        SetupProviderKind.NetEase => new AuthCodeHelp(
            "网易邮箱需要使用「客户端授权码」登录（不是网页登录密码）",
            [
                "① 登录网页版网易邮箱（mail.163.com / mail.126.com）。",
                "② 点击「设置」→「POP3/SMTP/IMAP」。",
                "③ 开启「IMAP/SMTP 服务」，并点击「新增授权密码」，按提示完成手机验证。",
                "④ 复制页面上只显示一次的授权码，粘贴到下面的「授权码」框。"
            ],
            "查看网易邮箱官方说明",
            "https://help.mail.163.com/faqDetail.do?code=d7a5dc8471cd0c0e8b4b8f4f8e49998b374173cfe9171305fa1ce630d7f67ac2a5feb28b66796d3b"),
        _ => null
    };
}

/// <summary>验证步骤里的一行（IMAP / SMTP / Graph）。</summary>
public sealed partial class VerifyItemViewModel : ObservableObject
{
    public VerifyItemViewModel(SetupStage stage, string title)
    {
        Stage = stage;
        Title = title;
    }

    public SetupStage Stage { get; }
    public string Title { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsPending), nameof(IsRunning), nameof(IsSucceeded), nameof(IsFailed), nameof(StatusText))]
    private SetupStageStatus? _status;

    [ObservableProperty]
    private string? _message;

    public bool IsPending => Status is null;
    public bool IsRunning => Status == SetupStageStatus.Running;
    public bool IsSucceeded => Status == SetupStageStatus.Succeeded;
    public bool IsFailed => Status == SetupStageStatus.Failed;

    public string StatusText => Status switch
    {
        SetupStageStatus.Running => "正在验证…",
        SetupStageStatus.Succeeded => "通过",
        SetupStageStatus.Failed => "失败",
        _ => "等待中"
    };
}

/// <summary>同步调用回调的 IProgress（<see cref="Progress{T}"/> 会 Post 到线程池，测试里不确定）。</summary>
internal sealed class InlineProgress<T> : IProgress<T>
{
    private readonly Action<T> _handler;

    public InlineProgress(Action<T> handler) => _handler = handler;

    public void Report(T value) => _handler(value);
}
