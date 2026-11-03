using System.Text.RegularExpressions;

namespace MailClient.Core.Updates;

/// <summary>标签（vX.Y.Z[.W]）的解析与比较。</summary>
public static partial class UpdateVersion
{
    [GeneratedRegex(@"^\s*[vV]?(\d{1,5})\.(\d{1,5})\.(\d{1,5})(?:\.(\d{1,5}))?\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex TagPattern();

    /// <summary>解析 v1.2.3 / 1.2.3.4 / V1.2.3；预发布后缀（-beta）、两段版本号等一律视为无法识别。结果统一补成四段。</summary>
    public static bool TryParseTag(string? tag, out Version version)
    {
        version = new Version(0, 0, 0, 0);
        if (string.IsNullOrWhiteSpace(tag))
        {
            return false;
        }

        var m = TagPattern().Match(tag);
        if (!m.Success)
        {
            return false;
        }

        var revision = m.Groups[4].Success ? int.Parse(m.Groups[4].Value) : 0;
        version = new Version(int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value), int.Parse(m.Groups[3].Value), revision);
        return true;
    }

    /// <summary>把任意 Version 补成四段（Build / Revision 为 -1 时按 0），便于比较 1.0.15 与 1.0.15.0。</summary>
    public static Version Normalize(Version v)
        => new(v.Major, v.Minor, Math.Max(0, v.Build), Math.Max(0, v.Revision));

    /// <summary>候选版本是否比当前版本新。</summary>
    public static bool IsNewer(Version candidate, Version current)
        => Normalize(candidate).CompareTo(Normalize(current)) > 0;

    /// <summary>给人看的版本号：1.0.15.0。</summary>
    public static string Display(Version v) => Normalize(v).ToString();
}