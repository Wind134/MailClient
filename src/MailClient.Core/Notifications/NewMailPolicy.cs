using System.Text;

namespace MailClient.Core.Notifications;

/// <summary>同步之后“可能要弹通知”的一封邮件（只含通知需要的字段）。</summary>
public sealed record NewMailCandidate(
    string MessageId,
    Guid AccountId,
    string AccountName,
    SpecialFolder Folder,
    string FromAddress,
    string? FromName,
    string Subject,
    string Preview,
    bool IsRead,
    bool IsDraft,
    DateTimeOffset? ReceivedAt)
{
    /// <summary>通知标题里显示的发件人：优先显示名，其次地址。</summary>
    public string SenderDisplay => !string.IsNullOrWhiteSpace(FromName)
        ? FromName!.Trim()
        : !string.IsNullOrWhiteSpace(FromAddress) ? FromAddress.Trim() : "未知发件人";

    public string SubjectDisplay => string.IsNullOrWhiteSpace(Subject) ? "（无主题）" : Subject.Trim();

    /// <summary>预览：合并空白并截断，通知里只放一两行。</summary>
    public string PreviewDisplay => NewMailPolicy.Shorten(Preview, 140);
}

/// <summary>多封新邮件合并成的一条汇总通知（“你有 N 封新邮件”）。</summary>
public sealed record NewMailSummary(int Count, string Title, string Body, Guid? AccountId);

/// <summary>一次同步产生的通知方案：要么逐封弹（封数不多），要么只弹一条汇总。</summary>
public sealed record NewMailToastPlan(IReadOnlyList<NewMailCandidate> Individual, NewMailSummary? Summary)
{
    public static NewMailToastPlan Empty { get; } = new(Array.Empty<NewMailCandidate>(), null);

    public int ToastCount => Summary is null ? Individual.Count : 1;
}

/// <summary>
/// “哪些邮件该弹通知”的纯逻辑（不碰数据库 / 界面，便于单元测试）。规则：
/// 只通知同步<b>之后新出现在收件箱</b>、<b>未读</b>、不是草稿、<b>不是自己发的</b>、不太旧的邮件；
/// 新加账号的首次完整同步、以及用户关掉通知时一律不通知。
/// </summary>
public static class NewMailPolicy
{
    /// <summary>超过一定天数的“新同步到”的邮件（例如从别的文件夹移回收件箱）不再提醒。</summary>
    public static readonly TimeSpan MaxAge = TimeSpan.FromDays(2);

    /// <summary>一次最多逐封弹几条；更多则合并成一条汇总，避免刷屏。</summary>
    public const int MaxIndividualToasts = 3;

    public static IReadOnlyList<NewMailCandidate> Select(
        IEnumerable<NewMailCandidate> inboxAfterSync,
        IReadOnlySet<string> knownBeforeSync,
        bool initialSync,
        bool notificationsEnabled,
        IReadOnlySet<string> ownAddresses,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(inboxAfterSync);
        ArgumentNullException.ThrowIfNull(knownBeforeSync);
        ArgumentNullException.ThrowIfNull(ownAddresses);
        if (!notificationsEnabled || initialSync)
        {
            return Array.Empty<NewMailCandidate>();
        }

        return inboxAfterSync
            .Where(m => IsNotifiable(m, knownBeforeSync, ownAddresses, now))
            .OrderByDescending(m => m.ReceivedAt ?? DateTimeOffset.MinValue)
            .ToList();
    }

    public static bool IsNotifiable(NewMailCandidate m, IReadOnlySet<string> knownBeforeSync, IReadOnlySet<string> ownAddresses, DateTimeOffset now)
    {
        if (m.Folder != SpecialFolder.Inbox || m.IsRead || m.IsDraft)
        {
            return false;
        }

        if (knownBeforeSync.Contains(m.MessageId))
        {
            return false;
        }

        if (IsOwnAddress(m.FromAddress, ownAddresses))
        {
            return false;
        }

        return m.ReceivedAt is not { } received || now - received <= MaxAge;
    }

    private static bool IsOwnAddress(string? from, IReadOnlySet<string> ownAddresses)
        => !string.IsNullOrWhiteSpace(from) && ownAddresses.Contains(from.Trim().ToLowerInvariant());

    /// <summary>把自己的邮箱地址集合规范化（小写 + 去空白）。</summary>
    public static IReadOnlySet<string> NormalizeAddresses(IEnumerable<string?> addresses)
        => addresses.Where(a => !string.IsNullOrWhiteSpace(a)).Select(a => a!.Trim().ToLowerInvariant()).ToHashSet(StringComparer.Ordinal);

    /// <summary>≤ <paramref name="maxIndividual"/> 封逐封弹；更多则合并成一条汇总。</summary>
    public static NewMailToastPlan Plan(IReadOnlyList<NewMailCandidate> selected, int maxIndividual = MaxIndividualToasts)
    {
        if (selected.Count == 0)
        {
            return NewMailToastPlan.Empty;
        }

        if (selected.Count <= Math.Max(1, maxIndividual))
        {
            return new NewMailToastPlan(selected, null);
        }

        var senders = selected.Select(m => m.SenderDisplay).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var shown = string.Join("、", senders.Take(3));
        var body = senders.Count > 3 ? $"来自 {shown} 等 {senders.Count} 位发件人" : $"来自 {shown}";
        var accounts = selected.Select(m => m.AccountId).Distinct().ToList();
        return new NewMailToastPlan(
            Array.Empty<NewMailCandidate>(),
            new NewMailSummary(selected.Count, $"你有 {selected.Count} 封新邮件", body, accounts.Count == 1 ? accounts[0] : null));
    }

    /// <summary>合并空白 / 换行并按字符数截断（加省略号）；按“文本元素”截，不会把代理对劈开。</summary>
    public static string Shorten(string? text, int max)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var sb = new StringBuilder(text.Length);
        var lastSpace = true;
        foreach (var ch in text)
        {
            if (char.IsWhiteSpace(ch) || char.IsControl(ch))
            {
                if (!lastSpace)
                {
                    sb.Append(' ');
                    lastSpace = true;
                }
            }
            else
            {
                sb.Append(ch);
                lastSpace = false;
            }
        }

        var s = sb.ToString().Trim();
        if (s.Length <= max)
        {
            return s;
        }

        var cut = max;
        if (char.IsHighSurrogate(s[cut - 1]))
        {
            cut--;
        }

        return s[..cut].TrimEnd() + "…";
    }
}

/// <summary>某个账号一次同步产出的“新邮件”事件。</summary>
public sealed class NewMailEventArgs : EventArgs
{
    public NewMailEventArgs(Guid accountId, string accountName, IReadOnlyList<NewMailCandidate> messages)
    {
        AccountId = accountId;
        AccountName = accountName;
        Messages = messages;
    }

    public Guid AccountId { get; }
    public string AccountName { get; }
    public IReadOnlyList<NewMailCandidate> Messages { get; }
}