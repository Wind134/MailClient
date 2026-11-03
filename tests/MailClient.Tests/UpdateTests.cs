using System.Net;
using System.Security.Cryptography;
using System.Text;
using MailClient.Core;
using MailClient.Core.Notifications;
using MailClient.Core.Updates;

namespace MailClient.Tests;

/// <summary>伪造的 HttpMessageHandler：不碰真实网络。</summary>
internal sealed class FakeHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, HttpResponseMessage> _respond;

    public FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) => _respond = respond;

    public List<HttpRequestMessage> Requests { get; } = new();

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Add(request);
        return Task.FromResult(_respond(request));
    }

    public static HttpResponseMessage Json(string json, HttpStatusCode code = HttpStatusCode.OK)
        => new(code) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    public static HttpResponseMessage Bytes(byte[] bytes, HttpStatusCode code = HttpStatusCode.OK)
        => new(code) { Content = new ByteArrayContent(bytes) };
}

public sealed class UpdateVersionTests
{
    [Theory]
    [InlineData("v1.0.16", 1, 0, 16, 0)]
    [InlineData("1.0.16", 1, 0, 16, 0)]
    [InlineData("V2.10.3.4", 2, 10, 3, 4)]
    [InlineData("  v1.0.15.0 ", 1, 0, 15, 0)]
    public void Tags_parse_to_four_parts(string tag, int a, int b, int c, int d)
    {
        Assert.True(UpdateVersion.TryParseTag(tag, out var v));
        Assert.Equal(new Version(a, b, c, d), v);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("latest")]
    [InlineData("v1.0")]
    [InlineData("v1.0.16-beta.1")]
    [InlineData("v1.0.16+build5")]
    [InlineData("release-1.0.16")]
    [InlineData("v1.0.x")]
    [InlineData("v1.0.16.1.2")]
    public void Unrecognised_tags_are_rejected(string? tag)
    {
        Assert.False(UpdateVersion.TryParseTag(tag, out _));
    }

    [Theory]
    [InlineData("v1.0.16", "1.0.15.0", true)]
    [InlineData("v1.0.15", "1.0.15.0", false)]
    [InlineData("v1.0.15.1", "1.0.15.0", true)]
    [InlineData("v1.0.14", "1.0.15.0", false)]
    [InlineData("v1.1.0", "1.0.99.0", true)]
    [InlineData("v2.0.0", "1.99.99.99", true)]
    [InlineData("v1.0.9", "1.0.10.0", false)] // 数字比较，不是字符串比较
    public void Newer_means_strictly_greater(string tag, string current, bool expected)
    {
        Assert.True(UpdateVersion.TryParseTag(tag, out var candidate));
        Assert.Equal(expected, UpdateVersion.IsNewer(candidate, Version.Parse(current)));
    }

    [Fact]
    public void Three_part_assembly_versions_are_normalised()
    {
        Assert.True(UpdateVersion.TryParseTag("v1.0.15", out var tag));
        Assert.False(UpdateVersion.IsNewer(tag, new Version(1, 0, 15)));
        Assert.Equal("1.0.15.0", UpdateVersion.Display(new Version(1, 0, 15)));
    }
}

public sealed class ReleaseParserTests
{
    internal const string SampleJson = """
    {
      "url": "https://api.github.com/repos/o/r/releases/1",
      "html_url": "https://github.com/o/r/releases/tag/v1.0.16",
      "tag_name": "v1.0.16",
      "name": "邮件 1.0.16",
      "draft": false,
      "prerelease": false,
      "published_at": "2026-11-03T08:00:00Z",
      "body": "## 新功能\n- **检查更新**\n- 修复若干问题",
      "assets": [
        { "name": "MailClient-Setup-1.0.16.0.exe", "size": 150701568, "browser_download_url": "https://github.com/o/r/releases/download/v1.0.16/MailClient-Setup-1.0.16.0.exe" },
        { "name": "MailClient-1.0.16.0-portable.zip", "size": 149569669, "browser_download_url": "https://github.com/o/r/releases/download/v1.0.16/MailClient-1.0.16.0-portable.zip" },
        { "name": "SHA256SUMS.txt", "size": 200, "browser_download_url": "https://github.com/o/r/releases/download/v1.0.16/SHA256SUMS.txt" }
      ]
    }
    """;

