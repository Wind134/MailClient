namespace MailClient.Core;

/// <summary>
/// 检测旧缓存中是否残留了乱码正文（被误按 UTF-8 解码后留下的多个 U+FFFD）或 Latin-1 乱码附件名，
/// 用于在打开邮件时触发一次自动重拉覆盖旧缓存。
/// </summary>
public static class GarbledText
{
    /// <summary>正文或 HTML 中出现 2 个及以上替换字符（U+FFFD），高度疑似旧缓存编码错误。</summary>
    public static bool LooksGarbled(string? bodyText, string? bodyHtml)
        => CountReplacementChars(bodyText) >= 2 || CountReplacementChars(bodyHtml) >= 2;

    /// <summary>文件名全在 Latin-1 范围内且含连续高位字节（>= 0x80），疑似原始 GBK/UTF-8 字节被误解。</summary>
    public static bool LooksGarbledFileName(string? fileName)
    {
        if (string.IsNullOrEmpty(fileName))
        {
            return false;
        }

        var hasConsecutiveHigh = false;
        var prevHigh = false;
        for (var i = 0; i < fileName.Length; i++)
        {
            var c = fileName[i];
            if (c > 0xFF)
            {
                return false;
            }

            var isHigh = c >= 0x80;
            if (isHigh && prevHigh)
            {
                hasConsecutiveHigh = true;
            }

            prevHigh = isHigh;
        }

        return hasConsecutiveHigh;
    }

    public static bool HasGarbledFileName(IEnumerable<string?> fileNames)
    {
        foreach (var name in fileNames)
        {
            if (LooksGarbledFileName(name))
            {
                return true;
            }
        }

        return false;
    }

    private static int CountReplacementChars(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return 0;
        }

        var count = 0;
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '\uFFFD' && ++count >= 2)
            {
                return count;
            }
        }

        return count;
    }
}
