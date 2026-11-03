namespace MailClient.ViewModels;

/// <summary>
/// 按键登记已打开的窗口（写信窗口按草稿键），保证同一份草稿只开一个：再次打开时激活已有窗口。
/// 与具体窗口类型无关，便于在 ViewModel 层测试。线程安全。
/// </summary>
public sealed class SingleInstanceTracker<T> where T : class
{
    private readonly object _gate = new();
    private readonly Dictionary<string, T> _items = new(StringComparer.Ordinal);

    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _items.Count;
            }
        }
    }

    public bool TryGet(string key, out T? item)
    {
        lock (_gate)
        {
            return _items.TryGetValue(key, out item);
        }
    }

    /// <summary>若该键已有窗口，就对它调用 <paramref name="activate"/> 并返回 true。</summary>
    public bool TryActivate(string key, Action<T> activate)
    {
        T? existing;
        lock (_gate)
        {
            if (!_items.TryGetValue(key, out existing))
            {
                return false;
            }
        }

        activate(existing);
        return true;
    }

    /// <summary>登记新窗口；键已被占用（并发打开）时返回 false，调用方应放弃新窗口。</summary>
    public bool Register(string key, T item)
    {
        lock (_gate)
        {
            return _items.TryAdd(key, item);
        }
    }

    /// <summary>窗口关闭时注销；只在登记的就是这个窗口时才移除。</summary>
    public void Unregister(string key, T item)
    {
        lock (_gate)
        {
            if (_items.TryGetValue(key, out var current) && ReferenceEquals(current, item))
            {
                _items.Remove(key);
            }
        }
    }

    public IReadOnlyList<T> Snapshot()
    {
        lock (_gate)
        {
            return _items.Values.ToList();
        }
    }
}
