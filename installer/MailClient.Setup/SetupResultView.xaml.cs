using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace MailClient.Setup;

/// <summary>结果页（安装完成 / 卸载完成 / 失败）：弹出的圆形徽标 + 标题 + 说明 + 可选的“立即运行”。</summary>
public sealed partial class SetupResultView : UserControl
{
    public SetupResultView()
    {
        InitializeComponent();
        Loaded += (_, _) => PopStory.Begin();
    }

    public event Action<bool>? PrimaryClicked;

    public event Action? SecondaryClicked;

    public void ConfigureSuccess(string title, string message, string primaryText, string? optionText = null)
    {
        TitleText.Text = title;
        MessageText.Text = message;
        PrimaryButton.Content = primaryText;
        if (optionText is not null)
        {
            OptionBox.Content = optionText;
            OptionBox.Visibility = Visibility.Visible;
        }
    }

    public void ConfigureFailure(string title, string message, string primaryText, string? secondaryText = null)
    {
        Badge.Fill = new SolidColorBrush(Windows.UI.Color.FromArgb(0xFF, 0xD1, 0x34, 0x38));
        BadgeIcon.Glyph = "\uE711";
        TitleText.Text = title;
        MessageText.Text = message;
        PrimaryButton.Content = primaryText;
        if (secondaryText is not null)
        {
            SecondaryButton.Content = secondaryText;
            SecondaryButton.Visibility = Visibility.Visible;
        }
    }

    public void SetHint(string message)
    {
        Hint.Message = message;
        Hint.IsOpen = true;
    }

    private void OnPrimary(object sender, RoutedEventArgs e) => PrimaryClicked?.Invoke(OptionBox.IsChecked == true);

    private void OnSecondary(object sender, RoutedEventArgs e) => SecondaryClicked?.Invoke();
}
