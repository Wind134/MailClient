using System.Globalization;

namespace MailClient.Core.Search;

/// <summary>
/// 解析搜索框文本。支持：
/// <c>from:</c> <c>to:</c> <c>subject:</c>（以及 发件人: 收件人: 主题:）、
/// <c>has:attachment</c>（附件）、<c>is:unread|read|flagged|unflagged</c>（未读 已读 已标记 未标记）、
/// <c>before:YYYY-MM-DD</c>（不含当天）/ <c>after:YYYY-MM-DD</c>（含当天），
/// 双引号短语（<c>"季度 报告"</c>，操作符的值也可以加引号）；其余为自由文本词。条件之间 AND。
/// 无法识别的“操作符”（如 10:30、http://x）当普通文本处理；值为空的操作符（正在输入）忽略。
/// 冒号可以是半角或全角。
/// </summary>
public static class SearchQueryParser
{
    private static readonly string[] DateFormats = ["yyyy-M-d", "yyyy/M/d", "yyyy.M.d"];

    public static SearchQuery Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return SearchQuery.Empty;
        }

        var terms = new List<SearchTerm>();
        bool? hasAttachment = null, unread = null, flagged = null;
        DateOnly? after = null, before = null;

        var i = 0;
        while (i < text.Length)
        {
            if (char.IsWhiteSpace(text[i]))
            {
                i++;
                continue;
            }

            if (text[i] == '"')
            {
                var phrase = ReadQuoted(text, ref i);
                AddTerm(terms, SearchField.Any, phrase, isPhrase: true);
                continue;
            }

            var start = i;
            var colon = -1;
            while (i < text.Length && !char.IsWhiteSpace(text[i]))
            {
                if (colon < 0 && IsColon(text[i]))
                {
                    colon = i;
                }

                if (colon >= 0 && i == colon + 1)
                {
                    break; // 冒号后面的值单独读取（可能带引号）
                }

                i++;
            }

            if (colon < 0)
            {
                // 普通词（i 已到词尾）
                AddTerm(terms, SearchField.Any, text[start..i], isPhrase: false);
                continue;
            }

            var key = text[start..colon].ToLowerInvariant();
            var op = key.Length == 0 ? null : MapKey(key);
            if (op is null)
            {
                // 不是已知操作符：把 “key:value” 整体当普通词
                i = colon + 1;
                while (i < text.Length && !char.IsWhiteSpace(text[i]))
                {
                    i++;
                }

                AddTerm(terms, SearchField.Any, text[start..i], isPhrase: false);
                continue;
            }

            i = colon + 1;
            string value;
            var quoted = false;
            if (i < text.Length && text[i] == '"')
            {
                value = ReadQuoted(text, ref i);
                quoted = true;
            }
            else
            {
                var vs = i;
                while (i < text.Length && !char.IsWhiteSpace(text[i]))
                {
                    i++;
                }

                value = text[vs..i];
            }

            value = value.Trim();
            if (value.Length == 0)
            {
                continue; // 正在输入的操作符
            }

            switch (op)
            {
                case "from":
                    AddTerm(terms, SearchField.From, value, quoted);
                    break;
                case "to":
                    AddTerm(terms, SearchField.To, value, quoted);
                    break;
                case "subject":
                    AddTerm(terms, SearchField.Subject, value, quoted);
                    break;
                case "has":
                    if (IsAttachmentWord(value))
                    {
                        hasAttachment = true;
                    }
                    else
                    {
                        AddTerm(terms, SearchField.Any, $"{text[start..colon]}:{value}", false);
                    }

                    break;
                case "is":
                    if (!TryApplyState(value, ref unread, ref flagged))
                    {
                        AddTerm(terms, SearchField.Any, $"{text[start..colon]}:{value}", false);
                    }

                    break;
                case "before":
                case "after":
                    if (TryParseDate(value, out var date))
                    {
                        if (op == "before")
                        {
                            before = date;
                        }
                        else
                        {
                            after = date;
                        }
                    }
                    else
                    {
                        AddTerm(terms, SearchField.Any, $"{text[start..colon]}:{value}", false);
                    }

                    break;
            }
        }

        return new SearchQuery(terms, hasAttachment, unread, flagged, after, before);
    }

    public static bool TryParseDate(string value, out DateOnly date)
        => DateOnly.TryParseExact(value.Trim(), DateFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out date);

    private static bool IsColon(char c) => c is ':' or '：';

    private static string ReadQuoted(string text, ref int i)
    {
        i++; // 跳过开头的引号
        var start = i;
        while (i < text.Length && text[i] != '"')
        {
            i++;
        }

        var value = text[start..i];
        if (i < text.Length)
        {
            i++; // 跳过结尾的引号
        }

        return value;
    }

    private static void AddTerm(List<SearchTerm> terms, SearchField field, string text, bool isPhrase)
    {
        var trimmed = text.Trim();
        if (trimmed.Length > 0)
        {
            terms.Add(new SearchTerm(field, trimmed, isPhrase));
        }
    }

    private static string? MapKey(string key) => key switch
    {
        "from" or "发件人" or "发件" => "from",
        "to" or "收件人" or "收件" => "to",
        "subject" or "主题" or "标题" => "subject",
        "has" or "含" or "包含" => "has",
        "is" or "状态" => "is",
        "before" or "早于" => "before",
        "after" or "晚于" => "after",
        _ => null
    };

    private static bool IsAttachmentWord(string v)
        => v.Equals("attachment", StringComparison.OrdinalIgnoreCase)
           || v.Equals("attachments", StringComparison.OrdinalIgnoreCase)
           || v == "附件";

    private static bool TryApplyState(string v, ref bool? unread, ref bool? flagged)
    {
        switch (v.ToLowerInvariant())
        {
            case "unread" or "未读":
                unread = true;
                return true;
            case "read" or "已读":
                unread = false;
                return true;
            case "flagged" or "starred" or "已标记" or "标记":
                flagged = true;
                return true;
            case "unflagged" or "未标记":
                flagged = false;
                return true;
            default:
                return false;
        }
    }
}
