using Gitfs.Core;
using Gitfs.Core.Objects;
using Gitfs.Core.Refs;
using Gitfs.Core.Walk;

namespace Gitfs.Vfs;

/// <summary>Иммутабельный снимок репозитория (спека §8): ссылки, объекты,
/// обходчики — одним пакетом. Операция читает ссылку на снапшот один раз
/// и работает с ним до конца; смена эпохи в середине операции не наблюдается.</summary>
public sealed class RepoSnapshot : IDisposable
{
    /// <summary>Счётчик ссылок: 1 у создателя (менеджера или using-владельца),
    /// +1 на каждую операцию через Acquire. Освобождает mmap-хендлы, когда
    /// последний читатель отпустил вытесненный снапшот — закрывает долг M2 №1
    /// («вытесненный снапшот не диспозится, читатели могут держать ссылку»).</summary>
    private int _refs = 1;

    public string GitDir { get; }
    public RefStore Refs { get; }
    public ObjectReader Objects { get; }
    public TreeWalker Trees { get; }
    public RevWalker Revs { get; }

    /// <summary>Кэши, привязанные к снапшоту (перф-долг M2 п.4). Снапшот
    /// иммутабелен, поэтому инвалидация не нужна вовсе: смена эпохи создаёт
    /// новый снапшот с пустыми кэшами.</summary>
    /// <summary>Бюджеты — в БАЙТАХ (спека §7), а не в записях: одно дерево
    /// может весить мегабайты, и счёт по штукам не ограничивает память.</summary>
    public LruCache<ObjectId, TreeObject> TreeCache { get; } =
        new(64L << 20, t => 64 + t.Entries.Count * 64L);
    /// <summary>ref-имя → вершина (null — битая ветка). Листинг корня вьюхи
    /// иначе парсит коммит на каждую ветку при каждом перечислении.</summary>
    public System.Collections.Concurrent.ConcurrentDictionary<string, CommitObject?> TipCache { get; } = new();
    /// <summary>OID дерева → отображаемые имена. Чистая функция от
    /// иммутабельного объекта при фиксированной политике имён.</summary>
    /// <summary>Ключ включает тег политики: один и тот же OID дерева даёт
    /// разные отображаемые имена под разными политиками (найдено тестом).</summary>
    public LruCache<(ObjectId Tree, string Policy), IReadOnlyList<DisplayName>> ListingCache { get; } =
        new(32L << 20, l => 64 + l.Sum(d => (long)(d.Display.Length + d.GitName.Length) * 2 + 48));

    /// <summary>История пути (history/) — самая дорогая производная: без кэша
    /// каждый вызов стоит полный обход истории (ревью M4).</summary>
    public LruCache<string, Views.HistoryView.PathHistory?> HistoryCache { get; } =
        new(8192, _ => 1);

    /// <summary>Индекс дней (dates/) — тоже полный обход на каждый вызов.</summary>
    public LruCache<string, IReadOnlyDictionary<string, CommitObject>> DateIndexCache { get; } =
        new(4, _ => 1);

    /// <summary>Разрешение пути в дереве: (дерево, путь+политика) → запись.
    /// Чистая функция от иммутабельных данных, инвалидация не нужна.</summary>
    public LruCache<(ObjectId Tree, string Path), CachedEntry> PathCache { get; } =
        new(65536, _ => 1);

    private RepoSnapshot(string gitDir, RefStore refs, ObjectReader objects)
    {
        GitDir = gitDir;
        Refs = refs;
        Objects = objects;
        Trees = new TreeWalker(objects);
        // commit-graph необязателен: нет — обход идёт через чтение объектов
        Revs = new RevWalker(objects, Core.Accel.CommitGraph.TryLoad(gitDir));
    }

    public static RepoSnapshot Load(string gitDir) =>
        new(gitDir, RefStore.Load(gitDir), new ObjectReader(gitDir));

    /// <summary>false — снапшот уже освобождён; вызывающий обязан перечитать
    /// Current и повторить (гонка с подменой эпохи).</summary>
    internal bool TryAddRef()
    {
        var current = Volatile.Read(ref _refs);
        while (current > 0)
        {
            var prior = Interlocked.CompareExchange(ref _refs, current + 1, current);
            if (prior == current) return true;
            current = prior;
        }
        return false;
    }

    internal void Release()
    {
        if (Interlocked.Decrement(ref _refs) == 0) Objects.Dispose();
    }

    /// <summary>Снимает ссылку создателя. Снапшот, полученный из
    /// SnapshotManager.Current, диспозить НЕЛЬЗЯ — он общий; берите
    /// аренду через Acquire().</summary>
    public void Dispose() => Release();
}

/// <summary>Аренда снапшота на время операции: пока жива, снапшот не будет
/// освобождён, даже если эпоха сменилась и менеджер уже отдал новый.</summary>
public readonly struct SnapshotLease : IDisposable
{
    public RepoSnapshot Snapshot { get; }
    internal SnapshotLease(RepoSnapshot snapshot) => Snapshot = snapshot;
    public void Dispose() => Snapshot.Release();
}

