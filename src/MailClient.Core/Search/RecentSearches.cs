namespace MailClient.Core.Search;

/// <summary>最近的搜索（搜索框建议用）。</summary>
public interface IRecentSearchStore
{
    IReadOnlyList<string> GetAll();

    /// <summary>记录一次提交的搜索：去重（忽略大小写）、最新在前、最多 <see cref="Max"/> 条。</summary>
    void Add(string query);

    void Clear();
}

public static class RecentSearchList
{
    public const int Max = 10;

    public static List<string> Add(IEnumerable<string> existing, string query)
    {
        var q = (query ?? string.Empty).Trim();
        var list = existing.ToList();
        if (q.Length == 0)
        {
            return list;
        }

        list.RemoveAll(x => string.Equals(x, q, StringComparison.OrdinalIgnoreCase));
        list.Insert(0, q);
        return list.Take(Max).ToList();
    }
}

public sealed class MemoryRecentSearchStore : IRecentSearchStore
{
    private readonly object _lock = new();
    private List<string> _items = new();

    public IReadOnlyList<string> GetAll()
    {
        lock (_lock)
        {
            return _items.ToList();
        }
    }

    public void Add(string query)
    {
        lock (_lock)
        {
            _items = RecentSearchList.Add(_items, query);
        }
    }

    public void Clear()
    {
        lock (_lock)
        {
            _items.Clear();
        }
    }
}

/// <summary>把最近的搜索存进 <see cref="AppSettings.RecentSearches"/>（settings.json）。</summary>
public sealed class SettingsRecentSearchStore : IRecentSearchStore
{
    private readonly AppSettingsStore _settings;

    public SettingsRecentSearchStore(AppSettingsStore settings) => _settings = settings;

    public IReadOnlyList<string> GetAll() => _settings.Snapshot().RecentSearches;

    public void Add(string query) => _settings.Update(s => s.RecentSearches = RecentSearchList.Add(s.RecentSearches, query));

    public void Clear() => _settings.Update(s => s.RecentSearches = new List<string>());
}
