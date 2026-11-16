using System.Net;
using System.Net.Sockets;
using MailClient.Auth;
using MailClient.Core;
using MailClient.Core.Auth;
using MailClient.Core.Sending;
using MailClient.Core.Services;
using MailClient.Providers.Setup;
using MailClient.Providers.Sync;
using MailClient.Storage.Services;
using MailClient.ViewModels;
using MailKit.Security;
using Microsoft.Identity.Client;

namespace MailClient.Tests;

public class ReloginTrackerTests
{
    [Fact]
    public void FirstAuthFailure_PromptsOnce_RetriesDoNotRePrompt()
    {
        var tracker = new ReloginTracker();
        var id = Guid.NewGuid();
        var events = new List<bool>();
        tracker.StateChanged += (_, e) => events.Add(e.NeedsRelogin);

        Assert.True(tracker.ReportAuthFailure(id));
        Assert.True(tracker.NeedsRelogin(id));
        Assert.True(tracker.ShouldPrompt(id));
        Assert.Equal(new[] { id }, tracker.PendingPrompts);

        Assert.True(tracker.TryBeginPrompt(id));
        Assert.False(tracker.TryBeginPrompt(id));

        // 后台每轮重试都失败：不再触发事件，也不再弹
        Assert.False(tracker.ReportAuthFailure(id));
        Assert.False(tracker.ReportAuthFailure(id));
        Assert.False(tracker.ShouldPrompt(id));
        Assert.Empty(tracker.PendingPrompts);
        Assert.Equal(new[] { true }, events);
    }

    [Fact]
    public void Later_SuppressesUntilSuccess_ThenNewFailurePromptsAgain()
    {
        var tracker = new ReloginTracker();
        var id = Guid.NewGuid();
        tracker.ReportAuthFailure(id);
        Assert.True(tracker.TryBeginPrompt(id));
        tracker.Snooze(id);      // 用户点“稍后”

        tracker.ReportAuthFailure(id);
        Assert.False(tracker.ShouldPrompt(id));
        Assert.False(tracker.TryBeginPrompt(id));
        Assert.True(tracker.NeedsRelogin(id));      // 红色感叹号仍在

        // 重新登录成功：标记和“稍后”都清掉
        Assert.True(tracker.ReportSuccess(id));
        Assert.False(tracker.NeedsRelogin(id));

        // 之后再次失效：这是新的一次，应该再弹
        Assert.True(tracker.ReportAuthFailure(id));
        Assert.True(tracker.ShouldPrompt(id));
        Assert.True(tracker.TryBeginPrompt(id));
    }

    [Fact]
    public void SnoozeBeforePrompt_StillSuppresses()
    {
        var tracker = new ReloginTracker();
        var id = Guid.NewGuid();
        tracker.ReportAuthFailure(id);
        tracker.Snooze(id);
        Assert.False(tracker.TryBeginPrompt(id));
    }

    [Fact]
    public void Success_WhenNotFlagged_IsNoOp_AndRaisesNothing()
    {
        var tracker = new ReloginTracker();
        var raised = 0;
        tracker.StateChanged += (_, _) => raised++;
        Assert.False(tracker.ReportSuccess(Guid.NewGuid()));
        Assert.Equal(0, raised);
    }

    [Fact]
    public void Accounts_AreIndependent()
    {
        var tracker = new ReloginTracker();
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        tracker.ReportAuthFailure(a);
        tracker.ReportAuthFailure(b);
        tracker.TryBeginPrompt(a);
        tracker.Snooze(a);
        Assert.Equal(new[] { b }, tracker.PendingPrompts);
        tracker.ReportSuccess(b);
        Assert.True(tracker.NeedsRelogin(a));
        Assert.False(tracker.NeedsRelogin(b));
    }

    [Fact]
    public async Task WaitUntilCleared_CompletesOnSuccess()
    {
        var tracker = new ReloginTracker();
        var id = Guid.NewGuid();
        Assert.True(tracker.WaitUntilClearedAsync(id, CancellationToken.None).IsCompleted);

        tracker.ReportAuthFailure(id);
        var wait = tracker.WaitUntilClearedAsync(id, CancellationToken.None);
        Assert.False(wait.IsCompleted);
        tracker.ReportSuccess(id);
        await wait.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task WaitUntilCleared_HonoursCancellation()
    {
        var tracker = new ReloginTracker();
        var id = Guid.NewGuid();
        tracker.ReportAuthFailure(id);
        using var cts = new CancellationTokenSource();
        var wait = tracker.WaitUntilClearedAsync(id, cts.Token);
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => wait);
    }
}

