using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MailClient.Core;
using MailClient.Providers.Setup;

namespace MailClient.ViewModels.AddAccount;

/// <summary>
/// 添加账号向导：选择类型 → 填写凭据 → 验证连接 → 完成（显示名 / 颜色 / 保存）。
/// 所有网络与存储副作用都在 <see cref="IAccountSetupService"/> 后面，这里只有状态机与校验，可在无网络下单测。
/// </summary>
public sealed partial class AddAccountViewModel : ObservableObject
{
    public static readonly IReadOnlyList<string> ColorChoices =
        ["#0F6CBD", "#107C10", "#C239B3", "#CA5010", "#D13438", "#8764B8", "#00B7C3", "#7A7574"];

    private static readonly IReadOnlyList<ProviderTileViewModel> AllTiles =
    [
        new(SetupProviderKind.Gmail, "Gmail", "使用 Google 账号登录", "G", "#EA4335"),
        new(SetupProviderKind.Outlook, "Outlook / Hotmail", "Microsoft 365 账号，使用 Microsoft 登录", "O", "#0F6CBD"),
        new(SetupProviderKind.Qq, "QQ 邮箱", "邮箱地址 + 授权码", "Q", "#12B7F5"),
        new(SetupProviderKind.NetEase, "163 / 126 网易邮箱", "邮箱地址 + 授权码", "易", "#D43C33"),
        new(SetupProviderKind.OtherImap, "其他 IMAP/SMTP", "手动或自动配置服务器", "@", "#5E5E5E")
    ];

    private readonly IAccountSetupService _service;
    private readonly AppSettingsStore? _settings;
    private readonly Func<Guid> _newId;
    private readonly SynchronizationContext? _ui = SynchronizationContext.Current;

    private CancellationTokenSource? _operation;
    private AccountDraft? _draft;
    private bool _applyingServer;
    private bool _serverEdited;
    private bool _serverNeedsConfirm;
    private string? _detectedFor;
    private bool _verified;
    private bool _saved;

    public AddAccountViewModel(IAccountSetupService service, AppSettingsStore? settings = null, Func<Guid>? idFactory = null)
    {
        _service = service ?? throw new ArgumentNullException(nameof(service));
        _settings = settings;
        _newId = idFactory ?? Guid.NewGuid;
    }

    // ───────── 状态 ─────────

    public IReadOnlyList<ProviderTileViewModel> Tiles => AllTiles;

    public ObservableCollection<VerifyItemViewModel> VerifyItems { get; } = new();

