using System.Buffers;

namespace MailClient.Core.Sending;

/// <summary>
/// 附件私有副本目录的共享原语。草稿（drafts）与发件箱（outbox）用的是同一套
/// 命名 / 去重 / 清理规则，集中在这里定义，避免两处各写一份再慢慢漂移。
/// </summary>
internal static class AttachmentDirectory
{
    private static readonly SearchValues<char> InvalidFileNameChars = SearchValues.Create(Path.GetInvalidFileNameChars());

    /// <summary>剔除路径非法字符，并拒绝空串与 <c>.</c> / <c>..</c> 等特殊目录名，防止外部 id 拼出目录穿越。</summary>
    public static string Sanitize(string id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);

        var cleaned = string.Create(id.Length, id, static (span, source) =>
        {
            for (var i = 0; i < source.Length; i++)
            {
                var c = source[i];
                span[i] = InvalidFileNameChars.Contains(c) ? '_' : c;
            }
        }).Trim();

        if (cleaned.Length == 0 || cleaned.TrimEnd('.').Length == 0)
        {
            throw new ArgumentException($"附件目录标识非法：{id}", nameof(id));
        }

        return cleaned;
    }

    public static string EnsureTrailingSeparator(string path)
        => path.EndsWith(Path.DirectorySeparatorChar) ? path : path + Path.DirectorySeparatorChar;

    /// <summary>同名附件按 “name (2).ext” 递增，返回可用文件名并登记到 <paramref name="used"/>。</summary>
    public static string UniqueName(HashSet<string> used, string fileName)
    {
        var candidate = fileName;
        for (var i = 2; !used.Add(candidate); i++)
        {
            candidate = $"{Path.GetFileNameWithoutExtension(fileName)} ({i}){Path.GetExtension(fileName)}";
        }

        return candidate;
    }

    /// <summary>目标已是该源文件的副本（大小与修改时间一致）：自动保存时不必重复复制大附件。</summary>
    public static bool IsSameCopy(string source, string target)
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

    /// <summary>递归删除目录；清理失败不影响主流程。</summary>
    public static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    public static void TryDeleteFile(string file)
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
