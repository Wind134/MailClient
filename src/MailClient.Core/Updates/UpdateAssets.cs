using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace MailClient.Core.Updates;

/// <summary>从发布资源里挑出安装程序和校验和文件。</summary>
public static class UpdateAssets
{
    /// <summary>
    /// 挑安装程序：名字匹配 MailClient-Setup-*.exe（忽略大小写，不含路径分隔符）。
    /// 有多个时优先名字里带当前发布版本号的，其次按名字排序取第一个。
    /// </summary>
    public static ReleaseAsset? SelectInstaller(ReleaseInfo release)
    {
        var candidates = release.Assets.Where(a => IsInstallerName(a.Name)).ToList();
        if (candidates.Count == 0)
        {
            return null;
        }

        var shortVersion = release.Version.Revision == 0 ? release.Version.ToString(3) : release.Version.ToString();
        return candidates
            .OrderByDescending(a => a.Name.Contains(shortVersion, StringComparison.OrdinalIgnoreCase))
            .ThenBy(a => a.Name, StringComparer.OrdinalIgnoreCase)
            .First();
    }

    public static ReleaseAsset? SelectChecksums(ReleaseInfo release)
        => release.Assets.FirstOrDefault(a => string.Equals(a.Name, UpdateConfig.ChecksumAssetName, StringComparison.OrdinalIgnoreCase));

    public static bool IsInstallerName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.IndexOfAny(new[] { '/', '\\', ':' }) >= 0 || name.Contains(".."))
        {
            return false;
        }

        return name.StartsWith("MailClient-Setup-", StringComparison.OrdinalIgnoreCase)
            && name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            && name.Length > "MailClient-Setup-.exe".Length;
    }
}

/// <summary>SHA256SUMS.txt 的解析与校验。</summary>
public static partial class Sha256Sums
{
    [GeneratedRegex(@"^\s*([0-9a-fA-F]{64})\s+\*?(.+?)\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex LinePattern();

    /// <summary>解析「sha256 空格 文件名」格式（也接受 sha256sum 的二进制标记 *）。返回 文件名 → 小写十六进制；文件名忽略大小写。</summary>
    public static Dictionary<string, string> Parse(string? text)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrEmpty(text))
        {
            return result;
        }

        foreach (var line in text.TrimStart('\uFEFF').Split('\n'))
        {
            var m = LinePattern().Match(line.TrimEnd('\r'));
            if (m.Success)
            {
                result[m.Groups[2].Value.Trim()] = m.Groups[1].Value.ToLowerInvariant();
            }
        }

        return result;
    }

    /// <summary>十六进制比较（忽略大小写 / 前后空白）。</summary>
    public static bool Matches(string? expected, string? actual)
        => !string.IsNullOrWhiteSpace(expected) && !string.IsNullOrWhiteSpace(actual)
           && string.Equals(expected.Trim(), actual.Trim(), StringComparison.OrdinalIgnoreCase);

    public static async Task<string> ComputeFileAsync(string path, CancellationToken ct = default)
    {
        await using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, useAsync: true);
        var hash = await SHA256.HashDataAsync(fs, ct).ConfigureAwait(false);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}