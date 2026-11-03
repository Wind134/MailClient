using MailClient.ViewModels;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace MailClient_App.Controls;

/// <summary>
/// 附加属性：<c>controls:HighlightText.Value="{x:Bind SubjectHl}"</c>。同时设置 TextBlock 的文字和命中高亮区间
/// （文字与区间一起变，不会出现“新文字配旧区间”）。
/// </summary>
public static class HighlightText
{
    public static readonly DependencyProperty ValueProperty =
        DependencyProperty.RegisterAttached("Value", typeof(HighlightedText), typeof(HighlightText), new PropertyMetadata(null, OnValueChanged));

    private static readonly Color HitColor = Color.FromArgb(0xB0, 0xFF, 0xD5, 0x4F);

    public static HighlightedText? GetValue(DependencyObject element) => (HighlightedText?)element.GetValue(ValueProperty);

    public static void SetValue(DependencyObject element, HighlightedText? value) => element.SetValue(ValueProperty, value);

    private static void OnValueChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not TextBlock block)
        {
            return;
        }

        var value = e.NewValue as HighlightedText;
        block.TextHighlighters.Clear();
        block.Text = value?.Text ?? string.Empty;
        if (value is null || value.Spans.Count == 0)
        {
            return;
        }

        var highlighter = new TextHighlighter
        {
            Background = new SolidColorBrush(HitColor),
            Foreground = new SolidColorBrush(Colors.Black)
        };
        foreach (var span in value.Spans)
        {
            highlighter.Ranges.Add(new TextRange { StartIndex = span.Start, Length = span.Length });
        }

        block.TextHighlighters.Add(highlighter);
    }
}
