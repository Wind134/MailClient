namespace MailClient.ViewModels;

/// <summary>写信窗口的最小抽象，使“同一份草稿只开一个窗口”的逻辑可以在 ViewModel 层测试。</summary>
public interface IComposeWindow
{
    ComposeViewModel ViewModel { get; }

    /// <summary>把窗口带到前台。</summary>
    void Activate();

    void Close();

    /// <summary>窗口关闭时回调（用于注销单实例键）。</summary>
    void OnClosed(Action callback);
}
