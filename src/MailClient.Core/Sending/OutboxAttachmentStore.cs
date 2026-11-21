namespace MailClient.Core.Sending;

/// <summary>
/// 把待发邮件的附件复制到应用自己的目录（%LocalAppData%\MailClient\outbox\{outboxId}\），
/// 这样用户之后移动 / 删除原文件、或应用崩溃重启，重试发送时附件仍然在。发送完成或删除条目后清理。
/// </summary>
public static class OutboxAttachmentStore
{
    public static string DirectoryFor(string root, string outboxId)
        => Path.Combine(root, AttachmentDirectory.Sanitize(outboxId));

    public static IReadOnlyList<string> CopyIn(string root, string outboxId, IEnumerable<string> sourcePaths)
    {
        var dir = DirectoryFor(root, outboxId);
        var result = new List<string>();
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var source in sourcePaths)
        {
            if (!File.Exists(source))
            {
                throw new FileNotFoundException($"附件不存在：{source}", source);
            }

            Directory.CreateDirectory(dir);
            var candidate = AttachmentDirectory.UniqueName(used, Path.GetFileName(source));
            var target = Path.Combine(dir, candidate);
            File.Copy(source, target, overwrite: true);
            result.Add(target);
        }

        return result;
    }

    public static void Delete(string? root, string outboxId)
    {
        if (string.IsNullOrEmpty(root))
        {
            return;
        }

        AttachmentDirectory.TryDeleteDirectory(DirectoryFor(root, outboxId));
    }
}
