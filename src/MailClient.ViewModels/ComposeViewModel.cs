using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MailClient.Core;
using MailClient.Core.Sending;
using MailClient.Core.Services;

namespace MailClient.ViewModels;

/// <param name="ProviderId">账号的 ProviderId，用来决定附件大小上限；为空按普通 IMAP 处理。</param>
public sealed record ComposeAccountOption(Guid Id, string Display, string ProviderId = "", string EmailAddress = "")
{
    public long AttachmentLimit => AttachmentLimits.For(ProviderId, EmailAddress);
}

/// <summary>撰写窗口里的一个附件。</summary>
public sealed class ComposeAttachmentViewModel
{
    public ComposeAttachmentViewModel(string path, long size)
    {
        Path = path;
        Size = size;
        FileName = System.IO.Path.GetFileName(path);
        SizeText = TimeFormatter.FormatSize(size);
    }

    public string Path { get; }
    public long Size { get; }
    public string FileName { get; }
    public string SizeText { get; }
}

/// <summary>写信窗口。发送 = 写入 ISendService（outbox），不直接联网；真正的投递由后台发件箱 worker 完成。</summary>
public sealed partial class ComposeViewModel : ObservableObject
{
    private static readonly char[] Separators = [';', ',', '，', '；', ' ', '\n', '\t'];
    private readonly ISendService _sendService;
    private readonly IDraftStore? _drafts;
    private readonly ComposeRequest _request;
    private readonly string _draftId;
    private CancellationTokenSource? _draftDebounce;
    private bool _finished;
    private bool _loading = true;
    private bool _persisted;

    public ComposeViewModel(
        ISendService sendService,
        IEnumerable<ComposeAccountOption> accounts,
        ComposeRequest request,
        IDraftStore? drafts = null)
    {
        _sendService = sendService;
        _drafts = drafts;
        _request = request;
        _draftId = request.DraftId ?? Guid.NewGuid().ToString("N");
        foreach (var a in accounts)
        {
            Accounts.Add(a);
        }

        SelectedAccount = Accounts.FirstOrDefault(a => a.Id == request.AccountId) ?? Accounts.FirstOrDefault();
        _to = request.To;
        _cc = request.Cc;
        _bcc = request.Bcc;
        _subject = request.Subject;
        _body = request.Body;
        _showCc = !string.IsNullOrWhiteSpace(request.Cc) || !string.IsNullOrWhiteSpace(request.Bcc);
        WindowTitle = request.Mode switch
        {
            ComposeMode.Reply or ComposeMode.ReplyAll => "回复",
            ComposeMode.Forward => "转发",
            _ => "新邮件"
        };

        foreach (var path in request.Attachments ?? Array.Empty<string>())
        {
            AddAttachment(path);
        }

        _persisted = request.DraftId is not null;
        _loading = false;
    }

    public ObservableCollection<ComposeAccountOption> Accounts { get; } = new();
    public ObservableCollection<ComposeAttachmentViewModel> Attachments { get; } = new();
    public string WindowTitle { get; }

    /// <summary>草稿自动保存的去抖间隔。</summary>
    public TimeSpan DraftDelay { get; set; } = TimeSpan.FromSeconds(3);

    public ComposeMode Mode => _request.Mode;
    public bool HasQuote => _request.HasQuote;

    /// <summary>引用原文（只读展示，发送时附在正文后面）。</summary>
    public string QuotedPreview => _request.QuotedText?.Trim() ?? string.Empty;

    public string DraftId => _draftId;

