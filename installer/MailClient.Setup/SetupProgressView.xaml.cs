using MailClient.Setup.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace MailClient.Setup;

/// <summary>进度页：脉动的图标、大号百分比、平滑推进的进度条。显示值向目标值缓动，所以即使文件复制是一跳一跳的，动画也是顺滑的。</summary>
public sealed partial class SetupProgressView : UserControl
{
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(16) };
    private double _target;
    private double _shown;

    public SetupProgressView(string title)
    {
        InitializeComponent();
        TitleText.Text = title;
        Loaded += (_, _) =>
        {
            PulseStory.Begin();
            _timer.Start();
        };
        Unloaded += (_, _) =>
        {
            PulseStory.Stop();
            _timer.Stop();
        };
        _timer.Tick += (_, _) =>
        {
            var diff = _target - _shown;
            _shown = Math.Abs(diff) < 0.05 ? _target : _shown + (diff * 0.12);
            Bar.Value = _shown;
            PercentText.Text = $"{(int)Math.Round(_shown)}%";
        };
    }

    public void Report(SetupProgress progress)
    {
        _target = Math.Clamp(progress.Fraction * 100, 0, 100);
        StatusText.Text = progress.Message;
        DetailText.Text = progress.Detail ?? string.Empty;
    }

    /// <summary>预览 / 截图用：直接定到某个进度，不做缓动。</summary>
    public void Jump(double percent)
    {
        _target = _shown = percent;
        Bar.Value = percent;
        PercentText.Text = $"{(int)percent}%";
    }
}
