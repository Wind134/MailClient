using MailClient.Auth;
using MailClient.Core;
using MailClient.Providers.Setup;
using MailClient.Storage.Demo;
using MailClient.ViewModels;
using MailClient.ViewModels.AddAccount;

namespace MailClient.Tests;

/// <summary>向导测试用的假服务：不碰网络、凭据库和数据库。</summary>
internal sealed class FakeSetupService : IAccountSetupService
{
    public SetupResult SignInResult { get; set; } = SetupResult.Ok("me@gmail.com");
    public SetupResult VerifyResult { get; set; } = SetupResult.Ok();
    public AutoconfigResult? Autoconfig { get; set; }
    public TaskCompletionSource<bool>? SignInGate { get; set; }
    public List<AccountDraft> Verified { get; } = new();
    public List<AccountDraft> Saved { get; } = new();
    public List<AccountDraft> Discarded { get; } = new();
    public List<Guid> Removed { get; } = new();
    public int AutoconfigCalls { get; private set; }
    public IReadOnlyList<SetupProgress> ProgressToReport { get; set; } = Array.Empty<SetupProgress>();

    public async Task<SetupResult> SignInAsync(AccountDraft draft, CancellationToken cancellationToken)
    {
        if (SignInGate is not null)
        {
            using var reg = cancellationToken.Register(() => SignInGate.TrySetCanceled(cancellationToken));
            await SignInGate.Task;
        }

        return SignInResult;
    }

    public Task<SetupResult> VerifyAsync(AccountDraft draft, IProgress<SetupProgress>? progress, CancellationToken cancellationToken)
    {
        Verified.Add(draft);
        foreach (var p in ProgressToReport)
        {
            progress?.Report(p);
        }

        return Task.FromResult(VerifyResult);
    }

    public Task<AccountEntity> SaveAsync(AccountDraft draft, CancellationToken cancellationToken)
    {
        Saved.Add(draft);
        var now = DateTimeOffset.UtcNow;
        return Task.FromResult(new AccountEntity(draft.Id, draft.DisplayName, draft.EmailAddress, "imap.generic",
            default, now, now, null, true));
    }

    public Task DiscardAsync(AccountDraft draft)
    {
        Discarded.Add(draft);
        return Task.CompletedTask;
    }

    public Task RemoveAccountAsync(Guid accountId, CancellationToken cancellationToken)
    {
        Removed.Add(accountId);
        return Task.CompletedTask;
    }

    public Task<AutoconfigResult?> LookupAutoconfigAsync(string email, CancellationToken cancellationToken)
    {
        AutoconfigCalls++;
        return Task.FromResult(Autoconfig);
    }
}

public class AddAccountViewModelTests
{
    private static ProviderTileViewModel Tile(AddAccountViewModel vm, SetupProviderKind kind)
        => vm.Tiles.Single(t => t.Kind == kind);

    private static AddAccountViewModel Create(FakeSetupService service, AppSettingsStore? settings = null)
        => new(service, settings, () => Guid.Parse("11111111-1111-1111-1111-111111111111"));

    [Fact]
    public void Every_provider_exposes_its_own_guide_and_it_opens_expanded_when_setup_is_needed()
    {
        var vm = Create(new FakeSetupService());
        foreach (var kind in Enum.GetValues<SetupProviderKind>())
        {
            vm.SelectedProvider = Tile(vm, kind);
            Assert.Same(MailClient.ViewModels.AddAccount.ProviderGuide.For(kind), vm.Guide);
        }

        vm.SelectedProvider = Tile(vm, SetupProviderKind.Qq);
        Assert.True(vm.GuideExpanded);
        vm.SelectedProvider = Tile(vm, SetupProviderKind.OtherImap);
        Assert.True(vm.GuideExpanded);
    }

    [Fact]
    public void Starts_on_provider_picker_with_five_tiles_and_next_disabled()
    {
        var vm = Create(new FakeSetupService());
        Assert.Equal(AddAccountStep.PickProvider, vm.Step);
        Assert.Equal(5, vm.Tiles.Count);
        Assert.False(vm.CanGoNext);
        Assert.False(vm.CanGoBack);
        Assert.Equal(1, vm.StepNumber);
    }

