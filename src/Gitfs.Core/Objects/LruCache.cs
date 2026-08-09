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
    private readonly long _maxItemCost;
    private readonly Func<TValue, long> _cost;
    private long _used;
    private long _hits;
    private long _misses;

    /// <param name="maxItemCost">Потолок для ОДНОЙ записи. По умолчанию равен
    /// всему бюджету — то есть не ограничивает ничего сверх него. Меньшее
    /// значение нужно там, где объект бывает во много раз крупнее типичного:
    /// один блоб на 200 МБ формально влезает в кэш на 512 МБ, но вытесняет
    /// оттуда тысячи деревьев ради одного чтения, которое едва ли повторится.</param>
    public LruCache(long maxCost, Func<TValue, long> cost, long? maxItemCost = null)
    {
        _maxCost = maxCost;
        _maxItemCost = Math.Min(maxItemCost ?? maxCost, maxCost);
        _cost = cost;
    }

    public long Hits => Interlocked.Read(ref _hits);
    public long Misses => Interlocked.Read(ref _misses);

    /// <summary>Сколько байт (единиц стоимости) кэш держит прямо сейчас.
    /// Нужно проверкам и панели тома: «96 МБ» в диалоге обязано быть тем же
    /// числом, что и здесь.</summary>
    public long Used { get { lock (_gate) return _used; } }

    public long MaxCost => _maxCost;
    public long MaxItemCost => _maxItemCost;

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
        if (cost > _maxItemCost) return; // один гигант не должен вымывать весь кэш (§7)
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