public class ConnectionFailureClassifierTests
{
    public static TheoryData<Exception> AuthFailures => new()
    {
        new GmailAuthRequiredException("Gmail 登录已失效，请重新登录。"),
        new GmailAuthRequiredException("尚未登录 Gmail，请先完成 Google 授权。"),
        new GoogleOAuthException("invalid_grant", "Token has been expired or revoked."),
        new MsalUiRequiredException("invalid_grant", "AADSTS70000"),
        new MsalUiRequiredException("no_account", "本地无 MSAL 账户缓存"),
        new MsalServiceException("invalid_grant", "refresh token expired"),
        new MailKit.Security.AuthenticationException("Invalid credentials"),
        new ClassifiedFailureException(FailureKind.Auth, "授权已失效"),
        new InvalidOperationException("凭据库中找不到密钥：imap:x"),
        new InvalidOperationException("Microsoft 登录失败", new MsalUiRequiredException("interaction_required", "x")),
        new AggregateException(new SocketException((int)SocketError.TimedOut), new GmailAuthRequiredException("x")),
        new IOException("wrapped", new MailKit.Security.AuthenticationException("bad password")),
    };

    public static TheoryData<Exception> TransientFailures => new()
    {
        new SocketException((int)SocketError.ConnectionRefused),
        new SocketException((int)SocketError.HostNotFound),
        new HttpRequestException("No such host is known."),
        new TimeoutException(),
        new TaskCanceledException("HttpClient timeout"),
        new System.Security.Authentication.AuthenticationException("TLS handshake failed"),
        new SslHandshakeException("cert"),
        new IOException("Unable to read data from the transport connection."),
        new MailKit.ServiceNotConnectedException(),
        new ClassifiedFailureException(FailureKind.Transient, "网络"),
        new GoogleOAuthException("temporarily_unavailable", null),
        new InvalidOperationException("wrapper", new HttpRequestException("offline")),
    };

    public static TheoryData<Exception> OtherFailures => new()
    {
        new InvalidOperationException("还没有填写 Google 的 ClientId。"),
        new GoogleOAuthException("invalid_client", "The OAuth client was not found."),
        new ClassifiedFailureException(FailureKind.Configuration, "尚未配置 Microsoft ClientId"),
        new InvalidOperationException("找不到该账号的服务器设置，请移除后重新添加。"),
    };

    [Theory]
    [MemberData(nameof(AuthFailures))]
    public void AuthFailures_AreAuth(Exception ex)
    {
        Assert.Equal(ConnectionStatus.AuthFailed, ConnectionFailureClassifier.Classify(ex));
        Assert.True(ConnectionFailureClassifier.IsAuthFailure(ex));
    }

    [Theory]
    [MemberData(nameof(TransientFailures))]
    public void NetworkAndTls_AreTransient(Exception ex)
    {
        Assert.Equal(ConnectionStatus.Transient, ConnectionFailureClassifier.Classify(ex));
        Assert.False(ConnectionFailureClassifier.IsAuthFailure(ex));
    }

    [Theory]
    [MemberData(nameof(OtherFailures))]
    public void Configuration_IsNeitherAuthNorTransient(Exception ex)
    {
        Assert.Equal(ConnectionStatus.Failed, ConnectionFailureClassifier.Classify(ex));
    }
}

public class ReloginFlowTests
{
    [Theory]
    [InlineData(ConnectionStatus.Ok, ReloginCheckDecision.ShowHealthy)]
    [InlineData(ConnectionStatus.AuthFailed, ReloginCheckDecision.StartReauth)]
    [InlineData(ConnectionStatus.Transient, ReloginCheckDecision.ShowNetworkProblem)]
    [InlineData(ConnectionStatus.Failed, ReloginCheckDecision.ShowCheckFailed)]
    public void Decide_MapsCheckResultToNextStep(ConnectionStatus status, ReloginCheckDecision expected)
        => Assert.Equal(expected, ReloginFlow.Decide(new ConnectionCheckResult(status, "x")));