    [Fact]
    public async Task Selecting_a_provider_enables_next_and_enters_credentials()
    {
        var vm = Create(new FakeSetupService());
        vm.SelectedProvider = Tile(vm, SetupProviderKind.Qq);
        Assert.True(vm.CanGoNext);
        await vm.NextAsync();
        Assert.Equal(AddAccountStep.Credentials, vm.Step);
        Assert.True(vm.IsAuthCodeKind);
        Assert.True(vm.HasHelp);
        Assert.Equal("授权码", vm.PasswordLabel);
        Assert.Equal("imap.qq.com", vm.ImapHost);
    }

    [Fact]
    public async Task Qq_requires_qq_address_and_auth_code()
    {
        var service = new FakeSetupService();
        var vm = Create(service);
        vm.SelectedProvider = Tile(vm, SetupProviderKind.Qq);
        await vm.NextAsync();

        await vm.NextAsync();
        Assert.Contains("邮箱地址", vm.ValidationError);

        vm.Email = "someone@163.com";
        vm.Password = "abcd";
        await vm.NextAsync();
        Assert.Contains("QQ 邮箱", vm.ValidationError);

        vm.Email = "10001@qq.com";
        vm.Password = string.Empty;
        await vm.NextAsync();
        Assert.Contains("授权码", vm.ValidationError);
        Assert.Empty(service.Verified);
        Assert.Equal(AddAccountStep.Credentials, vm.Step);
    }

    [Fact]
    public async Task Qq_happy_path_verifies_then_finishes_and_saves()
    {
        var service = new FakeSetupService();
        var vm = Create(service);
        AccountEntity? completed = null;
        var closed = false;
        vm.Completed += (_, a) => completed = a;
        vm.CloseRequested += (_, _) => closed = true;

        vm.SelectedProvider = Tile(vm, SetupProviderKind.Qq);
        await vm.NextAsync();
        vm.Email = "10001@qq.com";
        vm.Password = "authcode";
        await vm.NextAsync();

        Assert.Equal(AddAccountStep.Verify, vm.Step);
        Assert.Equal(2, vm.VerifyItems.Count);
        Assert.All(vm.VerifyItems, v => Assert.True(v.IsSucceeded));
        Assert.True(vm.CanGoNext);
        var draft = Assert.Single(service.Verified);
        Assert.Equal("imap.qq.com", draft.Imap!.Host);
        Assert.Equal("authcode", draft.Password);

        await vm.NextAsync();
        Assert.Equal(AddAccountStep.Finish, vm.Step);
        Assert.Equal("10001", vm.DisplayName);
        Assert.Equal("完成", vm.PrimaryButtonText);

        vm.DisplayName = "  我的 QQ  ";
        await vm.NextAsync();
        var saved = Assert.Single(service.Saved);
        Assert.Equal("我的 QQ", saved.DisplayName);
        Assert.NotNull(completed);
        Assert.True(closed);
        Assert.Same(completed, vm.Result);
    }

    [Fact]
    public async Task Verify_failure_shows_error_marks_item_failed_and_blocks_next()
    {
        var service = new FakeSetupService
        {
            VerifyResult = SetupResult.Fail(
                new SetupError(SetupErrorKind.NeedAuthCode, "请使用授权码"), SetupStage.Imap)
        };
        var vm = Create(service);
        vm.SelectedProvider = Tile(vm, SetupProviderKind.NetEase);
        await vm.NextAsync();
        vm.Email = "abc@163.com";
        vm.Password = "pw";
        await vm.NextAsync();

        Assert.Equal(AddAccountStep.Verify, vm.Step);
        Assert.Equal("请使用授权码", vm.ErrorMessage);
        Assert.True(vm.VerifyItems.First(v => v.Stage == SetupStage.Imap).IsFailed);
        Assert.False(vm.CanGoNext);
        Assert.True(vm.CanRetryVerify);

        // 修正后重试
        service.VerifyResult = SetupResult.Ok();
        await vm.RetryVerifyAsync();
        Assert.True(vm.CanGoNext);
        Assert.Null(vm.ErrorMessage);
        Assert.Equal(2, service.Verified.Count);
    }

    [Fact]
    public async Task Back_navigates_one_step_and_returns_to_picker()
    {
        var vm = Create(new FakeSetupService());
        vm.SelectedProvider = Tile(vm, SetupProviderKind.NetEase);
        await vm.NextAsync();
        vm.Email = "abc@126.com";
        vm.Password = "pw";
        await vm.NextAsync();
        vm.Back();
        Assert.Equal(AddAccountStep.Credentials, vm.Step);
        vm.Back();
        Assert.Equal(AddAccountStep.PickProvider, vm.Step);
        Assert.False(vm.CanGoBack);
    }