    [Fact]
    public void Parses_a_github_release()
    {
        var r = ReleaseParser.TryParse(SampleJson, out var error);
        Assert.Null(error);
        Assert.NotNull(r);
        Assert.Equal("v1.0.16", r!.TagName);
        Assert.Equal(new Version(1, 0, 16, 0), r.Version);
        Assert.Equal("邮件 1.0.16", r.Title);
        Assert.Equal("https://github.com/o/r/releases/tag/v1.0.16", r.HtmlUrl);
        Assert.Contains("检查更新", r.Body);
        Assert.Equal(3, r.Assets.Count);
        Assert.Equal(150701568, r.Assets[0].Size);
        Assert.Equal(new DateTimeOffset(2026, 11, 3, 8, 0, 0, TimeSpan.Zero), r.PublishedAt);
    }

    [Fact]
    public void Missing_optional_fields_are_tolerated()
    {
        var r = ReleaseParser.TryParse("""{ "tag_name": "v1.2.3" }""", out _);
        Assert.NotNull(r);
        Assert.Equal("v1.2.3", r!.Title);
        Assert.Equal(string.Empty, r.Body);
        Assert.Empty(r.Assets);
        Assert.Equal(UpdateConfig.ReleasesPageUrl(), r.HtmlUrl);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("""{ "tag_name": "nightly" }""")]
    [InlineData("""{ "tag_name": "v1.0.16", "draft": true }""")]
    [InlineData("""{ "tag_name": "v1.0.16", "prerelease": true }""")]
    public void Bad_or_unusable_json_gives_null_and_a_reason(string json)
    {
        var r = ReleaseParser.TryParse(json, out var error);
        Assert.Null(r);
        Assert.False(string.IsNullOrWhiteSpace(error));
    }

    [Fact]
    public void Assets_without_name_or_url_are_dropped()
    {
        var r = ReleaseParser.TryParse("""{ "tag_name": "v1.0.1", "assets": [ {"name":"a"}, {"browser_download_url":"https://x"}, 5, {"name":"ok.exe","browser_download_url":"https://github.com/ok.exe"} ] }""", out _);
        Assert.Single(r!.Assets);
        Assert.Equal("ok.exe", r.Assets[0].Name);
    }
}

public sealed class UpdateAssetTests
{
    private static ReleaseInfo Release(params string[] names)
        => new("v1.0.16", new Version(1, 0, 16, 0), "t", "", "https://github.com/o/r/releases/tag/v1.0.16", null,
            names.Select(n => new ReleaseAsset(n, "https://github.com/o/r/releases/download/v1.0.16/" + n, 10)).ToList());

    [Fact]
    public void Picks_the_setup_exe_not_the_zip()
    {
        var r = Release("MailClient-1.0.16.0-portable.zip", "SHA256SUMS.txt", "MailClient-Setup-1.0.16.0.exe", "other.exe");
        Assert.Equal("MailClient-Setup-1.0.16.0.exe", UpdateAssets.SelectInstaller(r)!.Name);
        Assert.Equal("SHA256SUMS.txt", UpdateAssets.SelectChecksums(r)!.Name);
    }

    [Fact]
    public void Matching_is_case_insensitive_and_prefers_the_release_version()
    {
        var r = Release("MailClient-Setup-1.0.15.0.exe", "mailclient-setup-1.0.16.0.EXE");
        Assert.Equal("mailclient-setup-1.0.16.0.EXE", UpdateAssets.SelectInstaller(r)!.Name);
    }

    [Fact]
    public void No_installer_gives_null()
    {
        Assert.Null(UpdateAssets.SelectInstaller(Release("MailClient-1.0.16.0-portable.zip", "MailClient_1.0.16.0_x64.msix")));
        Assert.Null(UpdateAssets.SelectChecksums(Release("MailClient-Setup-1.0.16.0.exe")));
    }

    [Theory]
    [InlineData("MailClient-Setup-1.0.16.0.exe", true)]
    [InlineData("MailClient-Setup-.exe", false)]
    [InlineData("MailClient-Setup-1.0.16.0.exe.sig", false)]
    [InlineData("Other-Setup-1.0.exe", false)]
    [InlineData("MailClient-Setup-..\\evil.exe", false)]
    [InlineData("MailClient-Setup-x/evil.exe", false)]
    public void Installer_name_pattern(string name, bool expected)
        => Assert.Equal(expected, UpdateAssets.IsInstallerName(name));

