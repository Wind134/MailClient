using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI;
using Windows.UI.Text;

namespace MailClient_App;

/// <summary>x:Bind 函数绑定用的小工具（避免写 IValueConverter 资源）。</summary>
public static class Converters
{
    public static FontWeight Bold(bool unread) => unread ? FontWeights.SemiBold : FontWeights.Normal;

    public static Visibility Invert(bool value) => value ? Visibility.Collapsed : Visibility.Visible;

    public static Visibility VisibleIfNotEmpty(string? text)
        => string.IsNullOrWhiteSpace(text) ? Visibility.Collapsed : Visibility.Visible;

    public static InfoBarSeverity StatusSeverity(bool isError) => isError ? InfoBarSeverity.Error : InfoBarSeverity.Success;

    /// <summary>#RRGGBB → 画刷；无效 / 空时返回灰色。</summary>
    public static Brush Brush(string? hex)
    {
        var color = ParseColor(hex) ?? Color.FromArgb(255, 0x7A, 0x75, 0x74);
        return new SolidColorBrush(color);
    }

    public static Color? ParseColor(string? hex)
    {
        if (string.IsNullOrWhiteSpace(hex))
        {
            return null;
        }

        var text = hex.Trim().TrimStart('#');
        if (text.Length != 6 || !int.TryParse(text, System.Globalization.NumberStyles.HexNumber, null, out var rgb))
        {
            return null;
        }

        return Color.FromArgb(255, (byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
    }
}

public sealed class HexBrushConverter : Microsoft.UI.Xaml.Data.IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
        => Converters.Brush(value as string);

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => throw new NotSupportedException();
}