    [Fact]
    public async Task Gmail_without_client_id_flags_missing_client_id_and_blocks_sign_in()
    {
        var service = new FakeSetupService();
        using var settingsDir = new TempDir();
        var settings = new AppSettingsStore(Path.Combine(settingsDir.Path, "settings.json"));
        var vm = Create(service, settings);
        vm.SelectedProvider = Tile(vm, SetupProviderKind.Gmail);
        await vm.NextAsync();

        if (!string.IsNullOrWhiteSpace(settings.GmailClientId))
        {
            return; // 环境变量里已配置：本测试不适用
        }

        Assert.True(vm.NeedsClientId);
        await vm.SignInAsync();
        Assert.Contains("怎么设置", vm.ValidationError);
        Assert.DoesNotContain(".md", vm.ValidationError);
        Assert.Equal(AddAccountStep.Credentials, vm.Step);
    }

    [Fact]
    public async Task Gmail_sign_in_success_advances_to_verify()
    {
        var service = new FakeSetupService { SignInResult = SetupResult.Ok("real@gmail.com") };
        var vm = Create(service);
        vm.SelectedProvider = Tile(vm, SetupProviderKind.Gmail);
        await vm.NextAsync();
        vm.ClientId = "abc.apps.googleusercontent.com";
        vm.NeedsClientId = false;

        Assert.False(vm.CanGoNext); // OAuth：必须先登录
        await vm.SignInAsync();

        Assert.Equal("real@gmail.com", vm.SignedInEmail);
        Assert.Equal(AddAccountStep.Verify, vm.Step);
        Assert.True(vm.CanGoNext);
        Assert.Equal("real@gmail.com", service.Verified.Single().EmailAddress);
    }

    [Fact]
    public async Task Sign_in_can_be_cancelled_while_waiting()
    {
        var service = new FakeSetupService { SignInGate = new TaskCompletionSource<bool>() };
        var vm = Create(service);
        vm.SelectedProvider = Tile(vm, SetupProviderKind.Gmail);
        await vm.NextAsync();
        vm.ClientId = "id";
        vm.NeedsClientId = false;

        var task = vm.SignInAsync();
        Assert.True(vm.IsSigningIn);
        Assert.True(vm.CanCancelOperation);
        vm.CancelOperation();
        await task;

        Assert.False(vm.IsSigningIn);
        Assert.False(vm.IsBusy);
        Assert.Equal(AddAccountStep.Credentials, vm.Step);
        Assert.True(vm.HasInfo);
        Assert.False(vm.HasError);
    }

    [Fact]
    public async Task Missing_client_id_error_from_service_reopens_client_id_hint()
    {
        var service = new FakeSetupService
        {
            SignInResult = SetupResult.Fail(new SetupError(SetupErrorKind.MissingClientId, "ClientId 不对"), SetupStage.Login)
        };
        var vm = Create(service);
        vm.SelectedProvider = Tile(vm, SetupProviderKind.Outlook);
        await vm.NextAsync();
        vm.ClientId = "id";
        vm.NeedsClientId = false;
        await vm.SignInAsync();
        Assert.True(vm.NeedsClientId);
        Assert.Equal("ClientId 不对", vm.ErrorMessage);
    }

    [Fact]
    public async Task Other_imap_known_domain_fills_preset_without_network_lookup()
    {
        var service = new FakeSetupService();
        var vm = Create(service);
        vm.SelectedProvider = Tile(vm, SetupProviderKind.OtherImap);
        await vm.NextAsync();
        vm.Email = "me@icloud.com";
        await vm.DetectServerAsync();
        Assert.Equal("imap.mail.me.com", vm.ImapHost);
        Assert.Equal("587", vm.SmtpPortText);
        Assert.Equal(1, vm.SmtpSecurityIndex);
        Assert.Equal(0, service.AutoconfigCalls);
        Assert.False(string.IsNullOrEmpty(vm.ServerHint));
    }

