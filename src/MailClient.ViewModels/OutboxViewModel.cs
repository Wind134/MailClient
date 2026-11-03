using System.Collections.ObjectModel;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using MailClient.Core;
using MailClient.Storage;

namespace MailClient.ViewModels;

/// <summary>发件箱列表里的一行：收件人 / 主题 / 状态 / 错误原因。</summary>
public sealed partial class OutboxItemViewModel : ObservableObject
{
    public OutboxItemViewModel(OutboxEntity entity, string accountDisplay, DateTimeOffset now)
    {
        Id = entity.Id;
        Status = entity.Status;
        AccountDisplay = accountDisplay;
        var message = TryParse(entity.PayloadJson);
        Subject = string.IsNullOrWhiteSpace(message?.Subject) ? UiStrings.NoSubject : message!.Subject;
        Recipients = message is null
            ? string.Empty
            : string.Join("; ", message.To.Concat(message.Cc).Concat(message.Bcc));
        AttachmentCount = message?.AttachmentPaths?.Count ?? 0;
        TimeText = TimeFormatter.FormatShort(entity.CreatedAt, now);
        (StatusText, Glyph) = entity.Status switch
        {
            OutboxStatus.Sending => ("发送中…", "\uE895"),
            OutboxStatus.Failed => ("发送失败", "\uEA39"),
            OutboxStatus.Sent => (entity.SentCopyPending ? "已发送，正在保存到已发送" : "已发送", "\uE73E"),
            _ => (PendingText(entity, now), "\uE916")
        };
        ErrorText = entity.LastError ?? string.Empty;
        CanRetry = entity.Status is OutboxStatus.Failed or OutboxStatus.Pending && (entity.Status == OutboxStatus.Failed || entity.Attempts > 0);
        CanDelete = entity.Status != OutboxStatus.Sending;
    }

    public string Id { get; }
    public OutboxStatus Status { get; }
    public string AccountDisplay { get; }
    public string Subject { get; }
    public string Recipients { get; }
    public int AttachmentCount { get; }
    public string TimeText { get; }
    public string StatusText { get; }
    public string Glyph { get; }
    public string ErrorText { get; }
    public bool HasError => !string.IsNullOrWhiteSpace(ErrorText);
    public bool IsFailed => Status == OutboxStatus.Failed;
    public bool CanRetry { get; }
    public bool CanDelete { get; }

    public string RecipientsLine => string.IsNullOrEmpty(Recipients) ? "（无收件人）" : "收件人：" + Recipients;

    private static string PendingText(OutboxEntity e, DateTimeOffset now)
    {
        if (e.Attempts > 0 && e.NextAttemptAt is { } next && next > now)
        {
            return $"第 {e.Attempts} 次发送失败，将在 {TimeFormatter.FormatClock(next.ToLocalTime())} 重试";
        }

        return e.Attempts > 0 ? "等待重试" : "等待发送";
    }

    private static OutgoingMailMessage? TryParse(string json)
    {
        try
        {
            return JsonSerializer.Deserialize(json, MailClientJsonContext.Default.OutgoingMailMessage);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

/// <summary>发件箱视图：列出待发 / 发送中 / 失败的邮件，支持重试和删除。</summary>
public sealed partial class OutboxViewModel : ObservableObject
{
    private readonly MailDatabase _database;
    private readonly Func<DateTimeOffset> _clock;

    public OutboxViewModel(MailDatabase database, Func<DateTimeOffset>? clock = null)
    {
        _database = database;
        _clock = clock ?? (() => DateTimeOffset.Now);
    }

    public ObservableCollection<OutboxItemViewModel> Items { get; } = new();

    [ObservableProperty]
    private bool _isEmpty = true;

    public async Task ReloadAsync()
    {
        var items = await _database.ListOutboxItemsAsync();
        var accounts = (await _database.ListAccountsAsync()).ToDictionary(a => a.Id, a => $"{a.DisplayName} <{a.EmailAddress}>");
        var now = _clock();
        Items.Clear();
        foreach (var item in items)
        {
            Items.Add(new OutboxItemViewModel(item, accounts.GetValueOrDefault(item.AccountId, string.Empty), now));
        }

        IsEmpty = Items.Count == 0;
    }

    public async Task RetryAsync(string id)
    {
        await _database.RetryOutboxAsync(id, _clock());
        await ReloadAsync();
    }

    public async Task DeleteAsync(string id)
    {
        await _database.DeleteOutboxAsync(id);
        await ReloadAsync();
    }
}
