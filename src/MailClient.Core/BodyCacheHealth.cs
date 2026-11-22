namespace MailClient.Core;

/// <summary>已缓存邮件正文的健康度检查结果（决定打开邮件时是否触发一次按需自愈重拉）。</summary>
public enum BodyCacheIssue
{
    /// <summary>缓存健康（或尚未缓存正文），无需自愈重拉。</summary>
    None,

    /// <summary>缓存由旧版解析器写入（BodyParserVersion &lt; CurrentParserVersion）。</summary>
    OutdatedParserVersion,

    /// <summary>HTML 中仍残留未改写的 cid: 内嵌图引用。</summary>
    UnresolvedCid,

    /// <summary>正文或 HTML 中存在大量 U+FFFD 乱码替换字符。</summary>
    GarbledBody,

    /// <summary>列表预览中存在大量 U+FFFD 乱码替换字符。</summary>
    GarbledPreview,

    /// <summary>附件名存在被按 Latin-1 误解的连续高位字节乱码。</summary>
    GarbledAttachmentName
}

/// <summary>
/// 本地正文缓存的统一健康度与自愈判定入口：
/// ViewModel 与 MessageBodyService 共用这一份规则，避免两处重复判定导致漂移。
/// 未来若升级正文解析/解码逻辑，只需将 <see cref="CurrentParserVersion"/> +1，
/// 旧缓存会在打开时自动按需重拉一次。
/// </summary>
public static class BodyCacheHealth
{
    /// <summary>当前正文解析器版本。破坏性升级正文解码/重写规则时递增此值。</summary>
    public const int CurrentParserVersion = 1;

    public static BodyCacheIssue Inspect(MessageEntity message, IReadOnlyList<AttachmentEntity>? attachments = null)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (!message.BodyFetched)
        {
            return BodyCacheIssue.None;
        }

        if (message.BodyParserVersion < CurrentParserVersion)
        {
            return BodyCacheIssue.OutdatedParserVersion;
        }

        if (CidHtmlRewriter.ContainsUnresolvedCid(message.BodyHtml))
        {
            return BodyCacheIssue.UnresolvedCid;
        }

        if (GarbledText.LooksGarbled(message.BodyText, message.BodyHtml))
        {
            return BodyCacheIssue.GarbledBody;
        }

        if (GarbledText.LooksGarbled(message.Preview, null))
        {
            return BodyCacheIssue.GarbledPreview;
        }

        if (attachments is not null && GarbledText.HasGarbledFileName(attachments.Select(a => a.FileName)))
        {
            return BodyCacheIssue.GarbledAttachmentName;
        }

        return BodyCacheIssue.None;
    }

    public static bool NeedsRefetch(MessageEntity message, IReadOnlyList<AttachmentEntity>? attachments = null)
        => Inspect(message, attachments) != BodyCacheIssue.None;
}