    [Fact]
    public async Task Other_imap_unknown_domain_uses_ispdb_result()
    {
        var service = new FakeSetupService
        {
            Autoconfig = new AutoconfigResult("Example",
                new ImapServerSettings("mail.example.org", 143, MailSecureSocketOptions.StartTls),
                new SmtpServerSettings("smtp.example.org", 587, MailSecureSocketOptions.StartTls),
                "me")
        };
        var vm = Create(service);
        vm.SelectedProvider = Tile(vm, SetupProviderKind.OtherImap);
        await vm.NextAsync();
        vm.Email = "me@example.org";
        vm.Password = "pw";
        await vm.NextAsync(); // 自动检测并继续进入验证

        Assert.Equal(1, service.AutoconfigCalls);
        var draft = Assert.Single(service.Verified);
        Assert.Equal("mail.example.org", draft.Imap!.Host);
        Assert.Equal(143, draft.Imap.Port);
        Assert.Equal(MailSecureSocketOptions.StartTls, draft.Imap.SocketOptions);
        Assert.Equal("me", draft.UserName);
    }

    [Fact]
    public async Task Other_imap_when_nothing_detected_guesses_and_asks_for_confirmation()
    {
        var service = new FakeSetupService();
        var vm = Create(service);
        vm.SelectedProvider = Tile(vm, SetupProviderKind.OtherImap);
        await vm.NextAsync();
        vm.Email = "me@unknown-corp.test";
        vm.Password = "pw";
        await vm.NextAsync();

        Assert.Equal(AddAccountStep.Credentials, vm.Step);
        Assert.True(vm.ShowAdvanced);
        Assert.Equal("imap.unknown-corp.test", vm.ImapHost);
        Assert.Empty(service.Verified);

        await vm.NextAsync(); // 用户核对后再点下一步
        Assert.Equal(AddAccountStep.Verify, vm.Step);
        Assert.Single(service.Verified);
    }

    [Fact]
    public void Server_validation_checks_hosts_and_ports()
    {
        var vm = Create(new FakeSetupService());
        Assert.False(vm.ValidateServer());
        vm.ImapHost = "imap.a.com";
        vm.SmtpHost = "smtp.a.com";
        vm.ImapPortText = "0";
        Assert.False(vm.ValidateServer());
        vm.ImapPortText = "70000";
        Assert.False(vm.ValidateServer());
        vm.ImapPortText = "abc";
        Assert.False(vm.ValidateServer());
        vm.ImapPortText = "993";
        vm.SmtpPortText = "465";
        Assert.True(vm.ValidateServer());
    }

    [Theory]
    [InlineData(0, MailSecureSocketOptions.SslOnConnect)]
    [InlineData(1, MailSecureSocketOptions.StartTls)]
    [InlineData(2, MailSecureSocketOptions.None)]
    public void Security_index_round_trips(int index, MailSecureSocketOptions option)
    {
        Assert.Equal(option, AddAccountViewModel.ToSocket(index));
        Assert.Equal(index, AddAccountViewModel.ToSecurityIndex(option));
    }

    [Fact]
    public async Task Cancel_discards_unsaved_draft_and_requests_close()
    {
        var service = new FakeSetupService();
        var vm = Create(service);
        var closed = false;
        vm.CloseRequested += (_, _) => closed = true;
        vm.SelectedProvider = Tile(vm, SetupProviderKind.Gmail);
        await vm.NextAsync();
        vm.ClientId = "id";
        vm.NeedsClientId = false;
        await vm.SignInAsync();

        vm.Cancel();
        Assert.True(closed);
        Assert.Single(service.Discarded);
        Assert.Empty(service.Saved);
    }

    [Fact]
    public async Task Saving_persists_chosen_color_in_settings()
    {
        using var dir = new TempDir();
        var settings = new AppSettingsStore(Path.Combine(dir.Path, "settings.json"));
        var service = new FakeSetupService();
        var vm = Create(service, settings);
        vm.SelectedProvider = Tile(vm, SetupProviderKind.Qq);
        await vm.NextAsync();
        vm.Email = "10001@qq.com";
        vm.Password = "x";
        await vm.NextAsync();
        await vm.NextAsync();
        vm.AccountColor = AddAccountViewModel.ColorChoices[1];
        await vm.NextAsync();

        var id = vm.Result!.Id;
        Assert.Equal(AddAccountViewModel.ColorChoices[1], settings.GetAccountColor(id));
    }
}

