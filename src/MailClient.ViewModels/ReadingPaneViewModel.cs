using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MailClient.Core;
using MailClient.Core.Search;
using MailClient.Core.Services;
using MailClient.Storage;

namespace MailClient.ViewModels;

public enum MessageMutation
{
    Read,
    Unread,
    Flagged,
    Unflagged,
    Archived,
    Deleted
}

public sealed record MessageMutationEventArgs(string MessageId, MessageMutation Mutation);

/// <summary>右栏：阅读窗格。</summary>
public sealed partial class ReadingPaneViewModel : ObservableObject
{
    private readonly MailDatabase _database;
    private readonly IMessageBodyLoader? _bodyLoader;
    private MessageEntity? _entity;
    private IReadOnlyList<RecipientEntity> _recipients = Array.Empty<RecipientEntity>();
    private readonly MessageActions _actions;
    private SynchronizationContext? _ui = SynchronizationContext.Current;
    private int _loadVersion;

    /// <summary>在 UI 线程就绪后注入。正文和已读状态都从后台回来，不能直接改绑定。</summary>
    public void AttachSynchronizationContext(SynchronizationContext? context = null)
        => _ui = context ?? SynchronizationContext.Current ?? _ui;

    public ReadingPaneViewModel(
        MailDatabase database,
        MailboxTreeViewModel tree,
        MessageActions? actions = null,
        IMessageBodyLoader? bodyLoader = null)
    {
        _database = database;
        _bodyLoader = bodyLoader;
        _actions = actions ?? new MessageActions(database, tree);
        _actions.Mutated += (_, e) => Post(() => OnActionMutated(e));
    }

    private void Post(Action action) => UiMarshal.Post(_ui, action);

    /// <summary>打开未读邮件后，停留多久才标记为已读（快速浏览时不会把路过的邮件都标成已读）。</summary>
    public TimeSpan MarkReadDelay { get; set; } = TimeSpan.FromMilliseconds(600);

    private void OnActionMutated(MessageMutationEventArgs e)
    {
        if (_entity is null || e.MessageId != _entity.Id)
        {
            return;
        }

        switch (e.Mutation)
        {
            case MessageMutation.Flagged:
            case MessageMutation.Unflagged:
                IsFlagged = e.Mutation == MessageMutation.Flagged;
                FlagLabel = IsFlagged ? "取消标记" : "标记";
                break;
            case MessageMutation.Archived:
            case MessageMutation.Deleted:
                Clear();
                break;
        }
    }

    public ObservableCollection<AttachmentItemViewModel> Attachments { get; } = new();

    [ObservableProperty]
    private bool _hasMessage;

    [ObservableProperty]
    private string _subject = string.Empty;

    [ObservableProperty]
    private string _fromName = string.Empty;

    [ObservableProperty]
    private string _fromAddress = string.Empty;

    [ObservableProperty]
    private string _initials = string.Empty;

    [ObservableProperty]
    private string _toText = string.Empty;

    [ObservableProperty]
    private string _ccText = string.Empty;

