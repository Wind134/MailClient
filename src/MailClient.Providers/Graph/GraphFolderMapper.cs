using MailClient.Core;
using MailClient.Providers.Imap;

namespace MailClient.Providers.Graph;

/// <summary>
/// 将 Graph mailFolder 的 displayName / 已知名称映射到 <see cref="SpecialFolder"/>。
/// </summary>
public static class GraphFolderMapper
{
    /// <summary>Graph 路径中可用的 well-known 名称。</summary>
    public static readonly HashSet<string> WellKnownNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "inbox", "sentitems", "deleteditems", "drafts", "junkemail", "archive", "outbox"
    };

    public static SpecialFolder Map(string? displayName, string? wellKnownOrId = null)
    {
        if (!string.IsNullOrWhiteSpace(wellKnownOrId))
        {
            var mapped = MapWellKnown(wellKnownOrId);
            if (mapped != SpecialFolder.Custom)
            {
                return mapped;
            }
        }

        if (!string.IsNullOrWhiteSpace(displayName))
        {
            return FolderSpecialMapper.MapByName(displayName);
        }

        return SpecialFolder.Custom;
    }

    public static SpecialFolder MapWellKnown(string name) => name.Trim().ToLowerInvariant() switch
    {
        "inbox" => SpecialFolder.Inbox,
        "sentitems" => SpecialFolder.Sent,
        "deleteditems" => SpecialFolder.Trash,
        "drafts" => SpecialFolder.Drafts,
        "outbox" => SpecialFolder.Outbox,
        "junkemail" => SpecialFolder.Junk,
        "archive" => SpecialFolder.Archive,
        _ => SpecialFolder.Custom
    };

    public static int DefaultSortOrder(SpecialFolder special) =>
        FolderSpecialMapper.DefaultSortOrder(special);
}