public class ProviderDetectorTests
{
    [Theory]
    [InlineData("a@qq.com", SetupProviderKind.Qq, "imap.qq.com")]
    [InlineData("a@foxmail.com", SetupProviderKind.Qq, "imap.qq.com")]
    [InlineData("a@163.com", SetupProviderKind.NetEase, "imap.163.com")]
    [InlineData("a@126.com", SetupProviderKind.NetEase, "imap.126.com")]
    [InlineData("a@GMAIL.com", SetupProviderKind.Gmail, "imap.gmail.com")]
    [InlineData("a@hotmail.com", SetupProviderKind.Outlook, "outlook.office365.com")]
    [InlineData("a@icloud.com", SetupProviderKind.OtherImap, "imap.mail.me.com")]
    public void Detects_known_domains(string email, SetupProviderKind kind, string imapHost)
    {
        var preset = ProviderDetector.Detect(email);
        Assert.NotNull(preset);
        Assert.Equal(kind, preset!.Kind);
        Assert.Equal(imapHost, preset.Imap.Host);
    }

    [Fact]
    public void Unknown_domain_and_bad_addresses_return_null()
    {
        Assert.Null(ProviderDetector.Detect("a@my-company.example"));
        Assert.Null(ProviderDetector.Detect("not-an-email"));
        Assert.Null(ProviderDetector.Detect(""));
        Assert.Null(ProviderDetector.Detect(null));
    }

    [Theory]
    [InlineData("a@b.co", true)]
    [InlineData(" a@b.co ", true)]
    [InlineData("a@b", false)]
    [InlineData("@b.com", false)]
    [InlineData("a@@b.com", false)]
    [InlineData("a b@c.com", false)]
    [InlineData("a@.com", false)]
    [InlineData("a@b..com", false)]
    public void Email_validation(string email, bool valid)
        => Assert.Equal(valid, ProviderDetector.IsValidEmail(email));

    [Fact]
    public void NetEase_uses_126_servers_for_126_addresses_and_163_otherwise()
    {
        Assert.Equal("imap.126.com", ProviderDetector.PresetFor(SetupProviderKind.NetEase, "x@126.com")!.Imap.Host);
        Assert.Equal("imap.163.com", ProviderDetector.PresetFor(SetupProviderKind.NetEase, "x@163.com")!.Imap.Host);
        Assert.Equal("imap.163.com", ProviderDetector.PresetFor(SetupProviderKind.NetEase, "")!.Imap.Host);
    }

    [Fact]
    public void Auth_code_providers_are_flagged()
    {
        Assert.True(ProviderDetector.Detect("a@qq.com")!.UsesAuthCode);
        Assert.True(ProviderDetector.Detect("a@163.com")!.UsesAuthCode);
    }
}

public class IspdbAutoconfigTests
{
    private const string Sample = """
        <?xml version="1.0" encoding="UTF-8"?>
        <clientConfig version="1.1">
          <emailProvider id="example.org">
            <domain>example.org</domain>
            <displayName>Example Mail</displayName>
            <incomingServer type="pop3">
              <hostname>pop.example.org</hostname>
              <port>995</port>
              <socketType>SSL</socketType>
              <username>%EMAILADDRESS%</username>
            </incomingServer>
            <incomingServer type="imap">
              <hostname>imap.%EMAILDOMAIN%</hostname>
              <port>143</port>
              <socketType>STARTTLS</socketType>
              <username>%EMAILLOCALPART%</username>
            </incomingServer>
            <incomingServer type="imap">
              <hostname>imaps.example.org</hostname>
              <port>993</port>
              <socketType>SSL</socketType>
              <username>%EMAILLOCALPART%</username>
            </incomingServer>
            <outgoingServer type="smtp">
              <hostname>smtp.example.org</hostname>
              <port>587</port>
              <socketType>STARTTLS</socketType>
              <username>%EMAILADDRESS%</username>
            </outgoingServer>
          </emailProvider>
        </clientConfig>
        """;

    [Fact]
    public void Parses_imap_smtp_and_prefers_ssl_and_fills_placeholders()
    {
        var result = IspdbAutoconfig.Parse(Sample, "jane@example.org");
        Assert.NotNull(result);
        Assert.Equal("Example Mail", result!.DisplayName);
        Assert.Equal("imaps.example.org", result.Imap.Host);
        Assert.Equal(993, result.Imap.Port);
        Assert.Equal(MailSecureSocketOptions.SslOnConnect, result.Imap.SocketOptions);
        Assert.Equal("smtp.example.org", result.Smtp.Host);
        Assert.Equal(587, result.Smtp.Port);
        Assert.Equal(MailSecureSocketOptions.StartTls, result.Smtp.SocketOptions);
        Assert.Equal("jane", result.UserName);
    }