    [Theory]
    [InlineData("https://github.com/o/r/releases/download/v1/x.exe", false, true)]
    [InlineData("https://objects.githubusercontent.com/x", false, true)]
    [InlineData("https://release-assets.githubusercontent.com/x", false, true)]
    [InlineData("http://github.com/x", false, false)]
    [InlineData("https://evil.example.com/x.exe", false, false)]
    [InlineData("https://github.com.evil.example.com/x.exe", false, false)]
    [InlineData("http://127.0.0.1:8123/x.exe", false, false)]
    [InlineData("http://127.0.0.1:8123/x.exe", true, true)]
    [InlineData("http://example.com/x.exe", true, false)]
    [InlineData("not a url", true, false)]
    public void Download_url_trust(string url, bool allowLoopback, bool expected)
        => Assert.Equal(expected, UpdateConfig.IsTrustedDownloadUrl(url, allowLoopback));
}

public sealed class UpdateSkipPolicyTests
{
    private static ReleaseInfo Release(string tag)
    {
        UpdateVersion.TryParseTag(tag, out var v);
        return new ReleaseInfo(tag, v, tag, "", "https://github.com/o/r", null, Array.Empty<ReleaseAsset>());
    }

    private static readonly Version Current = new(1, 0, 15, 0);

    [Fact]
    public void Newer_release_is_announced()
        => Assert.True(UpdateSkipPolicy.ShouldAnnounce(Release("v1.0.16"), Current, null, manual: false));

    [Fact]
    public void Same_or_older_release_is_not_announced()
    {
        Assert.False(UpdateSkipPolicy.ShouldAnnounce(Release("v1.0.15"), Current, null, manual: false));
        Assert.False(UpdateSkipPolicy.ShouldAnnounce(Release("v1.0.14"), Current, null, manual: true));
    }

    [Fact]
    public void Skipped_version_is_silent_for_automatic_checks_but_shown_for_manual()
    {
        Assert.False(UpdateSkipPolicy.ShouldAnnounce(Release("v1.0.16"), Current, "v1.0.16", manual: false));
        Assert.True(UpdateSkipPolicy.ShouldAnnounce(Release("v1.0.16"), Current, "v1.0.16", manual: true));
    }

    [Fact]
    public void A_later_release_is_announced_even_if_an_older_one_was_skipped()
        => Assert.True(UpdateSkipPolicy.ShouldAnnounce(Release("v1.0.17"), Current, "v1.0.16", manual: false));

    [Theory]
    [InlineData("1.0.16")]
    [InlineData("v1.0.16.0")]
    public void Skip_matches_regardless_of_tag_spelling(string skipped)
        => Assert.False(UpdateSkipPolicy.ShouldAnnounce(Release("v1.0.16"), Current, skipped, manual: false));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("garbage")]
    public void Garbage_skip_value_skips_nothing(string? skipped)
        => Assert.True(UpdateSkipPolicy.ShouldAnnounce(Release("v1.0.16"), Current, skipped, manual: false));

    [Fact]
    public void Settings_defaults_and_persistence()
    {
        Assert.True(new AppSettings().AutoCheckUpdates);
        Assert.Null(new AppSettings().SkippedUpdateVersion);

        var path = Path.Combine(Path.GetTempPath(), "mc-update-test-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var store = new AppSettingsStore(path);
            store.Update(s => { s.AutoCheckUpdates = false; s.SkippedUpdateVersion = "v1.0.16"; });
            var reloaded = new AppSettingsStore(path);
            Assert.False(reloaded.GetAutoCheckUpdates());
            Assert.Equal("v1.0.16", reloaded.GetSkippedUpdateVersion());
            Assert.False(reloaded.Snapshot().AutoCheckUpdates); // Snapshot（Clone）也带着这两项
        }
        finally
        {
            try { File.Delete(path); } catch (IOException) { }
        }
    }

    [Fact]
    public void Update_toast_action_round_trips_without_a_message_id()
    {
        var parsed = ToastAction.TryParse(new ToastAction(ToastActionKind.Update).ToArguments());
        Assert.Equal(ToastActionKind.Update, parsed!.Kind);
        Assert.False(parsed.RunsInBackground);
    }
}

