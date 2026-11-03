using System.Net;
using System.Text;
using System.Text.Json;
using MailClient.Auth;
using MailClient.Core;

namespace MailClient.Tests;

public class GmailOAuthTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 31, 12, 0, 0, TimeSpan.Zero);

    private static GmailAccountConfig Config(string? secret = null, string email = "me@gmail.com")
        => new(Guid.NewGuid(), email, "Me", "client-id.apps.googleusercontent.com", ClientSecret: secret);

    // ---------- PKCE ----------

    [Fact]
    public void Pkce_Challenge_Matches_Rfc7636_Vector()
    {
        Assert.Equal(
            "E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM",
            Pkce.CreateChallenge("dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXk"));
    }

    [Fact]
    public void Pkce_Verifier_Is_43_Url_Safe_Chars_And_Random()
    {
        var a = Pkce.CreateVerifier();
        var b = Pkce.CreateVerifier();
        Assert.Equal(43, a.Length);
        Assert.Matches("^[A-Za-z0-9_-]+$", a);
        Assert.NotEqual(a, b);
        Assert.Throws<ArgumentOutOfRangeException>(() => Pkce.CreateVerifier(16));
    }

    // ---------- 授权 URL ----------

    [Fact]
    public void AuthorizationUrl_Contains_Pkce_Offline_And_Scopes()
    {
        var url = GoogleAuthorizationUrl.Build(
            "cid", "http://127.0.0.1:5555/", GmailScopes.Default, "st", "chal", loginHint: "me@gmail.com");
        Assert.StartsWith("https://accounts.google.com/o/oauth2/v2/auth?", url.AbsoluteUri);

        var q = LoopbackQuery(url);
        Assert.Equal("cid", q["client_id"]);
        Assert.Equal("http://127.0.0.1:5555/", q["redirect_uri"]);
        Assert.Equal("code", q["response_type"]);
        Assert.Equal("S256", q["code_challenge_method"]);
        Assert.Equal("chal", q["code_challenge"]);
        Assert.Equal("st", q["state"]);
        Assert.Equal("offline", q["access_type"]);
        Assert.Equal("consent", q["prompt"]);
        Assert.Equal("me@gmail.com", q["login_hint"]);
        Assert.Equal("https://mail.google.com/ openid email profile", q["scope"]);
        Assert.False(q.ContainsKey("client_secret"));
    }

    [Fact]
    public void AuthorizationUrl_Validates_Input()
    {
        Assert.Throws<ArgumentException>(() => GoogleAuthorizationUrl.Build("", "http://127.0.0.1:1/", GmailScopes.Default, "s", "c"));
        Assert.Throws<ArgumentException>(() => GoogleAuthorizationUrl.Build("c", "http://127.0.0.1:1/", Array.Empty<string>(), "s", "c"));
    }

    private static Dictionary<string, string> LoopbackQuery(Uri url)
        => LoopbackAuthorizationListener.ParseQuery(url.PathAndQuery);

    // ---------- token 响应解析 ----------

    [Fact]
    public void TokenResponse_Parses_Expiry_And_Optional_Refresh()
    {
        var r = GoogleTokenResponse.Parse("""{"access_token":"a","expires_in":3599,"scope":"s","token_type":"Bearer"}""", Now);
        Assert.Equal("a", r.AccessToken);
        Assert.Null(r.RefreshToken);
        Assert.Equal(Now.AddSeconds(3599), r.ExpiresOn);

        var full = GoogleTokenResponse.Parse("""{"access_token":"a","refresh_token":"r","expires_in":10}""", Now);
        Assert.Equal("r", full.RefreshToken);
    }

    [Fact]
    public void TokenResponse_Error_Throws_With_InvalidGrant_Flag()
    {
        var ex = Assert.Throws<GoogleOAuthException>(() =>
            GoogleTokenResponse.Parse("""{"error":"invalid_grant","error_description":"Token has been expired or revoked."}""", Now));
        Assert.True(ex.IsInvalidGrant);
        Assert.Contains("revoked", ex.Message);
    }

    [Fact]
    public void IdToken_Email_Is_Read_From_Payload()
    {
        static string B64(string s) => Pkce.Base64Url(Encoding.UTF8.GetBytes(s));
        var jwt = $"{B64("{\"alg\":\"RS256\"}")}.{B64("{\"email\":\"me@gmail.com\",\"sub\":\"1\"}")}.sig";
        Assert.Equal("me@gmail.com", GoogleIdToken.TryReadEmail(jwt));
        Assert.Null(GoogleIdToken.TryReadEmail("garbage"));
        Assert.Null(GoogleIdToken.TryReadEmail(null));
    }

    // ---------- 刷新策略 ----------

    [Fact]
    public void NeedsRefresh_Uses_Two_Minute_Skew()
    {
        Assert.False(GmailTokenAcquirer.NeedsRefresh(Now.AddMinutes(10), Now));
        Assert.True(GmailTokenAcquirer.NeedsRefresh(Now.AddMinutes(1), Now));
        Assert.True(GmailTokenAcquirer.NeedsRefresh(Now.AddSeconds(-5), Now));
        Assert.True(GmailTokenAcquirer.NeedsRefresh(Now.AddMinutes(2), Now));
    }

    // ---------- 令牌存储 ----------

    [Fact]
    public async Task TokenStore_Roundtrip_And_Delete()
    {
        var store = new InMemoryCredentialStore();
        var tokens = new GmailTokenStore(store, "oauth:gmail:test");
        Assert.Null(await tokens.LoadAsync());

        var set = new GmailTokenSet("acc", "ref", Now.AddHours(1), "me@gmail.com", "scope");
        await tokens.SaveAsync(set);
        Assert.Equal(set, await new GmailTokenStore(store, "oauth:gmail:test").LoadAsync());

        await tokens.DeleteAsync();
        Assert.Null(await tokens.LoadAsync());
    }

    [Fact]
    public async Task TokenStore_Corrupt_Json_Is_Treated_As_Missing()
    {
        var store = new InMemoryCredentialStore();
        await store.SaveSecretAsync("k", "not json");
        Assert.Null(await new GmailTokenStore(store, "k").LoadAsync());
    }

    // ---------- 获取 / 刷新 ----------

    private sealed class FakeHandler : HttpMessageHandler
    {
        public List<(string Url, Dictionary<string, string> Form)> Requests { get; } = new();
        public Queue<(HttpStatusCode Status, string Body)> Responses { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var form = new Dictionary<string, string>();
            if (request.Content is not null)
            {
                form = LoopbackAuthorizationListener.ParseQuery("?" + await request.Content.ReadAsStringAsync(cancellationToken));
            }

            Requests.Add((request.RequestUri!.AbsoluteUri, form));
            var (status, body) = Responses.Count > 0 ? Responses.Dequeue() : (HttpStatusCode.OK, "{}");
            return new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        }
    }

    private static async Task<(GmailTokenAcquirer Acquirer, FakeHandler Handler, GmailTokenStore Tokens, Func<DateTimeOffset> Clock, Action<TimeSpan> Advance)>
        SetupAsync(GmailTokenSet? seeded, string? secret = null)
    {
        var config = Config(secret);
        var store = new InMemoryCredentialStore();
        var tokens = new GmailTokenStore(store, config.EffectiveVaultKey);
        if (seeded is not null)
        {
            await tokens.SaveAsync(seeded);
        }

        var handler = new FakeHandler();
        var now = Now;
        var acquirer = new GmailTokenAcquirer(config, store, new HttpClient(handler), new NoBrowser(), () => now);
        return (acquirer, handler, tokens, () => now, d => now += d);
    }

    private sealed class NoBrowser : IBrowserLauncher
    {
        public Task LaunchAsync(Uri uri, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("不应拉起浏览器。");
    }

    [Fact]
    public async Task Acquire_Returns_Cached_Token_Without_Network_When_Valid()
    {
        var (acquirer, handler, _, _, _) = await SetupAsync(new GmailTokenSet("cached", "ref", Now.AddMinutes(30), "me@gmail.com", null));
        var result = await acquirer.AcquireAsync(allowInteractive: false);
        Assert.Equal("cached", result.AccessToken);
        Assert.Equal("me@gmail.com", result.AccountUsername);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Acquire_Refreshes_Near_Expiry_And_Keeps_Existing_Refresh_Token()
    {
        var (acquirer, handler, tokens, _, _) = await SetupAsync(
            new GmailTokenSet("old", "ref-1", Now.AddSeconds(30), "me@gmail.com", null), secret: "shh");
        handler.Responses.Enqueue((HttpStatusCode.OK, """{"access_token":"new","expires_in":3600,"scope":"s"}"""));

        var result = await acquirer.AcquireAsync(allowInteractive: false);

        Assert.Equal("new", result.AccessToken);
        Assert.Equal(Now.AddSeconds(3600), result.ExpiresOn);
        var req = Assert.Single(handler.Requests);
        Assert.Equal("https://oauth2.googleapis.com/token", req.Url);
        Assert.Equal("refresh_token", req.Form["grant_type"]);
        Assert.Equal("ref-1", req.Form["refresh_token"]);
        Assert.Equal("shh", req.Form["client_secret"]);

        var saved = await tokens.LoadAsync();
        Assert.Equal("new", saved!.AccessToken);
        Assert.Equal("ref-1", saved.RefreshToken);   // 刷新响应没有 refresh_token：必须保留旧的

        // 紧接着再取：已是新令牌，不再请求网络
        await acquirer.AcquireAsync(allowInteractive: false);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task Acquire_Refreshes_Again_After_Clock_Advances_Into_Skew_Window()
    {
        var (acquirer, handler, _, _, advance) = await SetupAsync(new GmailTokenSet("t1", "ref", Now.AddMinutes(10), "me@gmail.com", null));
        handler.Responses.Enqueue((HttpStatusCode.OK, """{"access_token":"t2","expires_in":3600}"""));

        Assert.Equal("t1", (await acquirer.GetAccessTokenAsync()).AccessToken);
        advance(TimeSpan.FromMinutes(9)); // 剩 1 分钟 < 2 分钟 skew
        Assert.Equal("t2", (await acquirer.GetAccessTokenAsync()).AccessToken);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task Acquire_Rotated_Refresh_Token_Is_Adopted()
    {
        var (acquirer, handler, tokens, _, _) = await SetupAsync(new GmailTokenSet("old", "ref-1", Now.AddSeconds(5), "me@gmail.com", null));
        handler.Responses.Enqueue((HttpStatusCode.OK, """{"access_token":"new","refresh_token":"ref-2","expires_in":3600}"""));
        await acquirer.AcquireAsync(allowInteractive: false);
        Assert.Equal("ref-2", (await tokens.LoadAsync())!.RefreshToken);
    }

    [Fact]
    public async Task Acquire_InvalidGrant_Clears_Tokens_And_Requires_Login_When_Not_Interactive()
    {
        var (acquirer, handler, tokens, _, _) = await SetupAsync(new GmailTokenSet("old", "dead", Now.AddSeconds(5), "me@gmail.com", null));
        handler.Responses.Enqueue((HttpStatusCode.BadRequest, """{"error":"invalid_grant","error_description":"Token has been expired or revoked."}"""));

        await Assert.ThrowsAsync<GmailAuthRequiredException>(() => acquirer.AcquireAsync(allowInteractive: false));
        Assert.Null(await tokens.LoadAsync());
    }

    [Fact]
    public async Task Acquire_Without_Login_Throws_When_Not_Interactive()
    {
        var (acquirer, handler, _, _, _) = await SetupAsync(null);
        await Assert.ThrowsAsync<GmailAuthRequiredException>(() => acquirer.GetAccessTokenAsync());
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Acquire_Transient_Network_Error_Keeps_Stored_Tokens()
    {
        var (acquirer, handler, tokens, _, _) = await SetupAsync(new GmailTokenSet("old", "ref", Now.AddSeconds(5), "me@gmail.com", null));
        handler.Responses.Enqueue((HttpStatusCode.ServiceUnavailable, "oops"));
        await Assert.ThrowsAsync<HttpRequestException>(() => acquirer.AcquireAsync(allowInteractive: false));
        Assert.NotNull(await tokens.LoadAsync());
    }

    [Fact]
    public async Task SignOut_Revokes_And_Deletes()
    {
        var (acquirer, handler, tokens, _, _) = await SetupAsync(new GmailTokenSet("a", "ref-x", Now.AddHours(1), "me@gmail.com", null));
        await acquirer.SignOutAsync();
        var req = Assert.Single(handler.Requests);
        Assert.Equal("https://oauth2.googleapis.com/revoke", req.Url);
        Assert.Equal("ref-x", req.Form["token"]);
        Assert.Null(await tokens.LoadAsync());
    }

    [Fact]
    public void Acquirer_Requires_ClientId()
    {
        var config = Config() with { ClientId = " " };
        Assert.Throws<ArgumentException>(() => new GmailTokenAcquirer(config, new InMemoryCredentialStore()));
    }

    // ---------- 交互登录（真实回环监听 + 假浏览器） ----------

    private sealed class LoopbackBrowser : IBrowserLauncher
    {
        public Uri? Url { get; private set; }
        public string ReturnState { get; set; } = "";
        public bool UseWrongStateFirst { get; set; }
        public string? ErrorResult { get; set; }

        public async Task LaunchAsync(Uri uri, CancellationToken cancellationToken = default)
        {
            Url = uri;
            var q = LoopbackAuthorizationListener.ParseQuery(uri.PathAndQuery);
            var redirect = q["redirect_uri"];
            using var http = new HttpClient();
            // 浏览器常见的无关请求不应影响流程
            using var fav = await http.GetAsync(redirect + "favicon.ico", cancellationToken);
            Assert.Equal(HttpStatusCode.NotFound, fav.StatusCode);

            if (UseWrongStateFirst)
            {
                using var bad = await http.GetAsync($"{redirect}?code=evil&state=wrong", cancellationToken);
                Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
            }

            var tail = ErrorResult is null
                ? $"?code=auth-code&state={Uri.EscapeDataString(q["state"])}"
                : $"?error={ErrorResult}&state={Uri.EscapeDataString(q["state"])}";
            using var ok = await http.GetAsync(redirect + tail, cancellationToken);
            Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
            Assert.Contains("charset=utf-8", ok.Content.Headers.ContentType!.ToString());
        }
    }

    [Fact]
    public async Task Interactive_Login_Exchanges_Code_With_Matching_Pkce_Verifier_And_Saves_Tokens()
    {
        var store = new InMemoryCredentialStore();
        var config = Config(secret: "desktop-secret");
        var handler = new FakeHandler();
        var idToken = $"{Pkce.Base64Url("{}"u8.ToArray())}.{Pkce.Base64Url(Encoding.UTF8.GetBytes("{\"email\":\"me@gmail.com\"}"))}.s";
        handler.Responses.Enqueue((HttpStatusCode.OK, JsonSerializer.Serialize(new
        {
            access_token = "A1",
            refresh_token = "R1",
            expires_in = 3600,
            id_token = idToken,
            scope = "https://mail.google.com/ openid email"
        })));
        var browser = new LoopbackBrowser { UseWrongStateFirst = true };
        using var acquirer = new GmailTokenAcquirer(config, store, new HttpClient(handler), browser, () => Now);

        var result = await acquirer.AcquireAsync(allowInteractive: true);

        Assert.Equal("A1", result.AccessToken);
        Assert.Equal("me@gmail.com", result.AccountUsername);
        var urlQuery = LoopbackAuthorizationListener.ParseQuery(browser.Url!.PathAndQuery);
        Assert.StartsWith("http://127.0.0.1:", urlQuery["redirect_uri"]);
        Assert.Equal("me@gmail.com", urlQuery["login_hint"]);

        var req = Assert.Single(handler.Requests);
        Assert.Equal("authorization_code", req.Form["grant_type"]);
        Assert.Equal("auth-code", req.Form["code"]);
        Assert.Equal(urlQuery["redirect_uri"], req.Form["redirect_uri"]);
        Assert.Equal("desktop-secret", req.Form["client_secret"]);
        Assert.Equal(urlQuery["code_challenge"], Pkce.CreateChallenge(req.Form["code_verifier"]));

        var saved = await new GmailTokenStore(store, config.EffectiveVaultKey).LoadAsync();
        Assert.Equal("R1", saved!.RefreshToken);
        Assert.Equal("me@gmail.com", saved.Email);
    }

    [Fact]
    public async Task Interactive_Login_Denied_By_User_Throws_AccessDenied()
    {
        var browser = new LoopbackBrowser { ErrorResult = "access_denied" };
        using var acquirer = new GmailTokenAcquirer(Config(), new InMemoryCredentialStore(), new HttpClient(new FakeHandler()), browser, () => Now);
        var ex = await Assert.ThrowsAsync<GoogleOAuthException>(() => acquirer.AcquireAsync(true));
        Assert.Equal("access_denied", ex.Error);
    }

    [Fact]
    public async Task Interactive_Login_Rejects_Different_Google_Account()
    {
        var handler = new FakeHandler();
        var idToken = $"{Pkce.Base64Url("{}"u8.ToArray())}.{Pkce.Base64Url(Encoding.UTF8.GetBytes("{\"email\":\"other@gmail.com\"}"))}.s";
        handler.Responses.Enqueue((HttpStatusCode.OK, JsonSerializer.Serialize(new { access_token = "A", refresh_token = "R", expires_in = 60, id_token = idToken })));
        var store = new InMemoryCredentialStore();
        var config = Config();
        using var acquirer = new GmailTokenAcquirer(config, store, new HttpClient(handler), new LoopbackBrowser(), () => Now);

        await Assert.ThrowsAsync<InvalidOperationException>(() => acquirer.AcquireAsync(true));
        Assert.Null(await new GmailTokenStore(store, config.EffectiveVaultKey).LoadAsync());
    }

    [Fact]
    public async Task Listener_Times_Out_When_Browser_Never_Returns()
    {
        using var listener = LoopbackAuthorizationListener.Start();
        await Assert.ThrowsAsync<TimeoutException>(() => listener.WaitForResponseAsync("s", TimeSpan.FromMilliseconds(200)));
    }
}
