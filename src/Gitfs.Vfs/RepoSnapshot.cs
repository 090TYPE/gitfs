using Gitfs.Core.Objects;
using Gitfs.Core.Refs;
using Gitfs.Core.Walk;

namespace Gitfs.Vfs;

/// <summary>Иммутабельный снимок репозитория (спека §8): ссылки, объекты,
/// обходчики — одним пакетом. Операция читает ссылку на снапшот один раз
/// и работает с ним до конца; смена эпохи в середине операции не наблюдается.</summary>
public sealed class RepoSnapshot : IDisposable
{
    public string GitDir { get; }
    public RefStore Refs { get; }
    public ObjectReader Objects { get; }
    public TreeWalker Trees { get; }
    public RevWalker Revs { get; }

    private RepoSnapshot(string gitDir, RefStore refs, ObjectReader objects)
    {
        GitDir = gitDir;
        Refs = refs;
        Objects = objects;
        Trees = new TreeWalker(objects);
        Revs = new RevWalker(objects);
    }

    public static RepoSnapshot Load(string gitDir) =>
        new(gitDir, RefStore.Load(gitDir), new ObjectReader(gitDir));

    public void Dispose() => Objects.Dispose();
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
            _signature = signature;
            Volatile.Write(ref _current, fresh); // публикация = одна запись ссылки
            return fresh;
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