    [ObservableProperty]
    private string _dateText = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayBodyHtml))]
    private string? _bodyHtml;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(BodyHighlight))]
    private string _bodyText = string.Empty;

    /// <summary>当前搜索的命中词；设置后正文纯文本 / HTML 里的命中处会高亮。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(BodyHighlight), nameof(DisplayBodyHtml))]
    private IReadOnlyList<string> _highlightTerms = Array.Empty<string>();

    public HighlightedText BodyHighlight => HighlightedText.Of(BodyText, HighlightTerms);

    /// <summary>HTML 正文；有搜索词时在文本节点里给命中处加 &lt;mark&gt;。</summary>
    public string? DisplayBodyHtml
        => string.IsNullOrEmpty(BodyHtml) || HighlightTerms.Count == 0 ? BodyHtml : HtmlHighlighter.Apply(BodyHtml, HighlightTerms);

    /// <summary>当前打开的是一份草稿（本地或服务器同步的）：显示“继续编辑”，隐藏回复 / 转发。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsNotDraft))]
    private bool _isDraft;

    public bool IsNotDraft => !IsDraft;

    [ObservableProperty]
    private bool _hasAttachments;

    [ObservableProperty]
    private bool _isFlagged;

    [ObservableProperty]
    private string _flagLabel = "标记";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowBodyContent))]
    private bool _isBodyLoading;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasBodyLoadError), nameof(ShowBodyContent))]
    private string? _bodyLoadError;

    public bool HasBodyLoadError => !string.IsNullOrEmpty(BodyLoadError);

    /// <summary>有正文可显示（不在加载中、且没有挡住正文的错误，或已有缓存正文）。</summary>
    public bool ShowBodyContent => HasMessage && !IsBodyLoading;

    public bool IsEmpty => !HasMessage;

    public string? CurrentMessageId => _entity?.Id;

    partial void OnHasMessageChanged(bool value) => OnPropertyChanged(nameof(IsEmpty));

    public event EventHandler<ComposeRequest>? ComposeRequested;

    /// <summary>点“继续编辑”：请求打开当前草稿（由 Shell 负责取出草稿内容并打开写信窗口）。</summary>
    public event EventHandler<string>? EditDraftRequested;

    /// <summary>在阅读窗格里删除一份本地草稿。</summary>
    public event EventHandler<string>? DeleteDraftRequested;

    /// <summary>请求把附件存到用户选择的路径（App 层弹保存对话框）。</summary>
    public event EventHandler<AttachmentSaveRequest>? AttachmentSaveRequested;

    [RelayCommand]
    private void EditDraft()
    {
        if (_entity is { IsDraft: true })
        {
            EditDraftRequested?.Invoke(this, _entity.Id);
        }
    }

    /// <summary>记录“刚看过这封邮件的正文”，让它不被当成陈旧缓存清掉（失败无所谓）。</summary>
    private void TouchBodyAccess(string messageId)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await _database.TouchBodyAccessAsync(messageId).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // 记录访问时间失败不影响阅读
            }
        });
    }

    public async Task LoadAsync(string? messageId)
    {
        var version = Interlocked.Increment(ref _loadVersion);
        if (messageId is null)
        {
            Post(Clear);
            return;
        }

        var entity = await _database.GetMessageAsync(messageId).ConfigureAwait(false);
        if (entity is null || version != _loadVersion)
        {
            if (entity is null)
            {
                Post(() =>
                {
                    if (version == Volatile.Read(ref _loadVersion))
                    {
                        Clear();
                    }
                });
            }

            return;
        }

        var recipients = await _database.ListRecipientsAsync(messageId).ConfigureAwait(false);
        var attachments = await _database.ListAttachmentsAsync(messageId).ConfigureAwait(false);
        Post(() =>
        {
            if (version != Volatile.Read(ref _loadVersion))
            {
                return;
            }

            ApplyHeader(entity, recipients, attachments);

            if (entity.BodyFetched)
            {
                TouchBodyAccess(entity.Id);
                ApplyBody(entity);
                IsBodyLoading = false;
                BodyLoadError = null;
                // 旧缓存里的 cid: 内嵌图：后台静默再拉并改写，不闪加载态
                if (CidHtmlRewriter.ContainsUnresolvedCid(entity.BodyHtml))
                {
                    _ = EnsureBodyAsync(entity.Id, version);
                }
            }
            else
            {
                // 不要把 Preview 当成正文：显示加载态，后台去拉完整内容
                BodyHtml = null;
                BodyText = string.Empty;
                IsBodyLoading = true;
                BodyLoadError = null;
                _ = EnsureBodyAsync(entity.Id, version);
            }

            if (!entity.IsRead)
            {
                _ = MarkReadAfterDelayAsync(entity.Id, version);
            }
        });
    }

    private void ApplyHeader(MessageEntity entity, IReadOnlyList<RecipientEntity> recipients, IReadOnlyList<AttachmentEntity> attachments)
    {
        _entity = entity;
        _recipients = recipients;

        Subject = string.IsNullOrWhiteSpace(entity.Subject) ? UiStrings.NoSubject : entity.Subject;
        FromName = string.IsNullOrWhiteSpace(entity.FromName) ? entity.FromAddress : entity.FromName!;
        FromAddress = entity.FromAddress;
        Initials = MessageRowViewModel.MakeInitials(FromName);
        ToText = FormatRecipients(recipients, RecipientKind.To);
        CcText = FormatRecipients(recipients, RecipientKind.Cc);
        DateText = entity.ReceivedAt is { } d ? TimeFormatter.FormatFull(d) : string.Empty;
        IsDraft = entity.IsDraft;
        IsFlagged = entity.IsFlagged;
        FlagLabel = entity.IsFlagged ? "取消标记" : "标记";

        Attachments.Clear();
        foreach (var a in attachments.Where(x => !x.IsInline))
        {
            Attachments.Add(new AttachmentItemViewModel(a, DownloadAttachmentCommand));
        }

        HasAttachments = Attachments.Count > 0;
        HasMessage = true;
    }

    private void ApplyBody(MessageEntity entity)
    {
        BodyHtml = entity.BodyHtml;
        // 已缓存：只用真正的正文，绝不用 Preview 冒充
        BodyText = entity.BodyText ?? string.Empty;
    }

    private async Task EnsureBodyAsync(string messageId, int version)
    {
        if (_bodyLoader is null)
        {
            // 测试 / 无网络场景：退回预览，并标明不是全文
            var local = await _database.GetMessageAsync(messageId).ConfigureAwait(false);
            Post(() =>
            {
                if (local is null || version != Volatile.Read(ref _loadVersion) || _entity?.Id != messageId)
                {
                    return;
                }

                IsBodyLoading = false;
                BodyText = string.IsNullOrEmpty(local.BodyText) ? local.Preview : local.BodyText!;
                BodyHtml = local.BodyHtml;
                BodyLoadError = "当前环境无法向服务器拉取完整正文。";
            });
            return;
        }

        try
        {
            await _bodyLoader.EnsureLoadedAsync(messageId).ConfigureAwait(false);
            var latest = await _database.GetMessageAsync(messageId).ConfigureAwait(false);
            var attachments = await _database.ListAttachmentsAsync(messageId).ConfigureAwait(false);
            Post(() =>
            {
                if (latest is null || version != Volatile.Read(ref _loadVersion) || _entity?.Id != messageId)
                {
                    return;
                }

                _entity = latest;
                ApplyBody(latest);
                Attachments.Clear();
                foreach (var a in attachments.Where(x => !x.IsInline))
                {
                    Attachments.Add(new AttachmentItemViewModel(a, DownloadAttachmentCommand));
                }

                HasAttachments = Attachments.Count > 0;
                IsBodyLoading = false;
                BodyLoadError = null;
            });
        }
        catch (Exception ex)
        {
            var error = FriendlyBodyError(ex);
            Post(() =>
            {
                if (version != Volatile.Read(ref _loadVersion) || _entity?.Id != messageId)
                {
                    return;
                }

                IsBodyLoading = false;
                BodyLoadError = error;
                // 失败时仍展示预览，避免空白，并在错误条说明这不是全文
                if (_entity is { } entity && string.IsNullOrEmpty(BodyText))
                {
                    BodyText = entity.Preview;
                }
            });
        }
    }

    [RelayCommand]
    private async Task RetryLoadBodyAsync()
    {
        if (_entity is null)
        {
            return;
        }

        var version = Interlocked.Increment(ref _loadVersion);
        // 保持同一封邮件的 version 语义：重新 Load 头 + 正文
        var id = _entity.Id;
        BodyLoadError = null;
        IsBodyLoading = true;
        BodyHtml = null;
        BodyText = string.Empty;
        // 恢复 version 关联：EnsureBody 用新 version；但 HasMessage 仍在
        // 上面 Increment 会使进行中的 MarkRead 取消——可接受
        _ = EnsureBodyAsync(id, version);
        await Task.CompletedTask;
    }

    [RelayCommand]
    private void DownloadAttachment(AttachmentItemViewModel? item)
    {
        if (item is null || string.IsNullOrEmpty(item.Id))
        {
            return;
        }

        AttachmentSaveRequested?.Invoke(this, new AttachmentSaveRequest(item.Id, item.FileName));
    }

    /// <summary>由 App 层在用户选好保存路径后调用。</summary>
    public async Task SaveAttachmentToAsync(string attachmentId, string destinationPath)
    {
        if (_bodyLoader is null)
        {
            throw new InvalidOperationException("当前环境无法下载附件。");
        }

        await _bodyLoader.DownloadAttachmentAsync(attachmentId, destinationPath).ConfigureAwait(false);
        // 刷新 LocalPath 显示（如有）
        var attachments = await _database.ListAttachmentsAsync(_entity?.Id ?? string.Empty).ConfigureAwait(false);
        Post(() =>
        {
            Attachments.Clear();
            foreach (var a in attachments.Where(x => !x.IsInline))
            {
                Attachments.Add(new AttachmentItemViewModel(a, DownloadAttachmentCommand));
            }

            HasAttachments = Attachments.Count > 0;
        });
    }

    private static string FriendlyBodyError(Exception ex)
    {
        var msg = ex.Message ?? string.Empty;
        // 裁剪发布关掉反射 STJ 时的典型文案；不应再出现，兜底避免把长英文堆给用户
        if (msg.Contains("Reflection-based serialization", StringComparison.OrdinalIgnoreCase) ||
            msg.Contains("TypeInfoResolver", StringComparison.OrdinalIgnoreCase) ||
            msg.Contains("JsonSerializerContext", StringComparison.OrdinalIgnoreCase))
        {
            return "无法加载正文：应用打包配置异常（JSON），请更新到最新版本后重试。";
        }

        if (msg.Contains("认证", StringComparison.Ordinal) ||
            msg.Contains("login", StringComparison.OrdinalIgnoreCase) ||
            msg.Contains("auth", StringComparison.OrdinalIgnoreCase) ||
            msg.Contains("Msal", StringComparison.OrdinalIgnoreCase))
        {
            return "无法加载正文：账号需要重新登录。";
        }

        if (msg.Contains("网络", StringComparison.Ordinal) ||
            msg.Contains("network", StringComparison.OrdinalIgnoreCase) ||
            msg.Contains("timeout", StringComparison.OrdinalIgnoreCase) ||
            msg.Contains("连接", StringComparison.Ordinal))
        {
            return "无法加载正文：网络异常，请检查网络后重试。";
        }

        // 截断过长异常，避免黄条刷屏
        if (msg.Length > 180)
        {
            msg = msg[..177] + "…";
        }

        return "无法加载正文：" + (string.IsNullOrWhiteSpace(msg) ? ex.GetType().Name : msg);
    }

    /// <summary>
    /// 同步之后刷新当前打开邮件的状态：服务器上被删除 / 移走则清空阅读窗格；星标变了就更新按钮。
    /// 不重新渲染正文，所以不会闪烁，也不会重新触发“延迟标已读”。
    /// </summary>
    public async Task RefreshStateAsync()
    {
        var current = _entity;
        if (current is null)
        {
            return;
        }

        var latest = await _database.GetMessageAsync(current.Id).ConfigureAwait(false);
        Post(() =>
        {
            if (!ReferenceEquals(_entity, current))
            {
                return; // 期间切换了邮件
            }

            if (latest is null)
            {
                Clear();
                return;
            }

            _entity = latest;
            if (latest.IsFlagged != IsFlagged)
            {
                IsFlagged = latest.IsFlagged;
                FlagLabel = IsFlagged ? "取消标记" : "标记";
            }
        });
    }

    private async Task MarkReadAfterDelayAsync(string messageId, int version)
    {
        try
        {
            if (MarkReadDelay > TimeSpan.Zero)
            {
                await Task.Delay(MarkReadDelay).ConfigureAwait(false);
            }

            // 期间切换了邮件 / 已手动标记过：不再自动标已读
            if (version != Volatile.Read(ref _loadVersion) || _entity?.Id != messageId)
            {
                return;
            }

            await _actions.SetReadAsync(messageId, true).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // 后台自动已读失败不打扰用户
        }
    }

    private void Clear()
    {
        _entity = null;
        _recipients = Array.Empty<RecipientEntity>();
        HasMessage = false;
        Subject = FromName = FromAddress = ToText = CcText = DateText = BodyText = string.Empty;
        BodyHtml = null;
        IsDraft = false;
        Attachments.Clear();
        HasAttachments = false;
        IsBodyLoading = false;
        BodyLoadError = null;
    }

    private static string FormatRecipients(IEnumerable<RecipientEntity> all, RecipientKind kind)
        => string.Join("，", all.Where(r => r.Kind == kind).Select(r => string.IsNullOrWhiteSpace(r.Name) ? r.Address : r.Name));

    private string MyAddress()
        => _recipients.FirstOrDefault(r => r.Kind == RecipientKind.To)?.Address ?? string.Empty;

    [RelayCommand]
    private void Reply() => Compose(all: false, forward: false);

    [RelayCommand]
    private void ReplyAll() => Compose(all: true, forward: false);

    [RelayCommand]
    private void Forward() => Compose(all: false, forward: true);

    private void Compose(bool all, bool forward)
    {
        if (_entity is null)
        {
            return;
        }

        var request = forward
            ? ComposeRequest.Forward(_entity)
            : ComposeRequest.Reply(_entity, _recipients, MyAddress(), all);
        ComposeRequested?.Invoke(this, request);
    }

    [RelayCommand]
    private async Task ToggleFlagAsync()
    {
        if (_entity is null)
        {
            return;
        }

        await _actions.SetFlaggedAsync(_entity.Id, !IsFlagged);
    }

    [RelayCommand]
    private async Task MarkUnreadAsync()
    {
        if (_entity is null)
        {
            return;
        }

        // 取消尚未触发的“延迟标已读”
        Interlocked.Increment(ref _loadVersion);
        await _actions.SetReadAsync(_entity.Id, false);
    }

    [RelayCommand]
    private async Task ArchiveAsync()
    {
        if (_entity is not null)
        {
            await _actions.ArchiveAsync(_entity.Id);
        }
    }

    /// <summary>删除：不在已删除文件夹时移入已删除；已在其中则永久删除。</summary>
    [RelayCommand]
    private async Task DeleteAsync()
    {
        if (_entity is null)
        {
            return;
        }

        if (MessageActions.IsLocalDraft(_entity.Id))
        {
            // 本地草稿删除不可恢复：交给 Shell（确认 + 关掉已打开的写信窗口）
            DeleteDraftRequested?.Invoke(this, _entity.Id);
            return;
        }

        await _actions.DeleteAsync(_entity.Id);
    }
}

/// <summary>用户要把某个附件存盘。</summary>
public sealed record AttachmentSaveRequest(string AttachmentId, string SuggestedFileName);
