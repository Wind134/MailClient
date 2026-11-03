using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Windows.UI;

namespace MailClient.Setup;

/// <summary>左侧渐变面板：图标、名称、版本号和三步进度（准备 → 安装 → 完成）。</summary>
public sealed partial class SetupSidePanel : UserControl
{
    private readonly List<(Border Dot, TextBlock Glyph, TextBlock Label)> _steps = new();

    public SetupSidePanel()
    {
        InitializeComponent();
    }

    public void Configure(string version, params string[] stepNames)
    {
        VersionText.Text = "版本 " + version;
        StepsPanel.Children.Clear();
        _steps.Clear();
        for (var i = 0; i < stepNames.Length; i++)
        {
            var glyph = new TextBlock
            {
                Text = (i + 1).ToString(),
                FontSize = 12,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = new SolidColorBrush(Colors.White),
            };
            var dot = new Border
            {
                Width = 24,
                Height = 24,
                CornerRadius = new CornerRadius(12),
                BorderThickness = new Thickness(1.5),
                Child = glyph,
            };
            var label = new TextBlock
            {
                Text = stepNames[i],
                FontSize = 14,
                VerticalAlignment = VerticalAlignment.Center,
            };
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
            row.Children.Add(dot);
            row.Children.Add(label);
            StepsPanel.Children.Add(row);
            _steps.Add((dot, glyph, label));
        }

        SetActive(0);
    }

    /// <summary>active 之前的步骤显示 ✓，active 高亮，之后的变淡。</summary>
    public void SetActive(int active)
    {
        for (var i = 0; i < _steps.Count; i++)
        {
            var (dot, glyph, label) = _steps[i];
            if (i < active)
            {
                dot.Background = new SolidColorBrush(Color.FromArgb(0x55, 255, 255, 255));
                dot.BorderBrush = new SolidColorBrush(Color.FromArgb(0x00, 255, 255, 255));
                glyph.Text = "✓";
                label.Foreground = new SolidColorBrush(Color.FromArgb(0xCC, 255, 255, 255));
                label.FontWeight = Microsoft.UI.Text.FontWeights.Normal;
            }
            else if (i == active)
            {
                dot.Background = new SolidColorBrush(Color.FromArgb(0x40, 255, 255, 255));
                dot.BorderBrush = new SolidColorBrush(Colors.White);
                glyph.Text = (i + 1).ToString();
                label.Foreground = new SolidColorBrush(Colors.White);
                label.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold;
            }
            else
            {
                dot.Background = new SolidColorBrush(Color.FromArgb(0x00, 255, 255, 255));
                dot.BorderBrush = new SolidColorBrush(Color.FromArgb(0x66, 255, 255, 255));
                glyph.Text = (i + 1).ToString();
                label.Foreground = new SolidColorBrush(Color.FromArgb(0x99, 255, 255, 255));
                label.FontWeight = Microsoft.UI.Text.FontWeights.Normal;
            }
        }
    }

    private void OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        // 装饰用的半透明圆会越出面板，裁掉
        Root.Clip = new RectangleGeometry { Rect = new Rect(0, 0, e.NewSize.Width, e.NewSize.Height) };
    }
}
