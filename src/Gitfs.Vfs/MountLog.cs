namespace Gitfs.Vfs;

/// <summary>Журнал событий тома — источник `.gitfs/log.txt` (спека §14):
/// битые объекты, неоднозначные префиксы, срабатывания правил экранирования,
/// обрезание списков, переоткрытие пакетов.
///
/// Живёт в памяти и ограничен по числу строк: журнал диагностики не имеет
/// права расти без предела на томе, который держат неделями. Старые строки
/// вытесняются, и об этом СКАЗАНО в самом файле — «молчаливое усечение
/// недопустимо» относится и к журналу тоже.
///
/// Правила «первое срабатывание на правило» (§14) реализованы через
/// <see cref="Once"/>: сто тысяч экранированных имён не должны превращать
/// журнал в одну повторяющуюся строку.</summary>
public sealed class MountLog
{
    public const int DefaultCapacity = 500;

    private readonly object _gate = new();
    private readonly Queue<string> _lines;
    private readonly HashSet<string> _seen = new(StringComparer.Ordinal);
    private readonly int _capacity;
    private long _dropped;

    public MountLog(int capacity = DefaultCapacity) =>
        (_capacity, _lines) = (Math.Max(1, capacity), new Queue<string>(Math.Max(1, capacity)));

    /// <summary>Сколько раз журнал записывал переоткрытие пакетов. Это и есть
    /// «деградация» из макета: том жив, но пакеты пересобрались под ним.</summary>
    public int Reopens { get; private set; }

    public void Add(string category, string message)
    {
        var line = $"{DateTimeOffset.Now:HH:mm:ss} {category} {message}";
        lock (_gate)
        {
            if (category == "pack-reopened") Reopens++;
            while (_lines.Count >= _capacity)
            {
                _lines.Dequeue();
                _dropped++;
            }
            _lines.Enqueue(line);
        }
    }

    /// <summary>Записывает событие один раз на ключ. Для правил экранирования
    /// и прочего, что срабатывает на каждой второй записи дерева.</summary>
    public void Once(string key, string category, string message)
    {
        lock (_gate)
        {
            if (!_seen.Add(key)) return;
        }
        Add(category, message);
    }

    /// <summary>Строки в хронологическом порядке, свежие внизу — как в любом
    /// журнале, который читают человеческими глазами.</summary>
    public IReadOnlyList<string> Lines
    {
        get { lock (_gate) return _lines.ToArray(); }
    }

    public long Dropped { get { lock (_gate) return _dropped; } }

    public string Render()
    {
        var sb = new System.Text.StringBuilder();
        long dropped;
        string[] lines;
        lock (_gate)
        {
            dropped = _dropped;
            lines = _lines.ToArray();
        }
        if (dropped > 0)
            sb.AppendLine($"… {dropped} earlier line(s) dropped; this log keeps the last {_capacity}");
        foreach (var line in lines) sb.AppendLine(line);
        if (lines.Length == 0) sb.AppendLine("(nothing has happened worth writing down)");
        return sb.ToString();
    }
}