public sealed class Sha256SumsTests
{
    private const string Hash = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    [Fact]
    public void Parses_sha256sum_output_in_both_modes_and_with_crlf_and_bom()
    {
        var text = "\uFEFF" + Hash + "  MailClient-Setup-1.0.16.0.exe\r\n"
                 + Hash.ToUpperInvariant().Replace("0123", "FFFF") + " *MailClient-1.0.16.0-portable.zip\r\n"
                 + "\r\n# comment\r\n";
        var map = Sha256Sums.Parse(text);
        Assert.Equal(2, map.Count);
        Assert.Equal(Hash, map["MailClient-Setup-1.0.16.0.exe"]);
        Assert.StartsWith("ffff", map["mailclient-1.0.16.0-portable.zip"]); // 文件名忽略大小写、哈希转小写
    }

    [Fact]
    public void Garbage_lines_are_ignored()
    {
        Assert.Empty(Sha256Sums.Parse("hello\nabc  file.exe\n" + Hash.Substring(1) + "  short.exe"));
        Assert.Empty(Sha256Sums.Parse(null));
    }

    [Fact]
    public void Matches_ignores_case_and_whitespace_but_not_blank()
    {
        Assert.True(Sha256Sums.Matches(Hash, " " + Hash.ToUpperInvariant() + "\n"));
        Assert.False(Sha256Sums.Matches(Hash, Hash.Replace('0', '1')));
        Assert.False(Sha256Sums.Matches("", ""));
        Assert.False(Sha256Sums.Matches(null, Hash));
    }

    [Fact]
    public async Task Computes_a_file_hash()
    {
        var path = Path.Combine(Path.GetTempPath(), "mc-sha-" + Guid.NewGuid().ToString("N"));
        try
        {
            await File.WriteAllBytesAsync(path, Encoding.ASCII.GetBytes("abc"));
            Assert.Equal("ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad", await Sha256Sums.ComputeFileAsync(path));
        }
        finally
        {
            File.Delete(path);
        }
    }
}