/// <summary>Держатель текущего снапшота. Смена эпохи: mtime-подпись
/// (HEAD, packed-refs, refs/ рекурсивно), проверка не чаще раза в секунду,
/// публикация — одна волатильная запись ссылки.
/// Долг (план M2): вытесненные снапшоты не диспозятся — читатели могут
/// держать ссылку; refcount придёт с адаптером M3, mmap-хендлы пока
/// освобождает GC.</summary>
public sealed class SnapshotManager
{
    private readonly object _gate = new();
    private readonly string _gitDir;
    private readonly long _throttleMs;

    private RepoSnapshot _current;
    private string _signature;
    private long _lastCheckTicks;

    public SnapshotManager(string gitDir, TimeSpan? throttle = null)
    {
        _gitDir = gitDir;
        _throttleMs = (long)(throttle ?? TimeSpan.FromSeconds(1)).TotalMilliseconds;
        _current = RepoSnapshot.Load(gitDir);
        _signature = ComputeSignature(gitDir);
        _lastCheckTicks = Environment.TickCount64;
    }

    /// <summary>Текущий снапшот. Принадлежит менеджеру: вызывающий НЕ должен
    /// его Dispose — им конкурентно пользуются другие операции.</summary>
    public RepoSnapshot Current => Volatile.Read(ref _current);

    /// <summary>Проверяет эпоху и при изменении подменяет снапшот.
    /// force обходит троттлинг (для тестов и явного unmount/remount).
    /// Медленный путь сериализован (ревью M2): без lock пара
    /// (подпись, снапшот) теряет когерентность — перекрёстные записи двух
    /// потоков могут «заморозить» устаревший снапшот с новой подписью.</summary>
    public RepoSnapshot Refresh(bool force = false)
    {
        if (!force && Environment.TickCount64 - Interlocked.Read(ref _lastCheckTicks) < _throttleMs)
            return Current;
        lock (_gate)
        {
            if (!force && Environment.TickCount64 - _lastCheckTicks < _throttleMs)
                return Current; // проигравший гонку за окно уходит с текущим
            _lastCheckTicks = Environment.TickCount64;

            var signature = ComputeSignature(_gitDir);
            if (signature == _signature) return Current;

            var fresh = RepoSnapshot.Load(_gitDir);
            var old = _current;
            _signature = signature;
            Volatile.Write(ref _current, fresh); // публикация = одна запись ссылки
            old.Release();  // ссылка менеджера; живые аренды додержат снапшот сами
            return fresh;
        }
    }

    /// <summary>Аренда на время одной операции адаптера (спека §8: операция
    /// читает ссылку один раз и работает с ней до конца).</summary>
    public SnapshotLease Acquire()
    {
        while (true)
        {
            var snapshot = Refresh();
            if (snapshot.TryAddRef()) return new SnapshotLease(snapshot);
            // проиграли гонку с подменой — берём уже опубликованный новый
        }
    }

    /// <summary>Подпись эпохи — СОСТАВ файлов ссылок с их mtime, не max
    /// (ревью M2: max слеп к удалению ветки с не-максимальным mtime).
    /// *.lock исключены — транзиентные файлы git во время записи ссылки.
    /// Известное ограничение (принято §7): гранулярность mtime ФС; два
    /// изменения одной ссылки в один тик часов неразличимы до следующей записи.</summary>
    private static string ComputeSignature(string gitDir)
    {
        var sb = new System.Text.StringBuilder();
        void Stamp(string label, string path)
        {
            if (File.Exists(path))
                sb.Append(label).Append(':').Append(File.GetLastWriteTimeUtc(path).Ticks).Append(';');
        }
        Stamp("HEAD", Path.Combine(gitDir, "HEAD"));
        Stamp("packed", Path.Combine(gitDir, "packed-refs"));

        var refsDir = Path.Combine(gitDir, "refs");
        if (Directory.Exists(refsDir))
        {
            try
            {
                var entries = new List<string>();
                foreach (var f in Directory.EnumerateFiles(refsDir, "*", SearchOption.AllDirectories))
                {
                    if (f.EndsWith(".lock", StringComparison.OrdinalIgnoreCase)) continue;
                    entries.Add($"{Path.GetRelativePath(refsDir, f)}:{File.GetLastWriteTimeUtc(f).Ticks}");
                }
                entries.Sort(StringComparer.Ordinal);
                foreach (var e in entries) sb.Append(e).Append(';');
            }
            // каталог/файл исчез посреди обхода (git branch -d чистит пустые
            // каталоги): считаем эпоху изменившейся — следующий тик перепроверит
            catch (DirectoryNotFoundException) { sb.Append("!enum:").Append(Environment.TickCount64); }
            catch (FileNotFoundException) { sb.Append("!enum:").Append(Environment.TickCount64); }
            catch (IOException) { sb.Append("!enum:").Append(Environment.TickCount64); }
        }
        return sb.ToString();
    }
}
