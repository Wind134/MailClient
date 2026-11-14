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

    /// <summary>
    /// 投到 UI 调度器。回调里会把该上下文设为 Current，这样回调里启动的 async 方法
    /// 能在 await 之后回到 UI，而不是在线程池上接着改绑定。
    /// </summary>
    public static void Post(SynchronizationContext? context, Action action)
    {
        if (!ShouldPost(context))
        {
            action();
            return;
        }

        context!.Post(_ =>
        {
            var previous = SynchronizationContext.Current;
            if (!ReferenceEquals(previous, context))
            {
                SynchronizationContext.SetSynchronizationContext(context);
            }

            try
            {
                action();
            }
            finally
            {
                if (!ReferenceEquals(previous, context))
                {
                    SynchronizationContext.SetSynchronizationContext(previous);
                }
            }
        }, null);
    }

    /// <summary>在 UI 调度器上跑完再返回。没有可泵的调度器时就地执行，避免测试一直等。</summary>
    public static async Task RunAsync(SynchronizationContext? context, Action action)
    {
        if (!ShouldPost(context))
        {
            action();
            return;
        }

        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        context!.Post(_ =>
        {
            try
            {
                action();
                done.SetResult();
            }
            catch (Exception ex)
            {
                done.SetException(ex);
            }
        }, null);
        await done.Task.ConfigureAwait(false);
    }
}