public sealed class UpdateDownloaderTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "mc-dl-test-" + Guid.NewGuid().ToString("N"));
    private static readonly byte[] Payload = Enumerable.Range(0, 300_000).Select(i => (byte)(i * 7)).ToArray();
    private static readonly string PayloadHash = Convert.ToHexString(SHA256.HashData(Payload)).ToLowerInvariant();

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private static ReleaseInfo Release(bool withSums = true, string installerName = "MailClient-Setup-1.0.16.0.exe", string host = "https://github.com/o/r/releases/download/v1.0.16/")
    {
        var assets = new List<ReleaseAsset> { new(installerName, host + installerName, Payload.Length) };
        if (withSums)
        {
            assets.Add(new ReleaseAsset("SHA256SUMS.txt", host + "SHA256SUMS.txt", 100));
        }

        return new ReleaseInfo("v1.0.16", new Version(1, 0, 16, 0), "t", "", "https://github.com/o/r", null, assets);
    }

    private static FakeHandler Server(string sums)
        => new(req => req.RequestUri!.AbsolutePath.EndsWith("SHA256SUMS.txt", StringComparison.Ordinal)
            ? FakeHandler.Bytes(Encoding.UTF8.GetBytes(sums))
            : FakeHandler.Bytes(Payload));

    [Fact]
    public async Task Downloads_verifies_and_reports_progress()
    {
        var handler = Server(PayloadHash + "  MailClient-Setup-1.0.16.0.exe\n");
        var reports = new List<DownloadProgress>();
        var result = await new UpdateDownloader(new HttpClient(handler)).DownloadInstallerAsync(Release(), _dir, new SyncProgress<DownloadProgress>(reports.Add), CancellationToken.None);

        Assert.True(result.Verified);
        Assert.Equal(PayloadHash, result.Sha256);
        Assert.Equal(Payload, await File.ReadAllBytesAsync(result.Path));
        Assert.Equal("MailClient-Setup-1.0.16.0.exe", Path.GetFileName(result.Path));
        Assert.False(File.Exists(result.Path + ".part"));
        Assert.Equal(Payload.Length, reports[^1].Received);
        Assert.Equal(1.0, reports[^1].Fraction);
    }

    [Fact]
    public async Task Hash_mismatch_is_refused_and_the_file_is_deleted()
    {
        var wrong = new string('a', 64);
        var handler = Server(wrong + "  MailClient-Setup-1.0.16.0.exe\n");
        var ex = await Assert.ThrowsAsync<UpdateException>(() =>
            new UpdateDownloader(new HttpClient(handler)).DownloadInstallerAsync(Release(), _dir, null, CancellationToken.None));
        Assert.Equal(UpdateErrorKind.ChecksumMismatch, ex.Kind);
        Assert.Empty(Directory.GetFiles(_dir));
    }

    [Fact]
    public async Task Sums_file_without_an_entry_for_the_installer_is_refused_before_downloading()
    {
        var handler = Server(PayloadHash + "  something-else.exe\n");
        var ex = await Assert.ThrowsAsync<UpdateException>(() =>
            new UpdateDownloader(new HttpClient(handler)).DownloadInstallerAsync(Release(), _dir, null, CancellationToken.None));
        Assert.Equal(UpdateErrorKind.ChecksumMissingEntry, ex.Kind);
        Assert.DoesNotContain(handler.Requests, r => r.RequestUri!.AbsolutePath.EndsWith(".exe", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Release_without_a_sums_file_downloads_unverified()
    {
        var handler = Server(string.Empty);
        var result = await new UpdateDownloader(new HttpClient(handler)).DownloadInstallerAsync(Release(withSums: false), _dir, null, CancellationToken.None);
        Assert.False(result.Verified);
        Assert.Equal(PayloadHash, result.Sha256);
    }

    [Fact]
    public async Task Untrusted_hosts_are_refused_without_any_request()
    {
        var handler = Server(string.Empty);
        var ex = await Assert.ThrowsAsync<UpdateException>(() =>
            new UpdateDownloader(new HttpClient(handler)).DownloadInstallerAsync(Release(host: "https://evil.example.com/"), _dir, null, CancellationToken.None));
        Assert.Equal(UpdateErrorKind.UntrustedUrl, ex.Kind);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task No_installer_asset_is_reported()
    {
        var release = new ReleaseInfo("v1.0.16", new Version(1, 0, 16, 0), "t", "", "https://github.com/o/r", null,
            new[] { new ReleaseAsset("MailClient-1.0.16.0-portable.zip", "https://github.com/x.zip", 5) });
        var ex = await Assert.ThrowsAsync<UpdateException>(() =>
            new UpdateDownloader(new HttpClient(Server(string.Empty))).DownloadInstallerAsync(release, _dir, null, CancellationToken.None));
        Assert.Equal(UpdateErrorKind.NoInstallerAsset, ex.Kind);
    }

    [Fact]
    public async Task Truncated_download_is_refused()
    {
        var handler = new FakeHandler(req => req.RequestUri!.AbsolutePath.EndsWith("SHA256SUMS.txt", StringComparison.Ordinal)
            ? FakeHandler.Bytes(Encoding.UTF8.GetBytes(PayloadHash + "  MailClient-Setup-1.0.16.0.exe"))
            : FakeHandler.Bytes(Payload.Take(1000).ToArray()));
        var release = Release(); // 声明的大小是 Payload.Length，但服务器没给 Content-Length 以外的信息
        var ex = await Assert.ThrowsAsync<UpdateException>(() =>
            new UpdateDownloader(new HttpClient(handler)).DownloadInstallerAsync(release, _dir, null, CancellationToken.None));
        Assert.True(ex.Kind is UpdateErrorKind.SizeMismatch or UpdateErrorKind.ChecksumMismatch);
        Assert.Empty(Directory.GetFiles(_dir));
    }

    [Fact]
    public async Task Http_error_becomes_download_failed()
    {
        var handler = new FakeHandler(req => req.RequestUri!.AbsolutePath.EndsWith("SHA256SUMS.txt", StringComparison.Ordinal)
            ? FakeHandler.Bytes(Encoding.UTF8.GetBytes(PayloadHash + "  MailClient-Setup-1.0.16.0.exe"))
            : new HttpResponseMessage(HttpStatusCode.NotFound));
        var ex = await Assert.ThrowsAsync<UpdateException>(() =>
            new UpdateDownloader(new HttpClient(handler)).DownloadInstallerAsync(Release(), _dir, null, CancellationToken.None));
        Assert.Equal(UpdateErrorKind.DownloadFailed, ex.Kind);
    }

    [Fact]
    public async Task Cancellation_leaves_no_partial_file()
    {
        using var cts = new CancellationTokenSource();
        var handler = new FakeHandler(req => req.RequestUri!.AbsolutePath.EndsWith("SHA256SUMS.txt", StringComparison.Ordinal)
            ? FakeHandler.Bytes(Encoding.UTF8.GetBytes(PayloadHash + "  MailClient-Setup-1.0.16.0.exe"))
            : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new CancelAfterFirstRead(cts, Payload)) });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new UpdateDownloader(new HttpClient(handler)).DownloadInstallerAsync(Release(), _dir, null, cts.Token));
        Assert.Empty(Directory.GetFiles(_dir));
    }

    [Fact]
    public async Task Loopback_http_needs_the_debug_override()
    {
        var release = Release(host: "http://127.0.0.1:9/");
        await Assert.ThrowsAsync<UpdateException>(() =>
            new UpdateDownloader(new HttpClient(Server(string.Empty))).DownloadInstallerAsync(release, _dir, null, CancellationToken.None));
        var ok = await new UpdateDownloader(new HttpClient(Server(PayloadHash + "  MailClient-Setup-1.0.16.0.exe")), allowLoopbackHttp: true)
            .DownloadInstallerAsync(release, _dir, null, CancellationToken.None);
        Assert.True(ok.Verified);
    }

    private sealed class CancelAfterFirstRead : MemoryStream
    {
        private readonly CancellationTokenSource _cts;
        private bool _first = true;

        public CancelAfterFirstRead(CancellationTokenSource cts, byte[] data) : base(data) => _cts = cts;

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (!_first)
            {
                _cts.Cancel();
                cancellationToken.ThrowIfCancellationRequested();
            }

            _first = false;
            return base.ReadAsync(buffer.Slice(0, Math.Min(buffer.Length, 1000)), cancellationToken);
        }
    }
    private sealed class SyncProgress<T> : IProgress<T>
    {
        private readonly Action<T> _handler;
        public SyncProgress(Action<T> handler) => _handler = handler;
        public void Report(T value) => _handler(value);
    }
}

