using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace MailClient.Setup;

/// <summary>卸载确认页：默认保留邮件数据，勾选后才一并删除。</summary>
public sealed partial class SetupUninstallView : UserControl
{
    public SetupUninstallView()
    {
        InitializeComponent();
    }

    public event Action<bool>? UninstallRequested;

    public event Action? CancelRequested;

    public void SetLocation(string text) => LocationText.Text = text;

    private void OnUninstall(object sender, RoutedEventArgs e) => UninstallRequested?.Invoke(DataBox.IsChecked == true);

    private void OnCancel(object sender, RoutedEventArgs e) => CancelRequested?.Invoke();
}