    [Fact]
    public void Apply_OkClears_AuthFlags_OthersLeaveStateAlone()
    {
        var tracker = new ReloginTracker();
        var id = Guid.NewGuid();

        ReloginFlow.Apply(tracker, id, new ConnectionCheckResult(ConnectionStatus.AuthFailed));
        Assert.True(tracker.NeedsRelogin(id));

        ReloginFlow.Apply(tracker, id, new ConnectionCheckResult(ConnectionStatus.Transient));
        Assert.True(tracker.NeedsRelogin(id));      // 网络问题不能证明登录恢复了

        ReloginFlow.Apply(tracker, id, ConnectionCheckResult.Healthy);
        Assert.False(tracker.NeedsRelogin(id));

        ReloginFlow.Apply(tracker, id, new ConnectionCheckResult(ConnectionStatus.Failed, "配置"));
        Assert.False(tracker.NeedsRelogin(id));     // 配置问题不提示重新登录
    }

    [Fact]
    public void Texts_MatchSpec()
    {
        Assert.Equal("「平的Gmail」登录已失效", ReloginTexts.ExpiredTitle("平的Gmail"));
        Assert.Equal("邮件已停止同步，请重新登录以恢复。本地邮件不会被删除。", ReloginTexts.ExpiredBody);
        Assert.Equal("「平的Gmail」连接正常", ReloginTexts.HealthyTitle("平的Gmail"));
        Assert.Equal("登录已失效，右键重新登录", ReloginTexts.SidebarTooltip);
        Assert.Contains("删除该账户后重新添加", ReloginTexts.HealthyBody);
        Assert.Contains("网络", ReloginTexts.NetworkBody(null));
    }
}

/// <summary>用本机假 IMAP 服务器跑真实的同步 / 连接检查 / 重新登录代码（不连外网，不碰真实数据）。</summary>
public class ReloginImapIntegrationTests
{
    private static async Task<(TempDb Db, InMemoryCredentialStore Store, AppSettingsStore Settings, TempDir Dir, Guid Id)> SetupAsync(int port, string savedPassword)
    {
        var db = await TempDb.CreateAsync();
        var store = new InMemoryCredentialStore();
        var dir = new TempDir();
        var settings = new AppSettingsStore(Path.Combine(dir.Path, "settings.json"));
        settings.Update(s => s.DemoDataDismissed = true);
        var id = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        await db.Database.UpsertAccountAsync(new AccountEntity(id, "测试 IMAP", "user@example.test", "imap.generic", AuthKind.Password, now, now, null, true));
        await new AccountConnectionStore(store).SaveAsync(
            id,
            new AccountConnection(
                SetupProviderKind.OtherImap,
                "user@example.test",
                new ImapServerSettings("127.0.0.1", port, MailSecureSocketOptions.None),
                new SmtpServerSettings("127.0.0.1", port, MailSecureSocketOptions.None)),
            savedPassword);
        return (db, store, settings, dir, id);
    }

    private static int UnusedPort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    [Fact]
    public async Task Sync_WithRejectedPassword_FlagsAccount_AndBackgroundRetriesStopHammering()
    {
        await using var server = new FakeImapServer(acceptedPassword: "good");
        var (tmp, store, settings, dir, id) = await SetupAsync(server.Port, "expired");
        await using var _ = tmp;
        using var __ = dir;
        var tracker = new ReloginTracker();
        var sync = new AccountSyncService(tmp.Database, store, settings, relogin: tracker);
        string? reported = null;
        sync.ProgressChanged += (_, p) =>
        {
            if (p.Phase == SyncPhase.Failed)
            {
                reported = p.Error;
            }
        };

        await Assert.ThrowsAnyAsync<Exception>(() => sync.SyncAccountAsync(id, new SyncRequest(Background: true)));
        Assert.True(tracker.NeedsRelogin(id));
        Assert.Equal(ReloginTexts.SyncAuthFailed, reported);
        var attempts = server.LoginAttempts;
        Assert.True(attempts >= 1);

        // 失效期间的后台轮询直接跳过，不再登录服务器
        await sync.SyncAccountAsync(id, new SyncRequest(Background: true));
        await sync.SyncAllAsync(new SyncRequest(Background: true));
        Assert.Equal(attempts, server.LoginAttempts);
    }