public sealed class UpdateCheckerTests
{
    private static readonly Version Current = new(1, 0, 15, 0);

    private static UpdateChecker Checker(FakeHandler handler, Func<string?>? feed = null, List<string>? log = null)
    {
        var http = UpdateChecker.CreateHttpClient(Current);
        // CreateHttpClient 建的是真实 handler；测试里用同样的头换上假 handler
        var fake = new HttpClient(handler);
        foreach (var h in http.DefaultRequestHeaders)
        {
            fake.DefaultRequestHeaders.TryAddWithoutValidation(h.Key, h.Value);
        }

        return new UpdateChecker(fake, feed ?? (() => null), log is null ? null : log.Add);
    }

    [Fact]
    public async Task Newer_release_is_reported_and_the_request_is_well_formed()
    {
        var handler = new FakeHandler(_ => FakeHandler.Json(ReleaseParserTests.SampleJson));
        var result = await Checker(handler).CheckAsync(Current);

        Assert.Equal(UpdateCheckStatus.UpdateAvailable, result.Status);
        Assert.Equal("v1.0.16", result.Release!.TagName);
        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Equal($"https://api.github.com/repos/{UpdateConfig.Repository}/releases/latest", request.RequestUri!.ToString());
        Assert.Contains("MailClient/", string.Join(' ', request.Headers.UserAgent.Select(p => p.ToString())) + string.Join(' ', request.Headers.GetValues("User-Agent")));
    }

    [Fact]
    public void Http_client_sends_a_user_agent_and_github_headers()
    {
        using var http = UpdateChecker.CreateHttpClient(Current);
        Assert.Contains("MailClient/1.0.15.0", http.DefaultRequestHeaders.UserAgent.ToString());
        Assert.Contains("application/vnd.github+json", http.DefaultRequestHeaders.Accept.ToString());
        Assert.True(http.DefaultRequestHeaders.Contains("X-GitHub-Api-Version"));
    }

    [Fact]
    public async Task Same_version_is_up_to_date()
    {
        var json = ReleaseParserTests.SampleJson.Replace("v1.0.16", "v1.0.15");
        var result = await Checker(new FakeHandler(_ => FakeHandler.Json(json))).CheckAsync(Current);
        Assert.Equal(UpdateCheckStatus.UpToDate, result.Status);
        Assert.Contains("已是最新版本", result.ToDisplayText());
    }

    [Fact]
    public async Task Repository_without_releases_is_a_quiet_404()
    {
        var log = new List<string>();
        var result = await Checker(new FakeHandler(_ => FakeHandler.Json("""{"message":"Not Found"}""", HttpStatusCode.NotFound)), log: log).CheckAsync(Current);
        Assert.Equal(UpdateCheckStatus.NoReleases, result.Status);
        Assert.False(result.IsFailure);
        Assert.Contains("已是最新版本", result.ToDisplayText());
    }

