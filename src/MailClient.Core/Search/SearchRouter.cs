using System.Text;

namespace MailClient.Core.Search;

/// <summary>某个搜索词用哪种方式匹配。</summary>
public enum MatchStrategy
{
    /// <summary>FTS5 trigram 分词（≥3 个字符的任意子串，含中文）。</summary>
    Trigram,

    /// <summary>FTS5 unicode61 的词前缀匹配（1–2 个拉丁字母 / 数字，或 trigram 不可用时的拉丁词）。</summary>
    FtsPrefix,

    /// <summary>LIKE '%…%' 子串匹配（1–2 个字符的中文等，或 to:，或 FTS 不可用时）。</summary>
    Like
}

/// <summary>为每个搜索词选择匹配方式。</summary>
public static class SearchRouter
{
    public static MatchStrategy Route(SearchTerm term, bool trigramAvailable)
    {
        if (term.Field == SearchField.To)
        {
            return MatchStrategy.Like; // 收件人在 recipients 表里，不在 FTS 里
        }

        var length = CodePointCount(term.Text);
        if (trigramAvailable && length >= 3)
        {
            return MatchStrategy.Trigram;
        }

        if (IsAsciiWord(term.Text) && (length <= 2 || !trigramAvailable))
        {
            return MatchStrategy.FtsPrefix;
        }

        return MatchStrategy.Like;
    }

    public static int CodePointCount(string text)
    {
        var n = 0;
        foreach (var _ in text.EnumerateRunes())
        {
            n++;
        }

        return n;
    }

    private static bool IsAsciiWord(string text)
    {
        if (text.Length == 0)
        {
            return false;
        }

        foreach (var c in text)
        {
            if (!(c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9'))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>FTS5 字符串字面量：双引号包起来，内部的双引号写成两个。</summary>
    public static string QuoteFts(string text)
    {
        var sb = new StringBuilder("\"");
        foreach (var c in text)
        {
            sb.Append(c);
            if (c == '"')
            {
                sb.Append('"');
            }
        }

        return sb.Append('"').ToString();
    }
}
