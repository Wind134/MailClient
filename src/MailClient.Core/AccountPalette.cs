using System.Globalization;
using System.Text;

namespace MailClient.Core;

/// <summary>账号头像的颜色与字母：保证每个账号都有颜色（用户没选时按账号 Id 稳定地分配，并尽量互不相同）。</summary>
public static class AccountPalette
{
    /// <summary>默认色板（与「添加账号」向导的可选色一致风格，深浅适中，白字清晰）。</summary>
    public static readonly IReadOnlyList<string> Colors =
    [
        "#0F6CBD", // 蓝
        "#C239B3", // 品红
        "#107C10", // 绿
        "#CA5010", // 橙
        "#8764B8", // 紫
        "#038387", // 青
        "#C50F1F", // 红
        "#986F0B", // 琥珀
        "#4F6BED", // 靛
        "#5C7E1E"  // 橄榄
    ];

    /// <summary>是否是 #RRGGBB。</summary>
    public static bool IsValidHex(string? hex)
        => hex is { Length: 7 } && hex[0] == '#' &&
           int.TryParse(hex.AsSpan(1), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out _);

    /// <summary>按账号 Id 稳定地挑一个起始色（同一账号永远得到同一个）。</summary>
    public static string ColorFor(Guid accountId)
    {
        var bytes = accountId.ToByteArray();
        var hash = 17;
        foreach (var b in bytes)
        {
            hash = unchecked(hash * 31 + b);
        }

        return Colors[(int)((uint)hash % (uint)Colors.Count)];
    }

    /// <summary>
    /// 给一组账号定色：用户已设的合法颜色优先；没设的从 <see cref="ColorFor"/> 的起点开始，
    /// 顺延到第一个还没被占用的色板颜色（色板用完才允许重复）。结果与账号顺序有关但是确定的。
    /// </summary>
    public static IReadOnlyDictionary<Guid, string> Assign(IEnumerable<(Guid Id, string? Saved)> accounts)
    {
        var list = accounts.ToList();
        var result = new Dictionary<Guid, string>();
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (id, saved) in list)
        {
            if (IsValidHex(saved))
            {
                result[id] = saved!;
                used.Add(saved!);
            }
        }

        foreach (var (id, saved) in list)
        {
            if (result.ContainsKey(id))
            {
                continue;
            }

            var start = Colors.ToList().IndexOf(ColorFor(id));
            var pick = Colors[start];
            for (var i = 0; i < Colors.Count; i++)
            {
                var candidate = Colors[(start + i) % Colors.Count];
                if (!used.Contains(candidate))
                {
                    pick = candidate;
                    break;
                }
            }

            result[id] = pick;
            used.Add(pick);
        }

        return result;
    }

    /// <summary>头像字母：显示名第一个可见字符；没有显示名则取邮箱本地部分第一个字符；都没有则 "?"。</summary>
    public static string LetterFor(string? displayName, string? emailAddress)
    {
        var first = FirstVisible(displayName);
        if (first is null)
        {
            var email = emailAddress ?? string.Empty;
            var at = email.IndexOf('@');
            first = FirstVisible(at > 0 ? email[..at] : email.TrimStart('@'));
        }

        return first ?? "?";
    }

    private static string? FirstVisible(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        foreach (var rune in text.Trim().EnumerateRunes())
        {
            if (!Rune.IsWhiteSpace(rune) && !Rune.IsControl(rune))
            {
                return Rune.ToUpperInvariant(rune).ToString();
            }
        }

        return null;
    }
}
