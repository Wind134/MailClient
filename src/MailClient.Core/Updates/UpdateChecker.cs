using System.Net;
using System.Net.Http.Headers;

namespace MailClient.Core.Updates;

public enum UpdateCheckStatus
{
    /// <summary>已是最新版本。</summary>
    UpToDate,

    /// <summary>发现比当前新的版本。</summary>
    UpdateAvailable,

    /// <summary>仓库还没有任何发布（GitHub 返回 404）。</summary>
    NoReleases,

    /// <summary>被 GitHub 限流（403 / 429）。</summary>
    RateLimited,

    /// <summary>连不上网络。</summary>
    Offline,

    /// <summary>其他失败（超时、响应异常、版本号无法识别……）。</summary>
    Failed
}

public sealed record UpdateCheckResult(UpdateCheckStatus Status, Version Current, ReleaseInfo? Release = null, string? Message = null)
{
    public bool IsFailure => Status is UpdateCheckStatus.RateLimited or UpdateCheckStatus.Offline or UpdateCheckStatus.Failed;

    /// <summary>给用户看的一句话（设置页里显示）。</summary>
    public string ToDisplayText() => Status switch
    {
        UpdateCheckStatus.UpToDate => $"已是最新版本（{UpdateVersion.Display(Current)}）",
        UpdateCheckStatus.UpdateAvailable => $"发现新版本 {UpdateVersion.Display(Release!.Version)}",
        UpdateCheckStatus.NoReleases => $"已是最新版本（{UpdateVersion.Display(Current)}；仓库还没有发布过版本）",
        UpdateCheckStatus.RateLimited => "检查失败：访问 GitHub 过于频繁，请稍后再试",
        UpdateCheckStatus.Offline => "检查失败：无法连接到 GitHub，请检查网络",
        _ => "检查失败" + (string.IsNullOrWhiteSpace(Message) ? string.Empty : "：" + Message)
    };
}

/// <summary>
/// 向 GitHub Releases 查询最新发布并与当前版本比较。所有网络 / 解析问题都转成 <see cref="UpdateCheckResult"/>，不抛异常
/// （只有调用方主动取消才会抛 <see cref="OperationCanceledException"/>）。
/// </summary>
public sealed class UpdateChecker
{
    private readonly HttpClient _http;
    private readonly Func<string?> _feedOverride;
    private readonly Action<string>? _log;
    private readonly string _repository;

    /// <param name="feedOverride">调试源（本地 JSON 文件路径或 http(s) 地址）；返回 null 表示用 GitHub API。</param>
    public UpdateChecker(HttpClient http, Func<string?>? feedOverride = null, Action<string>? log = null, string? repository = null)
    {
        _http = http;
        _feedOverride = feedOverride ?? (() => UpdateConfig.FeedOverride);
        _log = log;
        _repository = string.IsNullOrWhiteSpace(repository) ? UpdateConfig.Repository : repository!;
    }

    public static HttpClient CreateHttpClient(Version current)
    {
        var http = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = true, MaxAutomaticRedirections = 5 })
        {
            Timeout = Timeout.InfiniteTimeSpan // 各请求自己用 CancellationToken 控制超时（下载安装包可能很久）
        };
        http.DefaultRequestHeaders.UserAgent.ParseAdd(UpdateConfig.UserAgent(current));
        http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        http.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
        return http;
    }

    public async Task<UpdateCheckResult> CheckAsync(Version current, CancellationToken ct = default)
    {
        try
        {
            var feed = _feedOverride();
            string json;
            if (feed is not null && !feed.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                && !feed.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                _log?.Invoke("update: reading local feed " + feed);
                if (!File.Exists(feed))
                {
                    return Fail(UpdateCheckStatus.Failed, current, "找不到更新源文件");
                }

                json = await File.ReadAllTextAsync(feed, ct).ConfigureAwait(false);
            }
            else
            {
                var url = feed ?? UpdateConfig.LatestReleaseApiUrl(_repository);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(UpdateConfig.RequestTimeout);
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseContentRead, timeout.Token).ConfigureAwait(false);
                var code = response.StatusCode;
                if (code == HttpStatusCode.NotFound)
                {
                    _log?.Invoke("update: 404 (no releases yet)");
                    return new UpdateCheckResult(UpdateCheckStatus.NoReleases, current);
                }

                if (code == HttpStatusCode.TooManyRequests || (code == HttpStatusCode.Forbidden && IsRateLimited(response)))
                {
                    return Fail(UpdateCheckStatus.RateLimited, current, "GitHub rate limit (" + (int)code + ")");
                }

                if (!response.IsSuccessStatusCode)
                {
                    return Fail(UpdateCheckStatus.Failed, current, "GitHub 返回 " + (int)code);
                }

                json = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
            }

            var release = ReleaseParser.TryParse(json, out var error);
            if (release is null)
            {
                return Fail(UpdateCheckStatus.Failed, current, error ?? "无法解析响应");
            }

            var newer = UpdateVersion.IsNewer(release.Version, current);
            _log?.Invoke($"update: latest={release.TagName} current={UpdateVersion.Display(current)} newer={newer}");
            return new UpdateCheckResult(newer ? UpdateCheckStatus.UpdateAvailable : UpdateCheckStatus.UpToDate, current, release);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return Fail(UpdateCheckStatus.Failed, current, "连接超时");
        }
        catch (HttpRequestException ex)
        {
            return Fail(UpdateCheckStatus.Offline, current, ex.Message);
        }
        catch (Exception ex)
        {
            return Fail(UpdateCheckStatus.Failed, current, ex.GetType().Name + ": " + ex.Message);
        }
    }

    private UpdateCheckResult Fail(UpdateCheckStatus status, Version current, string message)
    {
        _log?.Invoke($"update: {status} - {message}");
        return new UpdateCheckResult(status, current, null, message);
    }

    private static bool IsRateLimited(HttpResponseMessage response)
    {
        if (response.Headers.TryGetValues("X-RateLimit-Remaining", out var remaining) && remaining.Any(v => v.Trim() == "0"))
        {
            return true;
        }

        return response.Headers.RetryAfter is not null;
    }
}