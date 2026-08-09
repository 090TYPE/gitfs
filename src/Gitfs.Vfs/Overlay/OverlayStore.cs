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

    /// <summary>Как песочница сравнивает пути. Своего мнения у неё быть не
    /// должно: слой вьюх уже решил этот вопрос политикой имён, и под Posix
    /// `Makefile` и `makefile` — разные файлы. Пока здесь стоял жёсткий
    /// OrdinalIgnoreCase, запись в один из них подменяла другой, а удаление
    /// одного прятало оба.</summary>
    private readonly StringComparer _paths;
    private readonly StringComparison _pathComparison;
    private readonly Dictionary<string, OverlayEntry> _entries;

    /// <summary>Сравнение путей этой песочницы — чтобы наложение на листинг
    /// пользовалось тем же правилом, а не своим.</summary>
    public StringComparer PathComparer => _paths;
    private readonly string _root;
    private readonly bool _keepOnDispose;
    private readonly FileStream _lock;
    private bool _disposed;

    public string Root => _root;
    public string MountId { get; }

    private OverlayStore(string root, string mountId, bool keepOnDispose, FileStream ownerLock,
        StringComparer paths, StringComparison comparison)
    {
        _root = root;
        MountId = mountId;
        _keepOnDispose = keepOnDispose;
        _lock = ownerLock;
        _paths = paths;
        _pathComparison = comparison;
        _entries = new Dictionary<string, OverlayEntry>(paths);
    }

    /// <summary>mount-id включает идентификатор процесса и момент старта,
    /// поэтому list и doctor распознают осиротевшие после аварии каталоги.
    ///
    /// names задаёт, как песочница сравнивает пути. По умолчанию — политика
    /// текущей платформы, то есть то же правило, по которому их сравнивают
    /// вьюхи.</summary>
    public static OverlayStore Create(string? baseDirectory = null, bool keepOnDispose = false,
        DateTimeOffset? now = null, NamePolicy? names = null)
    {
        var policy = names ?? NamePolicy.For(NamePolicyKind.Native);
        var stamp = (now ?? DateTimeOffset.UtcNow).ToUnixTimeSeconds();
        var baseId = $"{Environment.ProcessId}-{stamp:x}";
        var parent = baseDirectory ?? DefaultRoot();

        // Два монтирования одного процесса в одну секунду дают одинаковый
        // mount-id: раньше они молча делили каталог, и первый же Dispose
        // сносил песочницу второго вместе с несохранёнными записями.
        //
        // Замка для этого мало: он охраняет только ОДНОВРЕМЕННЫЕ монтирования.
        // Песочница, оставленная сознательно («keep overlay after unmount»),
        // замка уже не держит — и следующий том той же секунды въезжал прямо
        // в неё, а при размонтировании стирал вместе со всем, что пользователь
        // просил сохранить. Каталог берётся только тогда, когда его ещё нет.
        for (var attempt = 1; ; attempt++)
        {
            var mountId = attempt == 1 ? baseId : $"{baseId}-{attempt}";
            var root = Path.Combine(parent, mountId);
            if (Directory.Exists(root))
            {
                if (attempt < 64) continue;
                throw new IOException($"cannot find a free overlay directory next to {root}");
            }
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

            var store = new OverlayStore(root, mountId, keepOnDispose, ownerLock,
                policy.Comparer, policy.Comparison);
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
            if (!entry.VirtualPath.StartsWith(prefix, _pathComparison)) continue;
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
            else
            {
                // Всегда обнуляем, а не «создаём, если нет». Сюда попадают
                // только те случаи, где по контракту должен получиться ПУСТОЙ
                // файл, а имя в песочнице — хеш пути, поэтому файл от прошлой
                // жизни этого же пути лежит ровно здесь. Прежнее «если нет»
                // усыновляло его: сорвавшийся на середине seed оставлял N
                // байт без записи в манифесте, потом rm ставил надгробие, не
                // удалив их, — и «новый пустой файл» печатал начало только
                // что удалённого блоба.
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
            // Удаляем файл песочницы БЕЗУСЛОВНО, а не только когда о нём есть
            // запись. Имя — хеш пути, так что кандидат ровно один; а файл,
            // осиротевший от сорвавшейся записи, записи в манифесте не имеет
            // и прежде переживал собственное надгробие, чтобы всплыть при
            // следующем создании того же пути.
            var path = Path.Combine(_root, StorageNameFor(key));
            try { if (File.Exists(path)) File.Delete(path); }
            catch (IOException) { }              // держит открытый хендл — переживём
            catch (UnauthorizedAccessException) { }
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
    /// <summary>Описание живого тома, положенное рядом с замком. Нужно
    /// `gitfs list`: том создаёт ОТДЕЛЬНЫЙ процесс, который живёт до Ctrl+C,
    /// и другому процессу узнать о нём больше неоткуда. Замок уже отличает
    /// живое от брошенного — здесь к нему добавляется, что именно живо.</summary>
    public const string DescriptorName = "mount.txt";

    public sealed record MountDescriptor(string Repository, string MountPoint, string Views,
        DateTimeOffset Since, string Root);

    /// <summary>Записывает описание тома. Формат — «ключ = значение» по
    /// строке: файл лежит в профиле пользователя, и его читают глазами не
    /// реже, чем программой.</summary>
    public void Describe(string repository, string mountPoint, string views,
        DateTimeOffset since)
    {
        try
        {
            File.WriteAllLines(Path.Combine(_root, DescriptorName), new[]
            {
                "repository = " + repository,
                "mount = " + mountPoint,
                "views = " + views,
                "since = " + since.ToString("O"),
            });
        }
        catch (IOException) { }               // list обойдётся без строки
        catch (UnauthorizedAccessException) { }
    }

    /// <summary>Тома, которые прямо сейчас держит хоть какой-то процесс.
    /// Ровно то, что должен печатать `gitfs list`.</summary>
    public static IReadOnlyList<MountDescriptor> FindLive(string? baseDirectory = null)
    {
        var root = baseDirectory ?? DefaultRoot();
        if (!Directory.Exists(root)) return [];
        var live = new List<MountDescriptor>();
        foreach (var dir in Directory.GetDirectories(root))
        {
            if (IsOrphan(dir)) continue;                 // брошенное — не том
            var file = Path.Combine(dir, DescriptorName);
            if (!File.Exists(file)) continue;            // том без описания: молчим о нём
            try
            {
                string repo = "", mount = "", views = "";
                var since = DateTimeOffset.MinValue;
                foreach (var line in File.ReadLines(file))
                {
                    var parts = line.Split('=', 2);
                    if (parts.Length != 2) continue;
                    var value = parts[1].Trim();
                    switch (parts[0].Trim())
                    {
                        case "repository": repo = value; break;
                        case "mount": mount = value; break;
                        case "views": views = value; break;
                        case "since":
                            DateTimeOffset.TryParse(value, null,
                                System.Globalization.DateTimeStyles.RoundtripKind, out since);
                            break;
                    }
                }
                if (mount.Length > 0) live.Add(new MountDescriptor(repo, mount, views, since, dir));
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        live.Sort((a, b) => string.CompareOrdinal(a.MountPoint, b.MountPoint));
        return live;
    }

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