    [Fact]
    public async Task Rate_limit_403_and_429_are_reported_as_rate_limited()
    {
        var limited = new FakeHandler(_ =>
        {
            var r = FakeHandler.Json("""{"message":"API rate limit exceeded"}""", HttpStatusCode.Forbidden);
            r.Headers.Add("X-RateLimit-Remaining", "0");
            return r;
        });
        Assert.Equal(UpdateCheckStatus.RateLimited, (await Checker(limited).CheckAsync(Current)).Status);
        Assert.Equal(UpdateCheckStatus.RateLimited, (await Checker(new FakeHandler(_ => FakeHandler.Json("{}", HttpStatusCode.TooManyRequests))).CheckAsync(Current)).Status);

        var plain403 = await Checker(new FakeHandler(_ => FakeHandler.Json("{}", HttpStatusCode.Forbidden))).CheckAsync(Current);
        Assert.Equal(UpdateCheckStatus.Failed, plain403.Status);
        Assert.True(plain403.IsFailure);
        Assert.StartsWith("检查失败", plain403.ToDisplayText());
    }

    [Fact]
    public async Task Server_error_is_a_failure_not_an_exception()
    {
        var result = await Checker(new FakeHandler(_ => FakeHandler.Json("oops", HttpStatusCode.InternalServerError))).CheckAsync(Current);
        Assert.Equal(UpdateCheckStatus.Failed, result.Status);
    }

    [Fact]
    public async Task Offline_and_timeout_and_garbage_never_throw()
    {
        var offline = new FakeHandler(_ => throw new HttpRequestException("No such host is known"));
        var r1 = await Checker(offline).CheckAsync(Current);
        Assert.Equal(UpdateCheckStatus.Offline, r1.Status);
        Assert.Contains("检查失败", r1.ToDisplayText());

        var timeout = new FakeHandler(_ => throw new TaskCanceledException("timed out"));
        Assert.Equal(UpdateCheckStatus.Failed, (await Checker(timeout).CheckAsync(Current)).Status);

        var garbage = new FakeHandler(_ => FakeHandler.Json("<html>captive portal</html>"));
        Assert.Equal(UpdateCheckStatus.Failed, (await Checker(garbage).CheckAsync(Current)).Status);

        var weird = new FakeHandler(_ => throw new InvalidOperationException("boom"));
        Assert.Equal(UpdateCheckStatus.Failed, (await Checker(weird).CheckAsync(Current)).Status);
    }

    [Fact]
    public async Task Caller_cancellation_is_not_swallowed()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var handler = new FakeHandler(_ => throw new OperationCanceledException(cts.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Checker(handler).CheckAsync(Current, cts.Token));
    }

    [Fact]
    public async Task Unrecognised_tag_is_a_failure_not_an_update()
    {
        var json = ReleaseParserTests.SampleJson.Replace("v1.0.16", "nightly-20261103");
        var result = await Checker(new FakeHandler(_ => FakeHandler.Json(json))).CheckAsync(Current);
        Assert.Equal(UpdateCheckStatus.Failed, result.Status);
    }

    [Fact]
    public async Task Local_json_feed_override_skips_the_network()
    {
        var path = Path.Combine(Path.GetTempPath(), "mc-feed-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            await File.WriteAllTextAsync(path, ReleaseParserTests.SampleJson);
            var handler = new FakeHandler(_ => throw new InvalidOperationException("must not hit the network"));
            var result = await Checker(handler, () => path).CheckAsync(Current);
            Assert.Equal(UpdateCheckStatus.UpdateAvailable, result.Status);
            Assert.Empty(handler.Requests);

            var missing = await Checker(handler, () => path + ".missing").CheckAsync(Current);
            Assert.Equal(UpdateCheckStatus.Failed, missing.Status);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Http_feed_override_is_fetched_instead_of_github()
    {
        var handler = new FakeHandler(_ => FakeHandler.Json(ReleaseParserTests.SampleJson));
        var result = await Checker(handler, () => "http://127.0.0.1:8123/latest.json").CheckAsync(Current);
        Assert.Equal(UpdateCheckStatus.UpdateAvailable, result.Status);
        Assert.Equal("http://127.0.0.1:8123/latest.json", handler.Requests[0].RequestUri!.ToString());
    }

    [Fact]
    public void The_repository_is_configured_in_exactly_one_place()
    {
        Assert.Equal("Wind134/MailClient", UpdateConfig.Repository);
        Assert.Contains(UpdateConfig.Repository, UpdateConfig.LatestReleaseApiUrl());
        Assert.Contains(UpdateConfig.Repository, UpdateConfig.ReleasesPageUrl());
        Assert.Equal(TimeSpan.FromSeconds(30), UpdateConfig.InitialDelay);
        Assert.Equal(TimeSpan.FromHours(6), UpdateConfig.CheckInterval);
    }
}

public sealed class EditionDetectorTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "mc-edition-" + Guid.NewGuid().ToString("N"));