    [Fact]
    public void Missing_port_falls_back_to_the_default_for_the_socket_type()
    {
        const string xml = """
            <clientConfig><emailProvider id="x">
              <incomingServer type="imap"><hostname>i.x.org</hostname><socketType>SSL</socketType><username>%EMAILADDRESS%</username></incomingServer>
              <outgoingServer type="smtp"><hostname>s.x.org</hostname><socketType>SSL</socketType></outgoingServer>
            </emailProvider></clientConfig>
            """;
        var result = IspdbAutoconfig.Parse(xml, "a@x.org");
        Assert.Equal(993, result!.Imap.Port);
        Assert.Equal(465, result.Smtp.Port);
        Assert.Equal("a@x.org", result.UserName);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("<html>not xml")]
    [InlineData("<clientConfig/>")]
    [InlineData("<clientConfig><emailProvider><incomingServer type=\"pop3\"><hostname>p</hostname></incomingServer></emailProvider></clientConfig>")]
    public void Bad_or_incomplete_documents_return_null(string? xml)
        => Assert.Null(IspdbAutoconfig.Parse(xml, "a@example.org"));

    [Fact]
    public void Doctype_is_rejected_to_block_xxe()
    {
        const string xml = "<?xml version=\"1.0\"?><!DOCTYPE x [<!ENTITY e SYSTEM \"file:///c:/windows/win.ini\">]><clientConfig><emailProvider>&e;</emailProvider></clientConfig>";
        Assert.Null(IspdbAutoconfig.Parse(xml, "a@example.org"));
    }

    [Fact]
    public void Url_is_built_only_for_plausible_domains()
    {
        Assert.Equal("https://autoconfig.thunderbird.net/v1.1/example.org", IspdbAutoconfig.BuildUrl("Example.org"));
        Assert.Null(IspdbAutoconfig.BuildUrl("exa mple.org"));
        Assert.Null(IspdbAutoconfig.BuildUrl("evil.com/../x"));
        Assert.Null(IspdbAutoconfig.BuildUrl("localhost"));
        Assert.Null(IspdbAutoconfig.BuildUrl(null));
    }
}

internal sealed class TempDir : IDisposable
{
    public TempDir()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "mc-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public void Dispose()
    {
        try { Directory.Delete(Path, true); } catch { /* ignore */ }
    }
}

public class CredentialAndSettingsStoreTests
{
    [Fact]
    public async Task Dpapi_store_round_trips_overwrites_and_deletes()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var dir = new TempDir();
        var path = Path.Combine(dir.Path, "credentials.dat");
        var store = new DpapiCredentialStore(path);

        Assert.Null(await store.GetSecretAsync("k1"));
        await store.SaveSecretAsync("k1", "密码-授权码 abc");
        await store.SaveSecretAsync("k2", "second");
        await store.SaveSecretAsync("k1", "updated");

        // 新实例读同一个文件（模拟重启）
        var reopened = new DpapiCredentialStore(path);
        Assert.Equal("updated", await reopened.GetSecretAsync("k1"));
        Assert.Equal("second", await reopened.GetSecretAsync("k2"));

        // 文件内容不是明文
        var raw = await File.ReadAllBytesAsync(path);
        Assert.DoesNotContain("updated", System.Text.Encoding.UTF8.GetString(raw));

