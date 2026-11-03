using System.Text.Json;

namespace MailClient.Core.Updates;

public sealed record ReleaseAsset(string Name, string DownloadUrl, long Size);

/// <summary>GitHub Release 里和更新有关的那部分信息。</summary>
public sealed record ReleaseInfo(
    string TagName,
    Version Version,
    string Title,
    string Body,
    string HtmlUrl,
    DateTimeOffset? PublishedAt,
    IReadOnlyList<ReleaseAsset> Assets);

/// <summary>解析 GitHub <c>releases/latest</c> 的 JSON（用 JsonDocument，不依赖反射序列化，裁剪 / 源生成环境都能用）。</summary>
public static class ReleaseParser
{
    /// <summary>解析失败（不是 JSON、缺 tag_name、版本号无法识别、草稿 / 预发布）时返回 null 并给出原因。</summary>
    public static ReleaseInfo? TryParse(string? json, out string? error)
    {
        error = null;
        if (string.IsNullOrWhiteSpace(json))
        {
            error = "响应为空";
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                error = "响应不是 JSON 对象";
                return null;
            }

            if (Bool(root, "draft") || Bool(root, "prerelease"))
            {
                error = "最新发布是草稿或预发布版本";
                return null;
            }

            var tag = Str(root, "tag_name");
            if (string.IsNullOrWhiteSpace(tag))
            {
                error = "响应里没有 tag_name";
                return null;
            }

            if (!UpdateVersion.TryParseTag(tag, out var version))
            {
                error = $"无法识别的版本标签：{tag}";
                return null;
            }

            var assets = new List<ReleaseAsset>();
            if (root.TryGetProperty("assets", out var arr) && arr.ValueKind == JsonValueKind.Array)
            {
                foreach (var a in arr.EnumerateArray())
                {
                    if (a.ValueKind != JsonValueKind.Object)
                    {
                        continue;
                    }

                    var name = Str(a, "name");
                    var url = Str(a, "browser_download_url");
                    if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(url))
                    {
                        continue;
                    }

                    var size = a.TryGetProperty("size", out var s) && s.ValueKind == JsonValueKind.Number && s.TryGetInt64(out var l) ? l : 0;
                    assets.Add(new ReleaseAsset(name!, url!, size));
                }
            }

            DateTimeOffset? published = null;
            if (Str(root, "published_at") is { } p && DateTimeOffset.TryParse(p, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal, out var dt))
            {
                published = dt;
            }

            var html = Str(root, "html_url");
            if (string.IsNullOrWhiteSpace(html))
            {
                html = UpdateConfig.ReleasesPageUrl();
            }

            return new ReleaseInfo(tag!, version, Str(root, "name") ?? tag!, Str(root, "body") ?? string.Empty, html!, published, assets);
        }
        catch (JsonException ex)
        {
            error = "响应不是有效的 JSON：" + ex.Message;
            return null;
        }
    }

    private static string? Str(JsonElement e, string name)
        => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static bool Bool(JsonElement e, string name)
        => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;
}