using System.Globalization;

namespace MailClient.Core;

/// <summary>#RRGGBB 颜色的纯解析。UI 层再把它转成具体框架的颜色类型。</summary>
public static class HexColor
{
    /// <summary>解析 <c>#RRGGBB</c>（<c>#</c> 可省略）。格式不符时返回 false。</summary>
    public static bool TryParse(string? hex, out byte r, out byte g, out byte b)
    {
        r = 0;
        g = 0;
        b = 0;

        if (string.IsNullOrWhiteSpace(hex))
        {
            return false;
        }

        var text = hex.Trim();
        if (text.StartsWith('#'))
        {
            text = text[1..];
        }
        if (text.Length != 6 ||
            !int.TryParse(text, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var rgb))
        {
            return false;
        }

        r = (byte)(rgb >> 16);
        g = (byte)(rgb >> 8);
        b = (byte)rgb;
        return true;
    }
}
