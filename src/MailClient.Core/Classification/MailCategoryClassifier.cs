namespace MailClient.Core.Classification;

/// <summary>
/// 基于发件人 / 关键词的轻量规则分类器（主要 / 交易 / 更新 / 促销）。
/// 纯函数、无依赖，便于单测；后续可被服务端标签或用户自定义规则覆盖。
/// 判定顺序：交易 → 促销 → 更新（自动发件人兜底）→ 主要。
/// </summary>
public static class MailCategoryClassifier
{
    private static readonly string[] TransactionKeywords =
    [
        "订单", "发货", "已发货", "快递", "物流", "签收", "送达", "账单", "发票", "付款", "支付", "收据", "退款",
        "购票", "验证码", "到期", "续费", "扣款", "还款",
        "order", "receipt", "invoice", "shipped", "shipment", "delivery", "payment", "refund",
        "confirmation", "verification code", "billing", "statement"
    ];

    private static readonly string[] TransactionSenderHints =
    [
        "order", "receipt", "invoice", "billing", "payment", "alipay", "jd.com", "12306", "cmbchina", "didi", "meituan"
    ];

    private static readonly string[] PromotionKeywords =
    [
        "优惠", "折扣", "促销", "限时", "特价", "立减", "满减", "优惠券", "领券", "红包", "抢购", "大促", "狂欢",
        "双11", "双十一", "买一送一", "折起", "会员专享", "新品",
        "sale", "% off", "discount", "coupon", "deal", "promo", "limited time", "unsubscribe"
    ];

    private static readonly string[] PromotionSenderHints =
    [
        "promo", "marketing", "campaign", "deals", "offers", "ads@", "edm@"
    ];

    private static readonly string[] UpdateKeywords =
    [
        "通知", "周刊", "热榜", "摘要", "提醒", "安全", "新登录", "登录活动", "订阅",
        "newsletter", "digest", "weekly", "alert", "security", "notification", "sign-in", "new sign-in"
    ];

    private static readonly string[] AutomatedSenderHints =
    [
        "noreply", "no-reply", "donotreply", "do-not-reply", "notification", "notifications", "notify",
        "mailer", "newsletter", "digest", "feedback@", "news@", "team@", "info@", "support@", "service@", "system@"
    ];

    public static MailCategory Classify(string? fromAddress, string? fromName, string? subject, string? preview = null)
    {
        var address = fromAddress ?? string.Empty;
        var name = fromName ?? string.Empty;
        var subj = subject ?? string.Empty;
        var body = preview ?? string.Empty;

        // 先看标题（最强信号），再看发件人，最后看预览。
        if (ContainsAny(subj, TransactionKeywords) || ContainsAny(address, TransactionSenderHints))
        {
            return MailCategory.Transactions;
        }

        if (ContainsAny(subj, PromotionKeywords) || ContainsAny(address, PromotionSenderHints))
        {
            return MailCategory.Promotions;
        }

        var automated = ContainsAny(address, AutomatedSenderHints);
        if (ContainsAny(subj, UpdateKeywords) || automated)
        {
            return MailCategory.Updates;
        }

        // 预览里的强信号（只在标题没有命中时作为补充）。
        if (ContainsAny(body, ["退订", "unsubscribe", "优惠券", "限时优惠"]))
        {
            return MailCategory.Promotions;
        }

        _ = name;
        return MailCategory.Primary;
    }

    private static bool ContainsAny(string text, IReadOnlyList<string> needles)
    {
        if (string.IsNullOrEmpty(text))
        {
            return false;
        }

        foreach (var needle in needles)
        {
            if (text.Contains(needle, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
