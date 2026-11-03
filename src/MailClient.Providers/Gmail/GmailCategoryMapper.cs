using MailClient.Core;

namespace MailClient.Providers;

/// <summary>
/// Gmail 标签页（category:*）→ 应用的四个分类。纯函数，便于单测。
/// 主要→主要；purchases→交易；updates / social / forums→更新；promotions→促销。
/// </summary>
public static class GmailCategoryMapper
{
    /// <summary>需要向服务器查询的 Gmail 分类（其余即“主要”）。</summary>
    public static readonly string[] QueriedCategories = ["promotions", "purchases", "updates", "social", "forums"];

    public static MailCategory? FromGmailCategory(string? name) => name?.Trim().ToLowerInvariant() switch
    {
        "primary" => MailCategory.Primary,
        "purchases" => MailCategory.Transactions,
        "updates" or "social" or "forums" => MailCategory.Updates,
        "promotions" => MailCategory.Promotions,
        _ => null
    };

    /// <summary>
    /// 一封邮件可能命中多个 category 搜索（极少）；按 促销 &gt; 交易 &gt; 更新 的优先级取一个，都未命中则为主要。
    /// </summary>
    public static MailCategory Resolve(uint uid, IReadOnlyDictionary<string, HashSet<uint>> uidsByCategory)
    {
        foreach (var name in new[] { "promotions", "purchases", "updates", "social", "forums" })
        {
            if (uidsByCategory.TryGetValue(name, out var set) && set.Contains(uid))
            {
                return FromGmailCategory(name) ?? MailCategory.Primary;
            }
        }

        return MailCategory.Primary;
    }
}