    public EditionDetectorTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public void Packaged_wins()
    {
        File.WriteAllText(Path.Combine(_dir, EditionDetector.ManifestFileName), "x");
        Assert.Equal(UpdateEdition.Packaged, EditionDetector.Detect(true, _dir));
    }

    [Fact]
    public void Install_manifest_plus_setup_exe_means_exe_edition()
    {
        Assert.Equal(UpdateEdition.Portable, EditionDetector.Detect(false, _dir));
        File.WriteAllText(Path.Combine(_dir, EditionDetector.ManifestFileName), "x");
        Assert.Equal(UpdateEdition.Portable, EditionDetector.Detect(false, _dir)); // 只有清单还不够
        File.WriteAllText(Path.Combine(_dir, EditionDetector.SetupExeName), "x");
        Assert.Equal(UpdateEdition.Installed, EditionDetector.Detect(false, _dir));
        Assert.True(EditionDetector.CanSelfUpdate(UpdateEdition.Installed));
        Assert.False(EditionDetector.CanSelfUpdate(UpdateEdition.Packaged));
        Assert.False(EditionDetector.CanSelfUpdate(UpdateEdition.Portable));
    }

    [Fact]
    public void Missing_directory_is_portable()
        => Assert.Equal(UpdateEdition.Portable, EditionDetector.Detect(false, Path.Combine(_dir, "nope")));

    [Fact]
    public void File_names_match_what_the_installer_writes()
    {
        Assert.Equal(MailClient.Setup.Core.SetupConstants.ManifestFileName, EditionDetector.ManifestFileName);
        Assert.Equal(MailClient.Setup.Core.SetupConstants.SetupExeName, EditionDetector.SetupExeName);
    }
}

public sealed class MarkdownLiteTests
{
    [Fact]
    public void Parses_headings_bullets_paragraphs_and_code()
    {
        var blocks = MarkdownLite.Parse("# 邮件 1.0.16\n\n## 新功能\n- **检查更新**：[详情](https://x.y)\n* 修复 `bug`\n1. 第一项\n\n普通段落\n接着一行\n\n```\ncode line\n```\n---\n");
        Assert.Collection(blocks,
            b => { Assert.Equal(MarkdownBlockKind.Heading, b.Kind); Assert.Equal(1, b.Level); Assert.Equal("邮件 1.0.16", b.Text); },
            b => { Assert.Equal(MarkdownBlockKind.Heading, b.Kind); Assert.Equal(2, b.Level); },
            b => { Assert.Equal(MarkdownBlockKind.Bullet, b.Kind); Assert.Equal("检查更新：详情", b.Text); },
            b => Assert.Equal("修复 bug", b.Text),
            b => Assert.Equal("第一项", b.Text),
            b => { Assert.Equal(MarkdownBlockKind.Paragraph, b.Kind); Assert.Equal("普通段落 接着一行", b.Text); },
            b => { Assert.Equal(MarkdownBlockKind.Code, b.Kind); Assert.Equal("code line", b.Text); });
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   \n\n  ")]
    public void Empty_body_gives_no_blocks(string? body) => Assert.Empty(MarkdownLite.Parse(body));

    [Fact]
    public void Inline_markup_and_html_are_stripped()
    {
        Assert.Equal("粗体 斜体 代码 链接", MarkdownLite.Inline("**粗体** *斜体* `代码` [链接](http://a)"));
        Assert.Equal("图 说明", MarkdownLite.Inline("![img](http://a.png)图 <b>说明</b>"));
    }

    [Fact]
    public void Huge_bodies_are_truncated()
    {
        var blocks = MarkdownLite.Parse(string.Join("\n", Enumerable.Repeat("- " + new string('x', 100), 1000)));
        Assert.True(blocks.Count < 1000);
    }
}