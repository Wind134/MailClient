namespace MailClient.Core.Sync;

public enum CoalesceAction
{
    /// <summary>没有旧记录：直接插入新的。</summary>
    Insert,
    /// <summary>用 <see cref="CoalesceResult.Op"/> 覆盖旧记录。</summary>
    Replace,
    /// <summary>两次变更互相抵消，删除旧记录、不写新的。</summary>
    Remove,
    /// <summary>保持旧记录不变（例如已经要永久删除了）。</summary>
    Keep
}

public sealed record CoalesceResult(CoalesceAction Action, PendingOpEntity? Op);

/// <summary>
/// 合并同一封邮件、同一字段上的变更：最后写入者胜出；
/// 移动会保留“最初所在文件夹”，A→B 再 B→A 视为没有变化。纯函数，可单测。
/// </summary>
public static class PendingOpCoalescer
{
    public static CoalesceResult Coalesce(PendingOpEntity? existing, PendingOpEntity incoming)
    {
        ArgumentNullException.ThrowIfNull(incoming);
        if (existing is null)
        {
            return incoming.Kind == PendingOpKind.Move && incoming.SourceFolderId == incoming.TargetFolderId
                ? new CoalesceResult(CoalesceAction.Keep, null)
                : new CoalesceResult(CoalesceAction.Insert, incoming);
        }

        if (existing.Field != incoming.Field)
        {
            throw new InvalidOperationException("只能合并同一字段上的变更。");
        }

        if (incoming.Field != PendingField.Location)
        {
            // 已读 / 标记：直接以最新的为准，定位信息沿用最初的
            return new CoalesceResult(CoalesceAction.Replace, incoming with
            {
                SourceFolderId = existing.SourceFolderId ?? incoming.SourceFolderId,
                RemoteKey = existing.RemoteKey ?? incoming.RemoteKey,
                CreatedAt = existing.CreatedAt,
                Attempts = 0,
                LastError = null,
                NextAttemptAt = null
            });
        }

        // 位置：已经要永久删除了，之后的任何变更都无意义
        if (existing.Kind == PendingOpKind.Delete)
        {
            return new CoalesceResult(CoalesceAction.Keep, existing);
        }

        var original = existing.SourceFolderId;
        if (incoming.Kind == PendingOpKind.Delete)
        {
            return new CoalesceResult(CoalesceAction.Replace, incoming with
            {
                SourceFolderId = original ?? incoming.SourceFolderId,
                RemoteKey = existing.RemoteKey ?? incoming.RemoteKey,
                CreatedAt = existing.CreatedAt,
                Attempts = 0,
                LastError = null,
                NextAttemptAt = null
            });
        }

        // Move 接 Move
        if (original is not null && string.Equals(original, incoming.TargetFolderId, StringComparison.Ordinal))
        {
            return new CoalesceResult(CoalesceAction.Remove, null);
        }

        return new CoalesceResult(CoalesceAction.Replace, incoming with
        {
            SourceFolderId = original ?? incoming.SourceFolderId,
            RemoteKey = existing.RemoteKey ?? incoming.RemoteKey,
            CreatedAt = existing.CreatedAt,
            Attempts = 0,
            LastError = null,
            NextAttemptAt = null
        });
    }
}
