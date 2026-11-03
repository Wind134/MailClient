namespace MailClient.Core.Updates;

/// <summary>要不要为某个发布提示用户：比当前新，并且用户没有「跳过此版本」（手动检查时无视跳过）。</summary>
public static class UpdateSkipPolicy
{
    public static bool ShouldAnnounce(ReleaseInfo release, Version current, string? skippedTag, bool manual)
    {
        if (!UpdateVersion.IsNewer(release.Version, current))
        {
            return false;
        }

        return manual || !IsSkipped(release, skippedTag);
    }

    public static bool IsSkipped(ReleaseInfo release, string? skippedTag)
    {
        if (!UpdateVersion.TryParseTag(skippedTag, out var skipped))
        {
            return false;
        }

        return UpdateVersion.Normalize(skipped).Equals(UpdateVersion.Normalize(release.Version));
    }
}