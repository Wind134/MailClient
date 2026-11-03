using System.Security.Cryptography;

namespace MailClient.Core.Updates;

public readonly record struct DownloadProgress(long Received, long? Total)
{
    /// <summary>0–1；总大小未知时为 null。</summary>
    public double? Fraction => Total is > 0 ? Math.Min(1.0, (double)Received / Total.Value) : null;
}

public enum UpdateErrorKind
{
    NoInstallerAsset,
    UntrustedUrl,
    ChecksumUnavailable,
    ChecksumMissingEntry,
    ChecksumMismatch,
    SizeMismatch,
    DownloadFailed
}

public sealed class UpdateException : Exception
{
    public UpdateException(UpdateErrorKind kind, string message, Exception? inner = null) : base(message, inner) => Kind = kind;

    public UpdateErrorKind Kind { get; }
}

/// <param name="Verified">true = 已按 SHA256SUMS.txt 校验通过；false = 发布里没有校验和文件，未校验。</param>
public sealed record DownloadedUpdate(string Path, string Sha256, bool Verified);

/// <summary>下载安装包并按 SHA256SUMS.txt 校验；校验不通过就删除文件并拒绝。</summary>
public sealed class UpdateDownloader
{
    private const int MaxChecksumBytes = 256 * 1024;

    private readonly HttpClient _http;
    private readonly bool _allowLoopbackHttp;

    public UpdateDownloader(HttpClient http, bool allowLoopbackHttp = false)
    {
        _http = http;
        _allowLoopbackHttp = allowLoopbackHttp;
    }

    public async Task<DownloadedUpdate> DownloadInstallerAsync(
        ReleaseInfo release, string targetDirectory, IProgress<DownloadProgress>? progress, CancellationToken ct)
    {
        var installer = UpdateAssets.SelectInstaller(release)
            ?? throw new UpdateException(UpdateErrorKind.NoInstallerAsset, "这个发布里没有安装程序（MailClient-Setup-*.exe）。");
        if (!UpdateConfig.IsTrustedDownloadUrl(installer.DownloadUrl, _allowLoopbackHttp))
        {
            throw new UpdateException(UpdateErrorKind.UntrustedUrl, "安装包的下载地址不是 GitHub 的 https 地址，已拒绝。");
        }

        // 1) 校验和：发布里有 SHA256SUMS.txt 就必须有这个安装包的条目
        string? expected = null;
        var sums = UpdateAssets.SelectChecksums(release);
        if (sums is not null)
        {
            if (!UpdateConfig.IsTrustedDownloadUrl(sums.DownloadUrl, _allowLoopbackHttp))
            {
                throw new UpdateException(UpdateErrorKind.UntrustedUrl, "校验和文件的下载地址不可信，已拒绝。");
            }

            var text = await GetSmallTextAsync(sums.DownloadUrl, ct).ConfigureAwait(false);
            if (!Sha256Sums.Parse(text).TryGetValue(installer.Name, out expected))
            {
                throw new UpdateException(UpdateErrorKind.ChecksumMissingEntry, $"SHA256SUMS.txt 里没有 {installer.Name} 的校验值，已拒绝。");
            }
        }

        // 2) 下载到 .part，边下边算哈希
        Directory.CreateDirectory(targetDirectory);
        var finalPath = Path.Combine(targetDirectory, installer.Name);
        var partPath = finalPath + ".part";
        string actual;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, installer.DownloadUrl);
            request.Headers.Accept.Clear();
            request.Headers.Accept.ParseAdd("application/octet-stream");
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                throw new UpdateException(UpdateErrorKind.DownloadFailed, "下载失败：服务器返回 " + (int)response.StatusCode);
            }

            var total = response.Content.Headers.ContentLength ?? (installer.Size > 0 ? installer.Size : null);
            using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            long received = 0;
            await using (var source = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
            await using (var target = new FileStream(partPath, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true))
            {
                var buffer = new byte[1 << 16];
                var lastReport = Environment.TickCount64;
                int n;
                while ((n = await source.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
                {
                    await target.WriteAsync(buffer.AsMemory(0, n), ct).ConfigureAwait(false);
                    hasher.AppendData(buffer, 0, n);
                    received += n;
                    if (progress is not null && Environment.TickCount64 - lastReport >= 80)
                    {
                        lastReport = Environment.TickCount64;
                        progress.Report(new DownloadProgress(received, total));
                    }
                }
            }

            progress?.Report(new DownloadProgress(received, total ?? received));
            if (total is { } t && received != t)
            {
                throw new UpdateException(UpdateErrorKind.SizeMismatch, $"下载不完整（{received} / {t} 字节）。");
            }

            actual = Convert.ToHexString(hasher.GetHashAndReset()).ToLowerInvariant();
        }
        catch (UpdateException)
        {
            TryDelete(partPath);
            throw;
        }
        catch (OperationCanceledException)
        {
            TryDelete(partPath);
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException)
        {
            TryDelete(partPath);
            throw new UpdateException(UpdateErrorKind.DownloadFailed, "下载失败：" + ex.Message, ex);
        }

        // 3) 校验
        if (expected is not null && !Sha256Sums.Matches(expected, actual))
        {
            TryDelete(partPath);
            throw new UpdateException(UpdateErrorKind.ChecksumMismatch, "安装包的 SHA256 与发布的校验值不一致，已删除并拒绝安装。");
        }

        TryDelete(finalPath);
        File.Move(partPath, finalPath);
        return new DownloadedUpdate(finalPath, actual, Verified: expected is not null);
    }

    private async Task<string> GetSmallTextAsync(string url, CancellationToken ct)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Accept.Clear();
            request.Headers.Accept.ParseAdd("application/octet-stream");
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                throw new UpdateException(UpdateErrorKind.ChecksumUnavailable, "无法下载 SHA256SUMS.txt：服务器返回 " + (int)response.StatusCode);
            }

            await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var ms = new MemoryStream();
            var buffer = new byte[8192];
            int n;
            while ((n = await stream.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
            {
                ms.Write(buffer, 0, n);
                if (ms.Length > MaxChecksumBytes)
                {
                    throw new UpdateException(UpdateErrorKind.ChecksumUnavailable, "SHA256SUMS.txt 异常地大，已拒绝。");
                }
            }

            return System.Text.Encoding.UTF8.GetString(ms.ToArray());
        }
        catch (HttpRequestException ex)
        {
            throw new UpdateException(UpdateErrorKind.ChecksumUnavailable, "无法下载 SHA256SUMS.txt：" + ex.Message, ex);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception)
        {
            // 清理失败无所谓
        }
    }
}