namespace MailClient.Core.Sending;

/// <summary>
/// 草稿附件的应用私有副本（%LocalAppData%\MailClient\drafts\{draftId}\）：
/// 用户之后移动 / 删除原文件，重新打开草稿时附件仍然在。
/// </summary>
public static class DraftAttachmentStore
{
    public static string DirectoryFor(string root, string draftId) => Path.Combine(root, Sanitize(draftId));

    /// <summary>
    /// 让草稿目录与给定附件列表一致：已在草稿目录里的保持不动，其他的复制进来，列表之外的文件删除。
    /// 返回草稿目录内的最终路径（与输入顺序一致）。源文件已不存在（且不在草稿目录里）的会被跳过。
    /// </summary>
    public static IReadOnlyList<string> Sync(string root, string draftId, IEnumerable<string> sourcePaths)
    {
        var dir = DirectoryFor(root, draftId);
        var dirFull = EnsureTrailingSeparator(Path.GetFullPath(dir));
        var result = new List<string>();
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var source in sourcePaths)
        {
            if (string.IsNullOrWhiteSpace(source) || !File.Exists(source))
            {
                continue;
            }

            var full = Path.GetFullPath(source);
            if (full.StartsWith(dirFull, StringComparison.OrdinalIgnoreCase))
            {
                used.Add(Path.GetFileName(full));
                result.Add(full);
                continue;
            }

            Directory.CreateDirectory(dir);
            var name = Path.GetFileName(full);
            var candidate = name;
            for (var i = 2; !used.Add(candidate); i++)
            {
                candidate = $"{Path.GetFileNameWithoutExtension(name)} ({i}){Path.GetExtension(name)}";
            }

            var target = Path.Combine(dir, candidate);
            if (!IsSameCopy(full, target))
            {
                File.Copy(full, target, overwrite: true);
            }

            result.Add(target);
        }

        if (Directory.Exists(dir))
        {
            var keep = new HashSet<string>(result.Select(Path.GetFullPath), StringComparer.OrdinalIgnoreCase);
            foreach (var file in Directory.EnumerateFiles(dir))
            {
                if (!keep.Contains(Path.GetFullPath(file)))
                {
                    TryDeleteFile(file);
                }
            }

            if (result.Count == 0)
            {
                Delete(root, draftId);
            }
        }

        return result;
    }

    public static void Delete(string? root, string draftId)
    {
        if (string.IsNullOrEmpty(root))
        {
            return;
        }

        try
        {
            var dir = DirectoryFor(root, draftId);
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    /// <summary>删除草稿根目录下没有对应草稿的子目录（崩溃 / 账号被删后的残留）。</summary>
    public static int DeleteOrphans(string? root, IReadOnlySet<string> liveDraftIds)
    {
        if (string.IsNullOrEmpty(root) || !Directory.Exists(root))
        {
            return 0;
        }

        var live = new HashSet<string>(liveDraftIds.Select(Sanitize), StringComparer.OrdinalIgnoreCase);
        var removed = 0;
        foreach (var dir in Directory.EnumerateDirectories(root))
        {
            if (!live.Contains(Path.GetFileName(dir)))
            {
                Delete(root, Path.GetFileName(dir));
                removed++;
            }
        }

        return removed;
    }

    /// <summary>目标已是该源文件的副本（大小与修改时间一致）：自动保存时不必每 3 秒重复复制大附件。</summary>
    private static bool IsSameCopy(string source, string target)
    {
        try
        {
            var a = new FileInfo(source);
            var b = new FileInfo(target);
            return b.Exists && a.Length == b.Length && a.LastWriteTimeUtc == b.LastWriteTimeUtc;
        }
        catch (IOException)
        {
            return false;
        }
    }

    private static string Sanitize(string id)
    {
        var invalid = Path.GetInvalidFileNameChars();
        return string.Concat(id.Select(c => invalid.Contains(c) ? '_' : c));
    }

    private static string EnsureTrailingSeparator(string path)
        => path.EndsWith(Path.DirectorySeparatorChar) ? path : path + Path.DirectorySeparatorChar;

    private static void TryDeleteFile(string file)
    {
        try
        {
            File.Delete(file);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
