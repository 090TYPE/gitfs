using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Gitfs.Vfs.Overlay;

public enum OverlayKind
{
    /// <summary>Файл перекрыт: содержимое берётся из песочницы.</summary>
    File = 1,
    /// <summary>Надгробие: узел удалён из выдачи, объект в репозитории цел.</summary>
    Tombstone = 2,
}

public sealed record OverlayEntry(string VirtualPath, OverlayKind Kind, string StorageName,
    DateTimeOffset When);

/// <summary>Copy-on-write песочница (спека §10). Запись разрешена, но уходит
/// сюда и НИКОГДА не попадает в репозиторий: история физически неизменна,
/// а снаружи том ведёт себя как обычный — иначе Word, Excel и часть IDE не
/// откроют файл, потому что пишут lock-файлы рядом с ним.
///
/// Имя файла в песочнице — hex SHA-256 от виртуального пути, а не сам путь:
/// путь может превышать лимит длины и содержать экранированные
/// последовательности. Сопоставление лежит в manifest.jsonl.</summary>
public sealed class OverlayStore : IDisposable
{
    private const string ManifestName = "manifest.jsonl";

    /// <summary>Пока песочница жива, её владелец держит этот файл открытым
    /// без права совместного доступа. Это и есть доказательство жизни:
    /// опознание владельца по номеру процесса ошибается в обе стороны —
    /// переиспользованный PID делает мусор неудаляемым навсегда, а чужой
    /// процесс с тем же номером выдаёт живую песочницу за брошенную.</summary>
    private const string LockName = ".lock";

    private readonly object _gate = new();
    private readonly Dictionary<string, OverlayEntry> _entries = new(StringComparer.OrdinalIgnoreCase);
    private readonly string _root;
    private readonly bool _keepOnDispose;
    private readonly FileStream _lock;
    private bool _disposed;

    public string Root => _root;
    public string MountId { get; }

    private OverlayStore(string root, string mountId, bool keepOnDispose, FileStream ownerLock)
    {
        _root = root;
        MountId = mountId;
        _keepOnDispose = keepOnDispose;
        _lock = ownerLock;
    }

