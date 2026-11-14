namespace MailClient.ViewModels;

/// <summary>
/// 只把集合更新投递到真正的 UI 调度器。
/// 测试宿主或其它不会泵消息的 SynchronizationContext 不能拿来 Post，
/// 否则列表刷新会一直占着加载锁，CI 上的等待就会超时。
/// </summary>
internal static class UiMarshal
{
    public static bool ShouldPost(SynchronizationContext? context)
    {
        if (context is null || ReferenceEquals(SynchronizationContext.Current, context))
        {
            return false;
        }

        var name = context.GetType().FullName ?? string.Empty;
        return name.Contains("Dispatcher", StringComparison.Ordinal);
    }
}