    [ObservableProperty]
    private ComposeAccountOption? _selectedAccount;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SendCommand))]
    private string _to;

    [ObservableProperty]
    private string _cc;

    [ObservableProperty]
    private string _bcc;

    [ObservableProperty]
    private bool _showCc;

    [ObservableProperty]
    private string _subject;

    [ObservableProperty]
    private string _body;

    [ObservableProperty]
    private string _errorMessage = string.Empty;

    /// <summary>附件超过所选账号上限时的提示（只警告，不拦截）。</summary>
    [ObservableProperty]
    private string _attachmentWarning = string.Empty;

    [ObservableProperty]
    private string _attachmentSummary = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SendCommand))]
    private bool _isSending;

    public event EventHandler? Sent;

    public long AttachmentsTotalBytes => Attachments.Sum(a => a.Size);

    public static IReadOnlyList<string> ParseAddresses(string? text)
        => (text ?? string.Empty)
            .Split(Separators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    public static bool IsValidAddress(string address)
    {
        var at = address.IndexOf('@');
        return at > 0 && at < address.Length - 3 && address.IndexOf('.', at) > at + 1 && !address.Contains(' ');
    }

    // ───────── 附件 ─────────

    /// <summary>添加附件；文件不存在 / 已添加过则忽略并给出提示，返回是否添加成功。</summary>
    public bool AddAttachment(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists)
            {
                ErrorMessage = $"找不到文件：{System.IO.Path.GetFileName(path)}";
                return false;
            }

            if (Attachments.Any(a => string.Equals(a.Path, info.FullName, StringComparison.OrdinalIgnoreCase)))
            {
                return false;
            }

            Attachments.Add(new ComposeAttachmentViewModel(info.FullName, info.Length));
        }
        catch (Exception ex)
        {
            ErrorMessage = $"无法添加附件：{ex.Message}";
            return false;
        }

        UpdateAttachmentInfo();
        ScheduleDraftSave();
        return true;
    }

    public void RemoveAttachment(ComposeAttachmentViewModel attachment)
    {
        Attachments.Remove(attachment);
        UpdateAttachmentInfo();
        ScheduleDraftSave();
    }

    partial void OnSelectedAccountChanged(ComposeAccountOption? value)
    {
        UpdateAttachmentInfo();
        ScheduleDraftSave();
    }

    private void UpdateAttachmentInfo()
    {
        OnPropertyChanged(nameof(AttachmentsTotalBytes));
        var total = AttachmentsTotalBytes;
        AttachmentSummary = Attachments.Count == 0 ? string.Empty : $"{Attachments.Count} 个附件，共 {TimeFormatter.FormatSize(total)}";
        var limit = SelectedAccount?.AttachmentLimit ?? AttachmentLimits.For(string.Empty);
        if (total > limit)
        {
            var graph = string.Equals(SelectedAccount?.ProviderId, "microsoft.graph", StringComparison.OrdinalIgnoreCase);
            AttachmentWarning = graph
                ? $"附件总大小 {TimeFormatter.FormatSize(total)} 超过 Outlook 账号目前支持的 {TimeFormatter.FormatSize(limit)}（大附件上传尚未实现），发送会失败。请减少附件或改用其他账号。"
                : $"附件总大小 {TimeFormatter.FormatSize(total)} 超过该邮箱通常允许的 {TimeFormatter.FormatSize(limit)}，服务器可能会拒收。";
        }
        else
        {
            AttachmentWarning = string.Empty;
        }
    }

    // ───────── 草稿自动保存 ─────────

    partial void OnToChanged(string value) => ScheduleDraftSave();

    partial void OnCcChanged(string value) => ScheduleDraftSave();

    partial void OnBccChanged(string value) => ScheduleDraftSave();

    partial void OnSubjectChanged(string value) => ScheduleDraftSave();

    partial void OnBodyChanged(string value) => ScheduleDraftSave();

    /// <summary>有没有值得保存 / 丢弃前要确认的内容（正文、主题、收件人或附件）。</summary>
    public bool HasContent()
        => !string.IsNullOrWhiteSpace(Body) || !string.IsNullOrWhiteSpace(Subject) || !string.IsNullOrWhiteSpace(To)
           || !string.IsNullOrWhiteSpace(Cc) || !string.IsNullOrWhiteSpace(Bcc) || Attachments.Count > 0;

    /// <summary>点“丢弃”前是否需要确认（有内容，或这是一份已保存的草稿）。</summary>
    public bool NeedsDiscardConfirmation => HasContent() || _persisted;

    /// <summary>该窗口的单实例键：对应的本地草稿键，加上（若由服务器草稿打开）来源键；见 <see cref="ComposeRequest.WindowKeys"/>。</summary>
    public IReadOnlyList<string> WindowKeys
        => _request.SourceMessageId is { } src ? ["src:" + src, "draft:" + _draftId] : ["draft:" + _draftId];

    private void ScheduleDraftSave()
    {
        if (_drafts is null || _finished || _loading)
        {
            return;
        }

        _draftDebounce?.Cancel();
        var cts = _draftDebounce = new CancellationTokenSource();
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(DraftDelay, cts.Token).ConfigureAwait(false);
                await FlushDraftAsync().ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception)
            {
                // 草稿保存失败不影响写信
            }
        });
    }

    /// <summary>立即把当前内容保存为草稿（窗口关闭前、测试里调用）；没有内容 / 已发送 / 已丢弃时不保存。</summary>
    public async Task FlushDraftAsync()
    {
        if (_drafts is null || _finished || SelectedAccount is null)
        {
            return;
        }

        if (!HasContent())
        {
            // 重新打开的草稿被清空了：不再保留旧内容
            if (_persisted)
            {
                _persisted = false;
                await _drafts.DeleteAsync(_draftId).ConfigureAwait(false);
            }

            return;
        }

        await _drafts.SaveAsync(new DraftContent(
            _draftId, SelectedAccount.Id, To ?? string.Empty, Cc ?? string.Empty, Bcc ?? string.Empty,
            Subject ?? string.Empty, Body ?? string.Empty,
            Mode: _request.Mode.ToString(),
            InReplyTo: _request.InReplyTo,
            References: _request.References,
            QuotedHtml: _request.QuotedHtml,
            QuotedText: _request.QuotedText,
            Attachments: Attachments.Select(a => a.Path).ToList(),
            SourceMessageId: _request.SourceMessageId)).ConfigureAwait(false);
        _persisted = true;
    }

    /// <summary>草稿在窗口外被删除（列表里删除）：停止自动保存且不再保存，窗口随后关闭。</summary>
    public void Abandon()
    {
        _finished = true;
        _draftDebounce?.Cancel();
    }

    /// <summary>丢弃：取消自动保存并删除已保存的草稿。</summary>
    public async Task DiscardAsync()
    {
        _finished = true;
        _draftDebounce?.Cancel();
        if (_drafts is not null)
        {
            try
            {
                await _drafts.DeleteAsync(_draftId).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // ignore
            }
        }
    }

    // ───────── 发送 ─────────

    private bool CanSend() => !IsSending && (ParseAddresses(To).Count > 0 || ParseAddresses(Cc).Count > 0 || ParseAddresses(Bcc).Count > 0);

    /// <summary>组装要入队的邮件：正文 = 用户写的部分 + 引用原文（HTML 与纯文本两份）。</summary>
    public OutgoingMailMessage BuildMessage()
    {
        var html = ComposeRequest.TextToHtml(Body ?? string.Empty);
        var text = Body ?? string.Empty;
        if (_request.HasQuote)
        {
            html += _request.QuotedHtml ?? string.Empty;
            text += _request.QuotedText ?? string.Empty;
        }

        return new OutgoingMailMessage(
            SelectedAccount?.Id.ToString("D"),
            ParseAddresses(To),
            ParseAddresses(Cc),
            ParseAddresses(Bcc),
            (Subject ?? string.Empty).Trim(),
            html,
            Attachments.Select(a => a.Path).ToList(),
            BodyText: text,
            InReplyTo: _request.InReplyTo,
            References: _request.References,
            DraftId: _draftId);
    }

    [RelayCommand(CanExecute = nameof(CanSend))]
    private async Task SendAsync()
    {
        ErrorMessage = string.Empty;
        var invalid = ParseAddresses(To).Concat(ParseAddresses(Cc)).Concat(ParseAddresses(Bcc)).FirstOrDefault(a => !IsValidAddress(a));
        if (invalid is not null)
        {
            ErrorMessage = $"收件人地址无效：{invalid}";
            return;
        }

        if (SelectedAccount is null)
        {
            ErrorMessage = "没有可用的发件账号，请先添加账号。";
            return;
        }

        IsSending = true;
        try
        {
            _draftDebounce?.Cancel();
            await _sendService.EnqueueAsync(BuildMessage());
            _finished = true;
            if (_drafts is not null)
            {
                try
                {
                    await _drafts.DeleteAsync(_draftId);
                }
                catch (Exception)
                {
                    // 草稿残留无害
                }
            }

            Sent?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            ErrorMessage = $"发送失败：{ex.Message}";
        }
        finally
        {
            IsSending = false;
        }
    }
}