    /// <summary>mount-id включает идентификатор процесса и момент старта,
    /// поэтому list и doctor распознают осиротевшие после аварии каталоги.</summary>
    public static OverlayStore Create(string? baseDirectory = null, bool keepOnDispose = false,
        DateTimeOffset? now = null)
    {
        var stamp = (now ?? DateTimeOffset.UtcNow).ToUnixTimeSeconds();
        var baseId = $"{Environment.ProcessId}-{stamp:x}";
        var parent = baseDirectory ?? DefaultRoot();

        // Два монтирования одного процесса в одну секунду дают одинаковый
        // mount-id: раньше они молча делили каталог, и первый же Dispose
        // сносил песочницу второго вместе с несохранёнными записями.
        for (var attempt = 1; ; attempt++)
        {
            var mountId = attempt == 1 ? baseId : $"{baseId}-{attempt}";
            var root = Path.Combine(parent, mountId);
            Directory.CreateDirectory(root);
            RestrictToOwner(root);

            FileStream ownerLock;
            try
            {
                ownerLock = new FileStream(Path.Combine(root, LockName), FileMode.OpenOrCreate,
                    FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException) when (attempt < 64)
            {
                continue; // каталогом владеет кто-то живой — берём соседний
            }

            var store = new OverlayStore(root, mountId, keepOnDispose, ownerLock);
            store.LoadManifest();
            return store;
        }
    }

    public static string DefaultRoot()
    {
        var basePath = OperatingSystem.IsWindows()
            ? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)
            : Environment.GetEnvironmentVariable("XDG_STATE_HOME")
              ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                  ".local", "state");
        return Path.Combine(basePath, "gitfs", "overlay");
    }

    // ---------- чтение состояния ----------

    public bool TryGet(string virtualPath, out OverlayEntry entry)
    {
        lock (_gate) return _entries.TryGetValue(Normalize(virtualPath), out entry!);
    }

    public bool IsHidden(string virtualPath) =>
        TryGet(virtualPath, out var e) && e.Kind == OverlayKind.Tombstone;

    /// <summary>Путь к файлу песочницы, если он перекрывает виртуальный путь.</summary>
    public string? TryGetFilePath(string virtualPath) =>
        TryGet(virtualPath, out var e) && e.Kind == OverlayKind.File
            ? Path.Combine(_root, e.StorageName)
            : null;

    public IReadOnlyList<OverlayEntry> Entries
    {
        get { lock (_gate) return _entries.Values.ToList(); }
    }

    /// <summary>Прямые потомки виртуальной директории — нужно, чтобы наложить
    /// созданные в песочнице файлы на листинг дерева.</summary>
    public IEnumerable<(string Name, OverlayEntry Entry)> ChildrenOf(string directoryPath)
    {
        var prefix = Normalize(directoryPath);
        if (prefix.Length > 0) prefix += "/";
        foreach (var entry in Entries)
        {
            if (!entry.VirtualPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
            var rest = entry.VirtualPath[prefix.Length..];
            if (rest.Length == 0 || rest.Contains('/')) continue; // не прямой потомок
            yield return (rest, entry);
        }
    }

    public long TotalBytes
    {
        get
        {
            long total = 0;
            foreach (var entry in Entries)
            {
                if (entry.Kind != OverlayKind.File) continue;
                var path = Path.Combine(_root, entry.StorageName);
                if (File.Exists(path)) total += new FileInfo(path).Length;
            }
            return total;
        }
    }

    // ---------- запись ----------

    /// <summary>Готовит файл песочницы к записи: если путь ещё не перекрыт,
    /// содержимое сначала копируется из репозитория (это и есть
    /// copy-on-write), иначе продолжается уже начатое.</summary>
    public string PrepareForWrite(string virtualPath, Func<Stream>? seed)
    {
        var key = Normalize(virtualPath);
        lock (_gate)
        {
            if (_entries.TryGetValue(key, out var existing) && existing.Kind == OverlayKind.File)
                return Path.Combine(_root, existing.StorageName);

            var storage = StorageNameFor(key);
            var target = Path.Combine(_root, storage);
            if (seed is not null)
            {
                using var source = seed();
                using var file = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None);
                source.CopyTo(file);
            }
            else if (!File.Exists(target))
            {
                File.WriteAllBytes(target, []);
            }
            Record(new OverlayEntry(key, OverlayKind.File, storage, DateTimeOffset.UtcNow));
            return target;
        }
    }

    /// <summary>Надгробие: узел исчезает из выдачи, объект в репозитории цел.</summary>
    public void Hide(string virtualPath)
    {
        var key = Normalize(virtualPath);
        lock (_gate)
        {
            if (_entries.TryGetValue(key, out var existing) && existing.Kind == OverlayKind.File)
            {
                var path = Path.Combine(_root, existing.StorageName);
                if (File.Exists(path)) File.Delete(path);
            }
            Record(new OverlayEntry(key, OverlayKind.Tombstone, "", DateTimeOffset.UtcNow));
        }
    }

    // ---------- служебное ----------

    private void Record(OverlayEntry entry)
    {
        _entries[entry.VirtualPath] = entry;
        File.AppendAllText(Path.Combine(_root, ManifestName),
            JsonSerializer.Serialize(entry) + Environment.NewLine, Encoding.UTF8);
    }

    private void LoadManifest()
    {
        var manifest = Path.Combine(_root, ManifestName);
        if (!File.Exists(manifest)) return;
        foreach (var line in File.ReadLines(manifest))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            OverlayEntry? entry;
            try { entry = JsonSerializer.Deserialize<OverlayEntry>(line); }
            catch (JsonException) { continue; } // битая строка не рушит восстановление
            if (entry is not null) _entries[entry.VirtualPath] = entry;
        }
    }

    private static string StorageNameFor(string virtualPath) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(virtualPath))).ToLowerInvariant();

    internal static string Normalize(string virtualPath) =>
        virtualPath.Replace('\\', '/').Trim('/');

    private static void RestrictToOwner(string path)
    {
        if (OperatingSystem.IsWindows()) return; // наследует ACL профиля
        try
        {
            File.SetUnixFileMode(path,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    /// <summary>Каталог песочницы, которым никто не владеет: замок либо
    /// снялся вместе с упавшим процессом, либо его не было вовсе.
    /// Свежие каталоги без замка не трогаем — между созданием каталога и
    /// взятием замка есть щель, и попасть в неё чужой уборкой нельзя.</summary>
    private static readonly TimeSpan OrphanGrace = TimeSpan.FromMinutes(1);

    /// <summary>Осиротевшие каталоги песочницы: их показывает doctor.</summary>
    public static IReadOnlyList<string> FindOrphans(string? baseDirectory = null)
    {
        var root = baseDirectory ?? DefaultRoot();
        if (!Directory.Exists(root)) return [];
        var orphans = new List<string>();
        foreach (var dir in Directory.GetDirectories(root))
        {
            if (IsOrphan(dir)) orphans.Add(dir);
        }
        orphans.Sort(StringComparer.OrdinalIgnoreCase);
        return orphans;
    }

    private static bool IsOrphan(string dir)
    {
        var lockPath = Path.Combine(dir, LockName);
        if (!File.Exists(lockPath))
            return DateTime.UtcNow - Directory.GetCreationTimeUtc(dir) > OrphanGrace;
        try
        {
            // взяли замок — значит владельца нет; сразу отпускаем
            using var probe = new FileStream(lockPath, FileMode.Open, FileAccess.ReadWrite,
                FileShare.None);
            return true;
        }
        catch (IOException) { return false; }              // держит живой процесс
        catch (UnauthorizedAccessException) { return false; }
    }

    /// <summary>Удаляет брошенные песочницы. Возвращает удалённые каталоги и
    /// те, что удалить не вышло — молча «почистить» и оставить мусор нельзя,
    /// иначе doctor будет вечно советовать команду, которая уже отработала.</summary>
    public static (IReadOnlyList<string> Removed, IReadOnlyList<string> Failed) PurgeOrphans(
        string? baseDirectory = null)
    {
        var removed = new List<string>();
        var failed = new List<string>();
        foreach (var dir in FindOrphans(baseDirectory))
        {
            try
            {
                Directory.Delete(dir, recursive: true);
                removed.Add(dir);
            }
            catch (IOException) { failed.Add(dir); }
            catch (UnauthorizedAccessException) { failed.Add(dir); }
        }
        return (removed, failed);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _lock.Dispose(); // до удаления: собственный замок держит каталог занятым
        if (_keepOnDispose) return;
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
