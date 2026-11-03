using System.Diagnostics;

namespace MailClient.Auth;

/// <summary>打开系统默认浏览器（可在测试中替换）。</summary>
public interface IBrowserLauncher
{
    Task LaunchAsync(Uri uri, CancellationToken cancellationToken = default);
}

public sealed class SystemBrowserLauncher : IBrowserLauncher
{
    public Task LaunchAsync(Uri uri, CancellationToken cancellationToken = default)
    {
        Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true })?.Dispose();
        return Task.CompletedTask;
    }
}
