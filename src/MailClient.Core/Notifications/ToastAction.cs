namespace MailClient.Core.Notifications;

public enum ToastActionKind
{
    /// <summary>点通知正文：打开这封邮件。</summary>
    Open,
    /// <summary>通知上的“标为已读”按钮。</summary>
    MarkRead,
    /// <summary>通知上的“删除”按钮。</summary>
    Delete,
    /// <summary>点汇总通知：打开收件箱。</summary>
    OpenInbox,
    /// <summary>点「邮件有新版本」通知：打开更新对话框。</summary>
    Update
}

/// <summary>
/// 通知被点击时带回来的动作（序列化成通知的 launch / 按钮参数，应用冷启动或已运行时都靠它还原）。
/// 纯数据 + 解析逻辑，不依赖 Windows API，便于单元测试。
/// </summary>
public sealed record ToastAction(ToastActionKind Kind, Guid? AccountId = null, string? MessageId = null)
{
    public const string KeyAction = "action";
    public const string KeyAccount = "accountId";
    public const string KeyMessage = "messageId";

    /// <summary>按钮类动作可以不显示主窗口，在后台完成（冷启动时尤其如此）。</summary>
    public bool RunsInBackground => Kind is ToastActionKind.MarkRead or ToastActionKind.Delete;

    public static string KindToText(ToastActionKind kind) => kind switch
    {
        ToastActionKind.Open => "open",
        ToastActionKind.MarkRead => "markRead",
        ToastActionKind.Delete => "delete",
        ToastActionKind.Update => "update",
        _ => "openInbox"
    };

    public IReadOnlyDictionary<string, string> ToArguments()
    {
        var d = new Dictionary<string, string> { [KeyAction] = KindToText(Kind) };
        if (AccountId is { } a)
        {
            d[KeyAccount] = a.ToString("D");
        }

        if (!string.IsNullOrEmpty(MessageId))
        {
            d[KeyMessage] = MessageId;
        }

        return d;
    }

    /// <summary>解析通知参数；没有 action 键（例如点了通知但参数为空）时返回 null。需要邮件的动作缺少邮件 Id 时退化为“打开收件箱”。</summary>
    public static ToastAction? TryParse(IEnumerable<KeyValuePair<string, string>>? arguments)
    {
        if (arguments is null)
        {
            return null;
        }

        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (k, v) in arguments)
        {
            map[k] = v;
        }

        if (!map.TryGetValue(KeyAction, out var text) || string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        Guid? account = map.TryGetValue(KeyAccount, out var a) && Guid.TryParse(a, out var g) ? g : null;
        map.TryGetValue(KeyMessage, out var message);
        if (string.IsNullOrEmpty(message))
        {
            message = null;
        }

        var kind = text.Trim().ToLowerInvariant() switch
        {
            "open" => ToastActionKind.Open,
            "markread" => ToastActionKind.MarkRead,
            "delete" => ToastActionKind.Delete,
            "openinbox" => ToastActionKind.OpenInbox,
            "update" => ToastActionKind.Update,
            _ => (ToastActionKind?)null
        };
        if (kind is null)
        {
            return null;
        }

        if (kind is ToastActionKind.Open or ToastActionKind.MarkRead or ToastActionKind.Delete && message is null)
        {
            kind = ToastActionKind.OpenInbox;
        }

        return new ToastAction(kind.Value, account, message);
    }
}