        await reopened.DeleteSecretAsync("k1");
        Assert.Null(await new DpapiCredentialStore(path).GetSecretAsync("k1"));
        Assert.Equal("second", await new DpapiCredentialStore(path).GetSecretAsync("k2"));
    }

    [Fact]
    public async Task Connection_store_round_trips_settings_and_password_and_deletes_all()
    {
        var mem = new InMemoryCredentialStore();
        var store = new AccountConnectionStore(mem);
        var id = Guid.NewGuid();
        var conn = new AccountConnection(
            SetupProviderKind.Qq, "10001@qq.com",
            new ImapServerSettings("imap.qq.com", 993, MailSecureSocketOptions.SslOnConnect),
            new SmtpServerSettings("smtp.qq.com", 465, MailSecureSocketOptions.SslOnConnect));

        await store.SaveAsync(id, conn, "authcode");
        var loaded = await store.LoadAsync(id);
        Assert.NotNull(loaded);
        Assert.Equal(SetupProviderKind.Qq, loaded!.Kind);
        Assert.Equal("imap.qq.com", loaded.Imap!.Host);
        Assert.Equal(465, loaded.Smtp!.Port);
        Assert.Equal("10001@qq.com\nauthcode", await mem.GetSecretAsync(AccountConnectionStore.ImapSecretKey(id)));

        await store.DeleteAllAsync(id);
        Assert.Null(await store.LoadAsync(id));
        Assert.Null(await mem.GetSecretAsync(AccountConnectionStore.ImapSecretKey(id)));
    }

    [Fact]
    public void App_settings_persist_and_account_colors_round_trip()
    {
        using var dir = new TempDir();
        var path = Path.Combine(dir.Path, "settings.json");
        var store = new AppSettingsStore(path);
        var id = Guid.NewGuid();
        store.Update(s =>
        {
            s.GmailClientId = "cid";
            s.DemoDataDismissed = true;
        });
        store.SetAccountColor(id, "#0F6CBD");

        var reopened = new AppSettingsStore(path);
        Assert.True(reopened.Snapshot().DemoDataDismissed);
        Assert.Equal("#0F6CBD", reopened.GetAccountColor(id));
        reopened.SetAccountColor(id, null);
        Assert.Null(new AppSettingsStore(path).GetAccountColor(id));
    }

    [Fact]
    public void App_settings_tolerate_a_corrupt_file()
    {
        using var dir = new TempDir();
        var path = Path.Combine(dir.Path, "settings.json");
        File.WriteAllText(path, "{ not json");
        var store = new AppSettingsStore(path);
        Assert.False(store.Snapshot().DemoDataDismissed);
    }
}

public class SetupErrorClassifierTests
{
    [Fact]
    public void Mailkit_auth_failure_on_qq_asks_for_auth_code()
    {
        var error = SetupErrorClassifier.Classify(
            new MailKit.Security.AuthenticationException("bad"), SetupProviderKind.Qq, SetupStage.Imap, "imap.qq.com");
        Assert.Equal(SetupErrorKind.NeedAuthCode, error.Kind);
        Assert.Contains("授权码", error.Message);
    }

    [Fact]
    public void Mailkit_auth_failure_on_generic_imap_is_plain_auth_failure()
    {
        var error = SetupErrorClassifier.Classify(
            new MailKit.Security.AuthenticationException("bad"), SetupProviderKind.OtherImap, SetupStage.Imap);
        Assert.Equal(SetupErrorKind.AuthFailed, error.Kind);
    }

    [Fact]
    public void Dns_failure_and_refused_connections_are_distinguished()
    {
        var dns = SetupErrorClassifier.Classify(
            new System.Net.Sockets.SocketException((int)System.Net.Sockets.SocketError.HostNotFound),
            SetupProviderKind.OtherImap, SetupStage.Imap, "imap.nowhere.test");
        Assert.Equal(SetupErrorKind.HostNotFound, dns.Kind);
        Assert.Contains("imap.nowhere.test", dns.Message);

        var refused = SetupErrorClassifier.Classify(
            new System.Net.Sockets.SocketException((int)System.Net.Sockets.SocketError.ConnectionRefused),
            SetupProviderKind.OtherImap, SetupStage.Smtp, "smtp.a.test");
        Assert.Equal(SetupErrorKind.ServerUnreachable, refused.Kind);
    }

    [Fact]
    public void Tls_failures_are_classified()
    {
        var error = SetupErrorClassifier.Classify(
            new System.Security.Authentication.AuthenticationException("ssl"), SetupProviderKind.OtherImap, SetupStage.Imap);
        Assert.Equal(SetupErrorKind.TlsError, error.Kind);
    }

    [Fact]
    public void Timeout_and_cancel_are_classified()
    {
        Assert.Equal(SetupErrorKind.Timeout,
            SetupErrorClassifier.Classify(new TimeoutException(), SetupProviderKind.Qq, SetupStage.Imap, "h").Kind);
        Assert.Equal(SetupErrorKind.Cancelled,
            SetupErrorClassifier.Classify(new OperationCanceledException(), SetupProviderKind.Qq, SetupStage.Imap).Kind);
    }