    [Fact]
    public async Task CheckConnection_ReportsAuthFailed_Ok_AndTransient()
    {
        await using var server = new FakeImapServer(acceptedPassword: "good");
        var (tmp, store, settings, dir, id) = await SetupAsync(server.Port, "expired");
        await using var _ = tmp;
        using var __ = dir;
        var auth = new AccountAuthService(tmp.Database, store, settings);

        var failed = await auth.CheckConnectionAsync(id, CancellationToken.None);
        Assert.Equal(ConnectionStatus.AuthFailed, failed.Status);
        Assert.False(string.IsNullOrWhiteSpace(failed.Message));

        server.AcceptedPassword = "expired";
        var ok = await auth.CheckConnectionAsync(id, CancellationToken.None);
        Assert.Equal(ConnectionStatus.Ok, ok.Status);

        var (tmp2, store2, settings2, dir2, id2) = await SetupAsync(UnusedPort(), "whatever");
        await using var ___ = tmp2;
        using var ____ = dir2;
        var offline = await new AccountAuthService(tmp2.Database, store2, settings2).CheckConnectionAsync(id2, CancellationToken.None);
        Assert.Equal(ConnectionStatus.Transient, offline.Status);
    }

    [Fact]
    public async Task ImapReauth_WrongPassword_KeepsOldCredentials_RightPasswordReplacesThem()
    {
        await using var server = new FakeImapServer(acceptedPassword: "new-secret");
        var (tmp, store, settings, dir, id) = await SetupAsync(server.Port, "expired");
        await using var _ = tmp;
        using var __ = dir;
        var auth = new AccountAuthService(tmp.Database, store, settings);
        Assert.True(await auth.RequiresPasswordAsync(id, CancellationToken.None));
        var key = AccountConnectionStore.ImapSecretKey(id);

        var wrong = await auth.ReauthenticateAsync(id, "typo", CancellationToken.None);
        Assert.False(wrong.Success);
        Assert.False(string.IsNullOrWhiteSpace(wrong.Error));
        Assert.Equal("user@example.test\nexpired", await store.GetSecretAsync(key));

        var right = await auth.ReauthenticateAsync(id, "new-secret", CancellationToken.None);
        Assert.True(right.Success, right.Error);
        Assert.Equal("user@example.test\nnew-secret", await store.GetSecretAsync(key));
        Assert.NotNull(await tmp.Database.GetAccountAsync(id));      // 账号没被删
        Assert.Equal(ConnectionStatus.Ok, (await auth.CheckConnectionAsync(id, CancellationToken.None)).Status);
    }
}

/// <summary>Shell 层：失效标记 → 左栏节点 / 提醒事件；连接检查与重新登录的状态衔接。</summary>
public class ShellReloginTests
{
    private sealed class FakeAuth : IAccountAuthService
    {
        public ConnectionCheckResult Check { get; set; } = ConnectionCheckResult.Healthy;
        public ReauthResult Reauth { get; set; } = ReauthResult.Ok;
        public int ReauthCalls { get; private set; }

        public Task<ConnectionCheckResult> CheckConnectionAsync(Guid accountId, CancellationToken cancellationToken) => Task.FromResult(Check);

        public Task<bool> RequiresPasswordAsync(Guid accountId, CancellationToken cancellationToken) => Task.FromResult(false);

        public Task<ReauthResult> ReauthenticateAsync(Guid accountId, string? password, CancellationToken cancellationToken)
        {
            ReauthCalls++;
            return Task.FromResult(Reauth);
        }
    }

    private static async Task<(ShellViewModel Shell, TempDb Db, TempDir Dir, Guid Id)> CreateAsync(ReloginTracker tracker, FakeAuth auth)
    {
        var tmp = await TempDb.CreateAsync();
        var id = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        await tmp.Database.UpsertAccountAsync(new AccountEntity(id, "平的Gmail", "someone@example.test", GmailEndpoints.ProviderId, AuthKind.OAuth, now, now, null, true));
        var dir = new TempDir();
        var settings = new AppSettingsStore(Path.Combine(dir.Path, "settings.json"));
        settings.Update(s => s.DemoDataDismissed = true);
        var shell = new ShellViewModel(tmp.Database, new OutboxSendService(tmp.Database), null, null, null, settings,
            relogin: tracker, accountAuth: auth);
        await shell.InitializeAsync(seedDemoData: false);
        return (shell, tmp, dir, id);
    }

