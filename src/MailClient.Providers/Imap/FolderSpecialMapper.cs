using MailKit;
using SpecialFolder = MailClient.Core.SpecialFolder;

namespace MailClient.Providers.Imap;

/// <summary>
/// 将 IMAP SPECIAL-USE 属性与常见文件夹名映射到 <see cref="SpecialFolder"/>。
/// 纯函数，便于单测（不连服务器）。
/// </summary>
public static class FolderSpecialMapper
{
    public static SpecialFolder Map(FolderAttributes attributes, string fullName, string name)
    {
        if (attributes.HasFlag(FolderAttributes.Inbox) || IsInboxName(fullName, name))
        {
            return SpecialFolder.Inbox;
        }

        if (attributes.HasFlag(FolderAttributes.Sent) || MatchesAny(fullName, name, SentNames))
        {
            return SpecialFolder.Sent;
        }

        if (attributes.HasFlag(FolderAttributes.Drafts) || MatchesAny(fullName, name, DraftNames))
        {
            return SpecialFolder.Drafts;
        }

        if (attributes.HasFlag(FolderAttributes.Trash) || MatchesAny(fullName, name, TrashNames))
        {
            return SpecialFolder.Trash;
        }

        // 归档 / 垃圾邮件：SPECIAL-USE 的 \Archive、\All（Gmail 的“所有邮件”）、\Junk（Gmail 的“垃圾邮件”）。
        if (attributes.HasFlag(FolderAttributes.Junk) || MatchesAny(fullName, name, JunkNames))
        {
            return SpecialFolder.Junk;
        }

        if (attributes.HasFlag(FolderAttributes.Archive) || attributes.HasFlag(FolderAttributes.All) ||
            MatchesAny(fullName, name, ArchiveNames))
        {
            return SpecialFolder.Archive;
        }

        // Gmail 的 [Gmail]/Starred（\Flagged）。\Important 不算“已标记”，保持自定义文件夹。
        if (attributes.HasFlag(FolderAttributes.Flagged) || MatchesAny(fullName, name, FlaggedNames))
        {
            return SpecialFolder.Flagged;
        }

        return SpecialFolder.Custom;
    }

    /// <summary>测试友好重载：仅按名称启发式（无 SPECIAL-USE）。</summary>
    public static SpecialFolder MapByName(string fullName, string? name = null)
        => Map(FolderAttributes.None, fullName, name ?? fullName);

    public static int DefaultSortOrder(SpecialFolder special) => special switch
    {
        SpecialFolder.Inbox => 0,
        SpecialFolder.Flagged => 1,
        SpecialFolder.Drafts => 2,
        SpecialFolder.Sent => 3,
        SpecialFolder.Outbox => 4,
        SpecialFolder.Archive => 5,
        SpecialFolder.Junk => 6,
        SpecialFolder.Trash => 7,
        _ => 100
    };

    private static readonly string[] SentNames =
    [
        "sent", "sent messages", "sent items", "sent mail",
        "已发送", "已寄邮件", "寄件备份"
    ];

    private static readonly string[] DraftNames =
    [
        "drafts", "draft", "草稿箱", "草稿"
    ];

    private static readonly string[] TrashNames =
    [
        "trash", "deleted", "deleted items", "deleted messages",
        "废纸篓", "已删除", "已删除邮件", "垃圾桶"
    ];

    private static readonly string[] FlaggedNames =
    [
        "flagged", "starred", "星标", "星标邮件"
    ];

    private static readonly string[] JunkNames =
    [
        "junk", "junk email", "junk e-mail", "spam", "bulk mail",
        "垃圾邮件", "垃圾箱", "垃圾"
    ];

    private static readonly string[] ArchiveNames =
    [
        "archive", "archives", "all mail",
        "归档", "存档", "所有邮件", "全部邮件"
    ];

    private static bool IsInboxName(string fullName, string name)
    {
        var candidates = new[] { fullName, name };
        foreach (var c in candidates)
        {
            if (string.Equals(c, "INBOX", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (string.Equals(c, "收件箱", StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static bool MatchesAny(string fullName, string name, string[] aliases)
    {
        foreach (var raw in new[] { fullName, name })
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                continue;
            }

            var leaf = raw;
            var slash = raw.LastIndexOfAny(['/', '.']);
            if (slash >= 0 && slash < raw.Length - 1)
            {
                leaf = raw[(slash + 1)..];
            }

            foreach (var alias in aliases)
            {
                if (string.Equals(leaf, alias, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(raw, alias, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }

        return false;
    }
}
