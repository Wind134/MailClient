using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace MailClient_App.Controls;

/// <summary>
/// 极简列分隔条（WinUI 3 自带控件库没有 GridSplitter）：拖动时调整 <see cref="Target"/> 列宽，
/// 并受该列 MinWidth / MaxWidth 约束。双击恢复默认宽度。
/// </summary>
public sealed class ColumnSplitter : Grid
{
    private double _startX;
    private double _startWidth;
    private bool _dragging;

    public ColumnSplitter()
    {
        Width = 8;
        Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        ProtectedCursor = InputSystemCursor.Create(InputSystemCursorShape.SizeWestEast);
        // 中间一条 1px 细线，悬停/拖动时更明显
        Children.Add(new Microsoft.UI.Xaml.Shapes.Rectangle
        {
            Width = 1,
            HorizontalAlignment = HorizontalAlignment.Center,
            Fill = new SolidColorBrush(Microsoft.UI.Colors.Gray),
            Opacity = 0.0
        });

        PointerEntered += (_, _) => SetLineOpacity(1.0);
        PointerExited += (_, _) => { if (!_dragging) SetLineOpacity(0.0); };
        PointerPressed += OnPressed;
        PointerMoved += OnMoved;
        PointerReleased += OnReleased;
        PointerCanceled += OnReleased;
        PointerCaptureLost += OnReleased;
        DoubleTapped += (_, _) =>
        {
            if (Target is not null)
            {
                Target.Width = new GridLength(DefaultWidth);
            }
        };
    }

    public ColumnDefinition? Target { get; set; }

    public double DefaultWidth { get; set; } = 380;

    private void SetLineOpacity(double value)
    {
        if (Children.Count > 0)
        {
            Children[0].Opacity = value;
        }
    }

    private void OnPressed(object sender, PointerRoutedEventArgs e)
    {
        if (Target is null)
        {
            return;
        }

        _dragging = true;
        _startX = e.GetCurrentPoint(null).Position.X;
        _startWidth = Target.ActualWidth;
        CapturePointer(e.Pointer);
        e.Handled = true;
    }

    private void OnMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_dragging || Target is null)
        {
            return;
        }

        var delta = e.GetCurrentPoint(null).Position.X - _startX;
        var width = Math.Clamp(_startWidth + delta, Target.MinWidth, Target.MaxWidth);
        Target.Width = new GridLength(width);
        e.Handled = true;
    }

    private void OnReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!_dragging)
        {
            return;
        }

        _dragging = false;
        ReleasePointerCaptures();
        SetLineOpacity(0.0);
    }
}