    [Fact]
    public void Wam_provider_error_message_includes_broker_redirect_hint()
    {
        var ex = new Microsoft.Identity.Client.MsalClientException(
            "wam_provider_error_2156265475", "WAM Error\n\n2156265475");
        var error = SetupErrorClassifier.Classify(ex, SetupProviderKind.Outlook, SetupStage.Login);
        Assert.Equal(SetupErrorKind.AuthFailed, error.Kind);
        Assert.Contains("ms-appx-web://microsoft.aad.brokerplugin", error.Message);
        Assert.Contains("身份验证", error.Message);
    }
}

public class ShellAccountFlagsTests
{
    [Fact]
    public async Task Empty_database_with_seed_off_clears_demo_and_offers_first_run_prompt()
    {
        await using var tmp = await TempDb.CreateSeededAsync();
        using var dir = new TempDir();
        var settings = new AppSettingsStore(Path.Combine(dir.Path, "settings.json"));
        var shell = new ShellViewModel(tmp.Database, new MailClient.Storage.Services.OutboxSendService(tmp.Database),
            null, null, new FakeSetupService(), settings);
        await shell.InitializeAsync(seedDemoData: false);

        Assert.True(shell.HasNoRealAccounts);
        Assert.False(shell.HasDemoData);
        Assert.False(shell.ShowClearDemo);
        Assert.True(shell.ShouldShowFirstRunPrompt);
        Assert.True(settings.Snapshot().DemoDataDismissed);
        Assert.Empty(await tmp.Database.ListAccountsAsync());

        shell.DismissFirstRunPrompt();
        Assert.False(shell.ShouldShowFirstRunPrompt);
    }

    [Fact]
    public async Task Real_account_survives_demo_clear_on_init()
    {
        await using var tmp = await TempDb.CreateSeededAsync();
        using var dir = new TempDir();
        var settings = new AppSettingsStore(Path.Combine(dir.Path, "settings.json"));
        var now = DateTimeOffset.UtcNow;
        var realId = Guid.NewGuid();
        await tmp.Database.UpsertAccountAsync(new AccountEntity(
            realId, "真实", "real@qq.com", "imap.generic", default, now, now, null, true));

        var shell = new ShellViewModel(tmp.Database, new MailClient.Storage.Services.OutboxSendService(tmp.Database),
            null, null, new FakeSetupService(), settings);
        await shell.InitializeAsync(seedDemoData: false);
        Assert.True(shell.HasRealAccounts);
        Assert.False(shell.HasDemoData);
        Assert.False(shell.ShowClearDemo);
        Assert.False(shell.ShouldShowFirstRunPrompt);
        Assert.True(settings.Snapshot().DemoDataDismissed);

        var accounts = await tmp.Database.ListAccountsAsync();
        Assert.Single(accounts);
        Assert.Equal(realId, accounts[0].Id);
    }

    [Fact]
    public async Task Removing_an_account_calls_the_setup_service_and_reloads_the_tree()
    {
        await using var tmp = await TempDb.CreateAsync();
        using var dir = new TempDir();
        var settings = new AppSettingsStore(Path.Combine(dir.Path, "settings.json"));
        settings.Update(s => s.DemoDataDismissed = true);
        var now = DateTimeOffset.UtcNow;
        var id = Guid.NewGuid();
        await tmp.Database.UpsertAccountAsync(new AccountEntity(
            id, "可删", "gone@example.com", "imap.generic", default, now, now, null, true));
        var service = new FakeSetupService();
        var shell = new ShellViewModel(tmp.Database, new MailClient.Storage.Services.OutboxSendService(tmp.Database),
            null, null, service, settings);
        await shell.InitializeAsync(seedDemoData: false);

        await shell.RemoveAccountAsync(id);
        Assert.Equal(id, Assert.Single(service.Removed));
    }

    [Fact]
    public async Task Database_delete_account_cascades_and_clear_demo_keeps_real_data()
    {
        await using var tmp = await TempDb.CreateSeededAsync();
        var now = DateTimeOffset.UtcNow;
        var realId = Guid.NewGuid();
        await tmp.Database.UpsertAccountAsync(new AccountEntity(
            realId, "真实", "real@qq.com", "imap.generic", default, now, now, null, true));
        var removed = await DemoDataSeeder.ClearDemoDataAsync(tmp.Database);
        Assert.True(removed >= 2);
        var left = await tmp.Database.ListAccountsAsync();
        Assert.Single(left);
        Assert.Equal(realId, left[0].Id);
    }
}
