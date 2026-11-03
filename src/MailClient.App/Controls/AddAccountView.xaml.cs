using MailClient.ViewModels.AddAccount;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace MailClient_App.Controls;

/// <summary>
/// “添加账号”向导的视图。所有逻辑都在 <see cref="AddAccountViewModel"/> 里；
/// 各邮箱的分步设置引导是内置的（<see cref="ProviderGuide"/>），其中的链接由 HyperlinkButton 用系统默认浏览器打开。
/// </summary>
public sealed partial class AddAccountView : UserControl
{
    public AddAccountView(AddAccountViewModel viewModel)
    {
        ViewModel = viewModel;
        InitializeComponent();
    }

    public AddAccountViewModel ViewModel { get; }

    public Visibility IsImapForm(bool isAuthCode, bool isOtherImap)
        => isAuthCode || isOtherImap ? Visibility.Visible : Visibility.Collapsed;
}