    [Fact]
    public async Task AuthFailure_MarksNode_AndRequestsPromptOnce()
    {
        var tracker = new ReloginTracker();
        var (shell, tmp, dir, id) = await CreateAsync(tracker, new FakeAuth());
        await using var _ = tmp;
        using var __ = dir;
        var requested = 0;
        shell.ReloginPromptRequested += (_, _) => requested++;

        tracker.ReportAuthFailure(id);
        tracker.ReportAuthFailure(id);

        Assert.Equal(1, requested);
        Assert.True(shell.Tree.Accounts.Single().NeedsRelogin);
        Assert.Equal(new[] { id }, shell.PendingReloginPrompts);
        Assert.True(shell.TryBeginReloginPrompt(id));
        Assert.Empty(shell.PendingReloginPrompts);

        // 树重建（例如同步后文件夹变化）后仍显示感叹号
        await shell.Tree.LoadAsync();
        Assert.True(shell.Tree.Accounts.Single().NeedsRelogin);
    }

    [Fact]
    public async Task HealthyCheck_ClearsFlag()
    {
        var tracker = new ReloginTracker();
        var auth = new FakeAuth { Check = ConnectionCheckResult.Healthy };
        var (shell, tmp, dir, id) = await CreateAsync(tracker, auth);
        await using var _ = tmp;
        using var __ = dir;
        tracker.ReportAuthFailure(id);

        var result = await shell.CheckAccountConnectionAsync(id);

        Assert.Equal(ReloginCheckDecision.ShowHealthy, ReloginFlow.Decide(result));
        Assert.False(tracker.NeedsRelogin(id));
        Assert.False(shell.Tree.Accounts.Single().NeedsRelogin);
        Assert.Equal(0, auth.ReauthCalls);      // 连接正常时不会自动重新登录
    }

    [Fact]
    public async Task AuthFailedCheck_FlagsAccount_AndDecidesReauth()
    {
        var tracker = new ReloginTracker();
        var auth = new FakeAuth { Check = new ConnectionCheckResult(ConnectionStatus.AuthFailed, "Gmail 登录已失效") };
        var (shell, tmp, dir, id) = await CreateAsync(tracker, auth);
        await using var _ = tmp;
        using var __ = dir;

        var result = await shell.CheckAccountConnectionAsync(id);

        Assert.Equal(ReloginCheckDecision.StartReauth, ReloginFlow.Decide(result));
        Assert.True(tracker.NeedsRelogin(id));
    }

    [Fact]
    public async Task NetworkCheck_DoesNotFlag()
    {
        var tracker = new ReloginTracker();
        var auth = new FakeAuth { Check = new ConnectionCheckResult(ConnectionStatus.Transient, "网络") };
        var (shell, tmp, dir, id) = await CreateAsync(tracker, auth);
        await using var _ = tmp;
        using var __ = dir;

        var result = await shell.CheckAccountConnectionAsync(id);

        Assert.Equal(ReloginCheckDecision.ShowNetworkProblem, ReloginFlow.Decide(result));
        Assert.False(tracker.NeedsRelogin(id));
    }

    [Fact]
    public async Task SuccessfulReauth_ClearsFlagAndSnooze_FailedReauthKeepsIt()
    {
        var tracker = new ReloginTracker();
        var auth = new FakeAuth { Reauth = new ReauthResult(false, "登录的账号不一致", AccountMismatch: true) };
        var (shell, tmp, dir, id) = await CreateAsync(tracker, auth);
        await using var _ = tmp;
        using var __ = dir;
        tracker.ReportAuthFailure(id);
        shell.TryBeginReloginPrompt(id);
        shell.SnoozeReloginPrompt(id);

        var mismatch = await shell.ReauthenticateAccountAsync(id);
        Assert.False(mismatch.Success);
        Assert.True(mismatch.AccountMismatch);
        Assert.True(tracker.NeedsRelogin(id));

        auth.Reauth = ReauthResult.Ok;
        var ok = await shell.ReauthenticateAccountAsync(id);
        Assert.True(ok.Success);
        Assert.False(tracker.NeedsRelogin(id));
        Assert.False(shell.Tree.Accounts.Single().NeedsRelogin);

        // 成功之后再失效：重新提醒
        tracker.ReportAuthFailure(id);
        Assert.Equal(new[] { id }, shell.PendingReloginPrompts);
    }

    [Fact]
    public async Task StartupCheck_FeedsTracker()
    {
        var tracker = new ReloginTracker();
        var auth = new FakeAuth { Check = new ConnectionCheckResult(ConnectionStatus.AuthFailed, "x") };
        var (shell, tmp, dir, id) = await CreateAsync(tracker, auth);
        await using var _ = tmp;
        using var __ = dir;

        await shell.CheckAllAccountsAsync();

        Assert.True(tracker.NeedsRelogin(id));
        Assert.Equal(new[] { id }, shell.PendingReloginPrompts);
    }
}