    public IReadOnlyList<string> Colors => ColorChoices;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsStepPick), nameof(IsStepCredentials), nameof(IsStepVerify), nameof(IsStepFinish),
        nameof(StepNumber), nameof(StepTitle), nameof(PrimaryButtonText), nameof(CanGoNext), nameof(CanGoBack))]
    private AddAccountStep _step = AddAccountStep.PickProvider;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Kind), nameof(IsGmail), nameof(IsOutlook), nameof(IsOAuth), nameof(IsAuthCodeKind),
        nameof(IsOtherImap), nameof(ShowAdvancedServer), nameof(Help), nameof(HasHelp), nameof(Guide), nameof(EmailLabel), nameof(PasswordLabel),
        nameof(ProviderTitle), nameof(CanGoNext))]
    private ProviderTileViewModel? _selectedProvider;

    [ObservableProperty]
    private string _email = string.Empty;

    [ObservableProperty]
    private string _password = string.Empty;

    [ObservableProperty]
    private string _displayName = string.Empty;

    [ObservableProperty]
    private string? _accountColor;

    // 高级服务器设置（其他 IMAP）
    [ObservableProperty]
    private string _userName = string.Empty;

    [ObservableProperty]
    private string _imapHost = string.Empty;

    [ObservableProperty]
    private string _imapPortText = "993";

    [ObservableProperty]
    private int _imapSecurityIndex;

    [ObservableProperty]
    private string _smtpHost = string.Empty;

    [ObservableProperty]
    private string _smtpPortText = "465";

    [ObservableProperty]
    private int _smtpSecurityIndex;

    [ObservableProperty]
    private bool _showAdvanced;

    [ObservableProperty]
    private string? _serverHint;

    // OAuth
    [ObservableProperty]
    private string _clientId = string.Empty;

    [ObservableProperty]
    private string _clientSecret = string.Empty;

    [ObservableProperty]
    private string _tenantId = string.Empty;

    /// <summary>尚未配置 ClientId：界面显示醒目的说明，并展开 ClientId 输入框。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(GuideExpanded))]
    private bool _needsClientId;

    [ObservableProperty]
    private string? _signedInEmail;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasValidationError))]
    private string? _validationError;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    private string? _errorMessage;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasInfo))]
    private string? _infoMessage;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanGoNext), nameof(CanGoBack), nameof(CanCancelOperation), nameof(IsNotBusy), nameof(CanRetryVerify))]
    private bool _isBusy;

    [ObservableProperty]
    private bool _isSigningIn;

    [ObservableProperty]
    private bool _isVerifying;

    [ObservableProperty]
    private bool _isDetecting;

    [ObservableProperty]
    private string? _busyText;

    public AccountEntity? Result { get; private set; }

    /// <summary>账号已保存。</summary>
    public event EventHandler<AccountEntity>? Completed;

    /// <summary>向导需要关闭（完成或取消）。</summary>
    public event EventHandler? CloseRequested;

    // ───────── 派生 ─────────

    public bool IsStepPick => Step == AddAccountStep.PickProvider;
    public bool IsStepCredentials => Step == AddAccountStep.Credentials;
    public bool IsStepVerify => Step == AddAccountStep.Verify;
    public bool IsStepFinish => Step == AddAccountStep.Finish;
    public int StepNumber => (int)Step + 1;

    public string StepTitle => Step switch
    {
        AddAccountStep.PickProvider => "选择邮箱类型",
        AddAccountStep.Credentials => $"登录 {ProviderTitle}",
        AddAccountStep.Verify => "验证连接",
        _ => "完成设置"
    };

    public SetupProviderKind? Kind => SelectedProvider?.Kind;
    public bool IsGmail => Kind == SetupProviderKind.Gmail;
    public bool IsOutlook => Kind == SetupProviderKind.Outlook;
    public bool IsOAuth => IsGmail || IsOutlook;
    public bool IsAuthCodeKind => Kind is SetupProviderKind.Qq or SetupProviderKind.NetEase;
    public bool IsOtherImap => Kind == SetupProviderKind.OtherImap;
    public bool ShowAdvancedServer => IsOtherImap;
    public string ProviderTitle => SelectedProvider?.Title ?? string.Empty;
    public AuthCodeHelp? Help => Kind is { } k ? AuthCodeHelp.For(k) : null;
    public bool HasHelp => Help is not null;

    /// <summary>当前所选邮箱的分步设置引导（每种邮箱都有，见 <see cref="ProviderGuide"/>）。</summary>
    public ProviderGuide Guide => ProviderGuide.For(Kind ?? SetupProviderKind.OtherImap);

    /// <summary>引导默认展开：需要用户先做设置时（没有 ClientId、授权码类、其他 IMAP）。</summary>
    public bool GuideExpanded => NeedsClientId || IsAuthCodeKind || IsOtherImap;
    public string EmailLabel => IsGmail ? "邮箱地址（可选，仅作登录提示）" : "邮箱地址";
    public string PasswordLabel => IsAuthCodeKind ? "授权码" : "密码";
    public bool HasValidationError => !string.IsNullOrEmpty(ValidationError);
    public bool HasError => !string.IsNullOrEmpty(ErrorMessage);
    public bool HasInfo => !string.IsNullOrEmpty(InfoMessage);
    public bool HasSignedIn => !string.IsNullOrEmpty(SignedInEmail);
    public bool CanCancelOperation => IsBusy && (IsSigningIn || IsVerifying);
    public bool IsNotBusy => !IsBusy;
    public bool CanRetryVerify => Step == AddAccountStep.Verify && !IsBusy && !_verified;

    /// <summary>OAuth 类型由页面里的“登录”按钮驱动，只有登录成功后“下一步”才可用。</summary>
    public bool CanGoNext => !IsBusy && Step switch
    {
        AddAccountStep.PickProvider => SelectedProvider is not null,
        AddAccountStep.Credentials => !IsOAuth || HasSignedIn,
        AddAccountStep.Verify => _verified,
        _ => !string.IsNullOrWhiteSpace(DisplayName)
    };

    public bool CanGoBack => Step != AddAccountStep.PickProvider && !(IsBusy && !CanCancelOperation);

    public string PrimaryButtonText => Step == AddAccountStep.Finish ? "完成" : "下一步";

    // ───────── 命令 ─────────

    [RelayCommand]
    private void SelectProvider(ProviderTileViewModel? tile)
    {
        if (tile is null || IsBusy)
        {
            return;
        }

        SelectedProvider = tile;
        ValidationError = null;
    }

    [RelayCommand]
    public async Task NextAsync()
    {
        if (!CanGoNext)
        {
            return;
        }

        ValidationError = null;
        ErrorMessage = null;
        switch (Step)
        {
            case AddAccountStep.PickProvider:
                EnterCredentials();
                break;
            case AddAccountStep.Credentials:
                await SubmitCredentialsAsync();
                break;
            case AddAccountStep.Verify:
                EnterFinish();
                break;
            case AddAccountStep.Finish:
                await SaveAsync();
                break;
        }
    }

    [RelayCommand]
    public void Back()
    {
        if (!CanGoBack)
        {
            return;
        }

        CancelOperation();
        ValidationError = null;
        ErrorMessage = null;
        InfoMessage = null;
        switch (Step)
        {
            case AddAccountStep.Credentials:
                DiscardDraft();
                Step = AddAccountStep.PickProvider;
                break;
            case AddAccountStep.Verify:
                Step = AddAccountStep.Credentials;
                break;
            case AddAccountStep.Finish:
                Step = AddAccountStep.Verify;
                break;
        }

        NotifyNav();
    }

    [RelayCommand]
    public void Cancel()
    {
        CancelOperation();
        if (!_saved)
        {
            DiscardDraft();
        }

        CloseRequested?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>取消正在进行的登录 / 验证（浏览器里的等待、网络连接）。</summary>
    [RelayCommand]
    public void CancelOperation() => _operation?.Cancel();

    [RelayCommand]
    public async Task SignInAsync()
    {
        if (IsBusy || !IsOAuth)
        {
            return;
        }

        ValidationError = null;
        ErrorMessage = null;
        InfoMessage = null;
        if (!ValidateOAuthInputs())
        {
            return;
        }

        var draft = BuildDraft();
        _draft = draft;
        using var cts = BeginOperation();
        IsSigningIn = true;
        BusyText = IsGmail ? "请在浏览器中完成 Google 登录…" : "请在弹出的 Microsoft 窗口中完成登录…";
        SetBusy(true);
        SetupResult result;
        try
        {
            result = await _service.SignInAsync(draft, cts.Token);
        }
        catch (OperationCanceledException)
        {
            result = SetupResult.Fail(new SetupError(SetupErrorKind.Cancelled, "已取消。"), SetupStage.Login);
        }
        catch (Exception ex)
        {
            result = SetupResult.Fail(SetupErrorClassifier.Classify(ex, draft.Kind, SetupStage.Login), SetupStage.Login);
        }
        finally
        {
            EndOperation(cts);
            IsSigningIn = false;
            SetBusy(false);
        }

        if (result.Success)
        {
            SignedInEmail = string.IsNullOrWhiteSpace(result.EmailAddress) ? draft.EmailAddress : result.EmailAddress;
            OnPropertyChanged(nameof(HasSignedIn));
            NeedsClientId = false;
            _draft = draft with { EmailAddress = SignedInEmail ?? string.Empty };
            await StartVerifyAsync();
            return;
        }

        if (result.Error?.Kind == SetupErrorKind.Cancelled)
        {
            InfoMessage = "已取消登录。";
        }
        else
        {
            ErrorMessage = result.Error?.Message ?? "登录失败。";
            if (result.Error?.Kind == SetupErrorKind.MissingClientId)
            {
                NeedsClientId = true;
            }
        }

        NotifyNav();
    }

    [RelayCommand]
    public async Task RetryVerifyAsync()
    {
        if (IsBusy || Step != AddAccountStep.Verify || _draft is null)
        {
            return;
        }

        await StartVerifyAsync();
    }

    /// <summary>其他 IMAP：按邮箱域名识别预设 → ISPDB 自动配置 → 常见规则猜测。</summary>
    [RelayCommand]
    public async Task DetectServerAsync()
    {
        ValidationError = null;
        var email = Email.Trim();
        if (!ProviderDetector.IsValidEmail(email))
        {
            ValidationError = email.Length == 0 ? "请先输入邮箱地址。" : "邮箱地址格式不正确。";
            return;
        }

        _serverEdited = false;
        _serverNeedsConfirm = false;
        IsDetecting = true;
        BusyText = "正在识别服务器设置…";
        SetBusy(true);
        try
        {
            var domain = ProviderDetector.ExtractDomain(email)!;
            var preset = ProviderDetector.Detect(email);
            if (preset is not null)
            {
                ApplyServer(preset.Imap, preset.Smtp, null);
                ServerHint = preset.Kind switch
                {
                    SetupProviderKind.Gmail => "识别为 Gmail。建议返回上一步选择「Gmail」以使用 Google 登录；若继续使用 IMAP，需要「应用专用密码」。",
                    SetupProviderKind.Outlook => "识别为 Outlook / Hotmail。建议返回上一步选择 Outlook 以使用 Microsoft 登录；微软已停用 IMAP 基本密码登录。",
                    _ => $"已识别为「{preset.DisplayName}」，服务器设置已自动填写。" +
                         (preset.UsesAuthCode ? "该邮箱通常需要使用「授权码」代替登录密码。" : string.Empty)
                };
                _detectedFor = email;
                return;
            }

            AutoconfigResult? auto = null;
            try
            {
                auto = await _service.LookupAutoconfigAsync(email, CancellationToken.None);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // best-effort
            }

            if (auto is not null)
            {
                ApplyServer(auto.Imap, auto.Smtp, auto.UserName);
                ServerHint = "已通过 Mozilla ISPDB 自动获取服务器设置，请核对后继续。";
                _detectedFor = email;
                return;
            }

            ApplyServer(
                new ImapServerSettings($"imap.{domain}", 993, MailSecureSocketOptions.SslOnConnect),
                new SmtpServerSettings($"smtp.{domain}", 465, MailSecureSocketOptions.SslOnConnect),
                null);
            ServerHint = "未能自动识别该邮箱的服务器，已按常见规则预填。请在下面核对 / 修改后再点「下一步」。";
            ShowAdvanced = true;
            _serverNeedsConfirm = true;
            _detectedFor = email;
        }
        finally
        {
            IsDetecting = false;
            SetBusy(false);
        }
    }

    // ───────── 步骤逻辑 ─────────

    private void EnterCredentials()
    {
        DiscardDraft();
        var kind = SelectedProvider!.Kind;
        _verified = false;
        _serverEdited = false;
        _serverNeedsConfirm = false;
        _detectedFor = null;
        SignedInEmail = null;
        OnPropertyChanged(nameof(HasSignedIn));
        Password = string.Empty;
        ServerHint = null;
        ShowAdvanced = false;
        if (kind is SetupProviderKind.Qq or SetupProviderKind.NetEase)
        {
            var preset = ProviderDetector.PresetFor(kind, Email);
            ApplyServer(preset!.Imap, preset.Smtp, null);
        }
        else if (kind == SetupProviderKind.OtherImap)
        {
            ApplyServer(new ImapServerSettings(string.Empty, 993, MailSecureSocketOptions.SslOnConnect),
                new SmtpServerSettings(string.Empty, 465, MailSecureSocketOptions.SslOnConnect), null);
        }

        if (kind == SetupProviderKind.Gmail)
        {
            ClientId = _settings?.GmailClientId ?? string.Empty;
            ClientSecret = _settings?.GmailClientSecret ?? string.Empty;
            NeedsClientId = string.IsNullOrWhiteSpace(ClientId);
        }
        else if (kind == SetupProviderKind.Outlook)
        {
            ClientId = _settings?.MicrosoftClientId ?? string.Empty;
            TenantId = _settings?.MicrosoftTenantId ?? "common";
            NeedsClientId = string.IsNullOrWhiteSpace(ClientId);
        }
        else
        {
            NeedsClientId = false;
        }

        Step = AddAccountStep.Credentials;
        NotifyNav();
    }

    private async Task SubmitCredentialsAsync()
    {
        if (IsOAuth)
        {
            // 已登录（用户返回过）→ 直接重新验证
            _draft ??= BuildDraft();
            await StartVerifyAsync();
            return;
        }

        if (!ValidateImapCredentials())
        {
            return;
        }

        if (IsOtherImap)
        {
            var email = Email.Trim();
            var needDetect = string.IsNullOrWhiteSpace(ImapHost) ||
                             (!_serverEdited && !string.Equals(_detectedFor, email, StringComparison.OrdinalIgnoreCase));
            if (needDetect)
            {
                await DetectServerAsync();
                if (HasValidationError)
                {
                    return;
                }

                if (_serverNeedsConfirm)
                {
                    return;
                }
            }
            else
            {
                _serverNeedsConfirm = false;
            }

            if (!ValidateServer())
            {
                ShowAdvanced = true;
                return;
            }
        }

        _draft = BuildDraft();
        await StartVerifyAsync();
    }

    private async Task StartVerifyAsync()
    {
        var draft = _draft ?? BuildDraft();
        _draft = draft;
        _verified = false;
        ErrorMessage = null;
        InfoMessage = null;
        VerifyItems.Clear();
        if (draft.Kind == SetupProviderKind.Outlook)
        {
            VerifyItems.Add(new VerifyItemViewModel(SetupStage.Graph, "Microsoft Graph（读取账号信息）"));
        }
        else
        {
            VerifyItems.Add(new VerifyItemViewModel(SetupStage.Imap, "收信服务器（IMAP）登录并打开收件箱"));
            VerifyItems.Add(new VerifyItemViewModel(SetupStage.Smtp, "发信服务器（SMTP）登录"));
        }

        Step = AddAccountStep.Verify;
        var imapText = draft.Imap is { } i ? $"{i.Host}:{i.Port}" : null;
        BusyText = imapText is null ? "正在验证…" : $"正在连接 {imapText}…";
        IsVerifying = true;
        SetBusy(true);
        using var cts = BeginOperation();
        var progress = new InlineProgress<SetupProgress>(p => Post(() => ApplyProgress(p)));
        SetupResult result;
        try
        {
            result = await _service.VerifyAsync(draft, progress, cts.Token);
        }
        catch (OperationCanceledException)
        {
            result = SetupResult.Fail(new SetupError(SetupErrorKind.Cancelled, "已取消验证。"));
        }
        catch (Exception ex)
        {
            result = SetupResult.Fail(SetupErrorClassifier.Classify(ex, draft.Kind, SetupStage.Imap), SetupStage.Imap);
        }
        finally
        {
            EndOperation(cts);
            IsVerifying = false;
            SetBusy(false);
        }

        if (result.Success)
        {
            _verified = true;
            if (!string.IsNullOrWhiteSpace(result.EmailAddress) && draft.Kind == SetupProviderKind.Outlook)
            {
                _draft = draft with { EmailAddress = result.EmailAddress! };
            }

            foreach (var item in VerifyItems.Where(v => v.Status != SetupStageStatus.Succeeded))
            {
                item.Status = SetupStageStatus.Succeeded;
            }
        }
        else if (result.Error?.Kind == SetupErrorKind.Cancelled)
        {
            InfoMessage = "已取消验证。可点「重试」再次验证，或返回修改。";
        }
        else
        {
            ErrorMessage = result.Error?.Message ?? "验证失败。";
            var failed = VerifyItems.FirstOrDefault(v => v.Stage == result.FailedStage) ??
                         VerifyItems.FirstOrDefault(v => v.Status == SetupStageStatus.Running);
            if (failed is not null && failed.Status != SetupStageStatus.Failed)
            {
                failed.Status = SetupStageStatus.Failed;
                failed.Message = result.Error?.Message;
            }
        }

        NotifyNav();
    }

    private void EnterFinish()
    {
        if (string.IsNullOrWhiteSpace(DisplayName))
        {
            var email = _draft?.EmailAddress ?? Email;
            DisplayName = ProviderDetector.ExtractLocalPart(email) ?? string.Empty;
        }

        Step = AddAccountStep.Finish;
        NotifyNav();
    }

    private async Task SaveAsync()
    {
        if (_draft is null)
        {
            return;
        }

        var draft = _draft with { DisplayName = DisplayName.Trim() };
        BusyText = "正在保存…";
        SetBusy(true);
        try
        {
            var account = await _service.SaveAsync(draft, CancellationToken.None);
            _saved = true;
            Result = account;
            if (_settings is not null)
            {
                _settings.SetAccountColor(account.Id, AccountColor);
            }
        }
        catch (Exception ex)
        {
            ErrorMessage = $"保存失败：{ex.Message}";
            return;
        }
        finally
        {
            SetBusy(false);
        }

        Completed?.Invoke(this, Result!);
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }

    // ───────── 校验 ─────────

    /// <summary>步骤 2（QQ / 网易 / 其他 IMAP）的输入校验；错误写入 <see cref="ValidationError"/>。</summary>
    public bool ValidateImapCredentials()
    {
        var email = Email.Trim();
        if (email.Length == 0)
        {
            ValidationError = "请输入邮箱地址。";
            return false;
        }

        if (!ProviderDetector.IsValidEmail(email))
        {
            ValidationError = "邮箱地址格式不正确，应类似 name@example.com。";
            return false;
        }

        var detected = ProviderDetector.Detect(email);
        if (Kind == SetupProviderKind.Qq && detected?.Kind != SetupProviderKind.Qq)
        {
            ValidationError = "这不是 QQ 邮箱地址（应以 @qq.com 或 @foxmail.com 结尾）。其他邮箱请返回上一步选择对应类型。";
            return false;
        }

        if (Kind == SetupProviderKind.NetEase && detected?.Kind != SetupProviderKind.NetEase)
        {
            ValidationError = "这不是网易邮箱地址（应以 @163.com、@126.com、@yeah.net 等结尾）。其他邮箱请返回上一步选择对应类型。";
            return false;
        }

        if (string.IsNullOrEmpty(Password))
        {
            ValidationError = IsAuthCodeKind ? "请输入授权码（不是网页登录密码）。" : "请输入密码。";
            return false;
        }

        return true;
    }

    /// <summary>服务器设置校验：主机非空，端口 1–65535。</summary>
    public bool ValidateServer()
    {
        if (string.IsNullOrWhiteSpace(ImapHost) || string.IsNullOrWhiteSpace(SmtpHost))
        {
            ValidationError = "请填写 IMAP 与 SMTP 服务器地址。";
            return false;
        }

        if (!TryPort(ImapPortText, out _) || !TryPort(SmtpPortText, out _))
        {
            ValidationError = "端口应为 1–65535 之间的整数。";
            return false;
        }

        return true;
    }

    private bool ValidateOAuthInputs()
    {
        var email = Email.Trim();
        if (IsGmail && email.Length > 0 && !ProviderDetector.IsValidEmail(email))
        {
            ValidationError = "邮箱地址格式不正确（该项可留空）。";
            return false;
        }

        if (string.IsNullOrWhiteSpace(ClientId) && NeedsClientId)
        {
            ValidationError = IsGmail
                ? "还没有 Google ClientId：请展开上方「怎么设置」按步骤创建并粘贴 ClientId（和客户端密钥）。"
                : "还没有 Microsoft 应用 ID：请展开上方「怎么设置」按步骤注册应用并粘贴 ClientId。";
            return false;
        }

        return true;
    }

    private static bool TryPort(string? text, out int port)
        => int.TryParse(text?.Trim(), out port) && port is >= 1 and <= 65535;

    // ───────── 草稿 ─────────

    /// <summary>由当前表单构建账号草稿（public 便于测试）。</summary>
    public AccountDraft BuildDraft()
    {
        var kind = SelectedProvider?.Kind ?? throw new InvalidOperationException("尚未选择邮箱类型。");
        var id = _draft?.Id is { } existing && existing != Guid.Empty && _draft.Kind == kind ? existing : _newId();
        var email = Email.Trim();
        switch (kind)
        {
            case SetupProviderKind.Gmail:
                return new AccountDraft(id, kind, email, string.Empty,
                    ClientId: ClientId.Trim(), ClientSecret: ClientSecret.Trim());
            case SetupProviderKind.Outlook:
                return new AccountDraft(id, kind, string.Empty, string.Empty,
                    ClientId: ClientId.Trim(), TenantId: TenantId.Trim());
            case SetupProviderKind.Qq:
            case SetupProviderKind.NetEase:
            {
                var preset = ProviderDetector.PresetFor(kind, email)!;
                return new AccountDraft(id, kind, email, string.Empty, UserName: email, Password: Password,
                    Imap: preset.Imap, Smtp: preset.Smtp);
            }
            default:
                return new AccountDraft(
                    id, kind, email, string.Empty,
                    UserName: string.IsNullOrWhiteSpace(UserName) ? email : UserName.Trim(),
                    Password: Password,
                    Imap: new ImapServerSettings(ImapHost.Trim(), int.Parse(ImapPortText.Trim()), ToSocket(ImapSecurityIndex)),
                    Smtp: new SmtpServerSettings(SmtpHost.Trim(), int.Parse(SmtpPortText.Trim()), ToSocket(SmtpSecurityIndex)));
        }
    }

    public static MailSecureSocketOptions ToSocket(int index) => index switch
    {
        1 => MailSecureSocketOptions.StartTls,
        2 => MailSecureSocketOptions.None,
        _ => MailSecureSocketOptions.SslOnConnect
    };

    public static int ToSecurityIndex(MailSecureSocketOptions options) => options switch
    {
        MailSecureSocketOptions.StartTls or MailSecureSocketOptions.StartTlsWhenAvailable => 1,
        MailSecureSocketOptions.None => 2,
        _ => 0
    };

    private void ApplyServer(ImapServerSettings imap, SmtpServerSettings smtp, string? userName)
    {
        _applyingServer = true;
        try
        {
            ImapHost = imap.Host;
            ImapPortText = imap.Port.ToString();
            ImapSecurityIndex = ToSecurityIndex(imap.SocketOptions);
            SmtpHost = smtp.Host;
            SmtpPortText = smtp.Port.ToString();
            SmtpSecurityIndex = ToSecurityIndex(smtp.SocketOptions);
            UserName = userName is not null && !string.Equals(userName, Email.Trim(), StringComparison.OrdinalIgnoreCase)
                ? userName
                : string.Empty;
        }
        finally
        {
            _applyingServer = false;
        }
    }

    private void DiscardDraft()
    {
        var draft = _draft;
        _draft = null;
        _verified = false;
        if (draft is not null && draft.IsOAuth)
        {
            _ = SafeDiscardAsync(draft);
        }
    }

    private async Task SafeDiscardAsync(AccountDraft draft)
    {
        try
        {
            await _service.DiscardAsync(draft);
        }
        catch
        {
            // ignore
        }
    }

    // ───────── 辅助 ─────────

    private CancellationTokenSource BeginOperation()
    {
        var cts = new CancellationTokenSource();
        _operation = cts;
        return cts;
    }

    private void EndOperation(CancellationTokenSource cts)
    {
        if (ReferenceEquals(_operation, cts))
        {
            _operation = null;
        }
    }

    private void SetBusy(bool busy)
    {
        IsBusy = busy;
        if (!busy)
        {
            BusyText = null;
        }

        NotifyNav();
    }

    private void NotifyNav()
    {
        OnPropertyChanged(nameof(CanGoNext));
        OnPropertyChanged(nameof(CanGoBack));
        OnPropertyChanged(nameof(CanCancelOperation));
        OnPropertyChanged(nameof(CanRetryVerify));
    }

    private void ApplyProgress(SetupProgress p)
    {
        var item = VerifyItems.FirstOrDefault(v => v.Stage == p.Stage);
        if (item is null)
        {
            return;
        }

        item.Status = p.Status;
        item.Message = p.Message;
        BusyText = p.Status == SetupStageStatus.Running ? $"正在验证：{item.Title}…" : BusyText;
    }

    private void Post(Action action)
    {
        if (_ui is null || ReferenceEquals(SynchronizationContext.Current, _ui))
        {
            action();
        }
        else
        {
            _ui.Post(_ => action(), null);
        }
    }

    // ───────── 属性变化钩子 ─────────

    partial void OnEmailChanged(string value)
    {
        ValidationError = null;
        if (IsOtherImap && !string.Equals(_detectedFor, value.Trim(), StringComparison.OrdinalIgnoreCase) && !_serverEdited)
        {
            _serverNeedsConfirm = false;
        }
    }

    partial void OnPasswordChanged(string value) => ValidationError = null;

    partial void OnDisplayNameChanged(string value) => NotifyNav();

    partial void OnImapHostChanged(string value) => MarkServerEdited();

    partial void OnImapPortTextChanged(string value) => MarkServerEdited();

    partial void OnImapSecurityIndexChanged(int value) => MarkServerEdited();

    partial void OnSmtpHostChanged(string value) => MarkServerEdited();

    partial void OnSmtpPortTextChanged(string value) => MarkServerEdited();

    partial void OnSmtpSecurityIndexChanged(int value) => MarkServerEdited();

    partial void OnUserNameChanged(string value) => MarkServerEdited();

    private void MarkServerEdited()
    {
        if (!_applyingServer)
        {
            _serverEdited = true;
            _serverNeedsConfirm = false;
        }
    }
}
