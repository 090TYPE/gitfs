namespace Gitfs.Core.Objects;

/// <summary>Потокобезопасный LRU с бюджетом в единицах стоимости (байты,
/// штуки — задаёт делегат). Основа кэшей §7; счётчики хитов пойдут в
/// status.txt (§14). Блокировка общая — дёшево относительно инфляции
/// zlib, которую кэш и экономит.</summary>
public sealed class LruCache<TKey, TValue> where TKey : notnull
{
    private sealed class Entry
    {
        public required TKey Key;
        public required TValue Value;
        public required long Cost;
        public LinkedListNode<Entry>? Node;
    }

    private readonly object _gate = new();
    private readonly Dictionary<TKey, Entry> _map = new();
    private readonly LinkedList<Entry> _order = new(); // голова — самое свежее
    private readonly long _maxCost;
    private readonly Func<TValue, long> _cost;
    private long _used;
    private long _hits;
    private long _misses;

    public LruCache(long maxCost, Func<TValue, long> cost)
    {
        _maxCost = maxCost;
        _cost = cost;
    }

    public long Hits => Interlocked.Read(ref _hits);
    public long Misses => Interlocked.Read(ref _misses);

    public bool TryGet(TKey key, out TValue value)
    {
        lock (_gate)
        {
            if (_map.TryGetValue(key, out var entry))
            {
                _order.Remove(entry.Node!);
                entry.Node = _order.AddFirst(entry);
                value = entry.Value;
                Interlocked.Increment(ref _hits);
                return true;
            }
        }
        Interlocked.Increment(ref _misses);
        value = default!;
        return false;
    }

    public void Set(TKey key, TValue value)
    {
        var cost = _cost(value);
        if (cost > _maxCost) return; // один гигант не должен вымывать весь кэш (§7)
        lock (_gate)
        {
            if (_map.TryGetValue(key, out var existing))
            {
                _used -= existing.Cost;
                _order.Remove(existing.Node!);
                _map.Remove(key);
            }
            while (_used + cost > _maxCost && _order.Last is { } last)
            {
                _used -= last.Value.Cost;
                _map.Remove(last.Value.Key);
                _order.RemoveLast();
            }
            var entry = new Entry { Key = key, Value = value, Cost = cost };
            entry.Node = _order.AddFirst(entry);
            _map[key] = entry;
            _used += cost;
        }
    }
}
