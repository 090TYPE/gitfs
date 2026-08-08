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

    private readonly object _gate = new();
    private readonly Dictionary<string, OverlayEntry> _entries = new(StringComparer.OrdinalIgnoreCase);
    private readonly string _root;
    private readonly bool _keepOnDispose;
    private bool _disposed;

    public string Root => _root;
    public string MountId { get; }

    private OverlayStore(string root, string mountId, bool keepOnDispose)
    {
        _root = root;
        MountId = mountId;
        _keepOnDispose = keepOnDispose;
    }

    /// <summary>mount-id включает идентификатор процесса и момент старта,
    /// поэтому list и doctor распознают осиротевшие после аварии каталоги.</summary>
    public static OverlayStore Create(string? baseDirectory = null, bool keepOnDispose = false,
        DateTimeOffset? now = null)
    {
        var stamp = (now ?? DateTimeOffset.UtcNow).ToUnixTimeSeconds();
        var mountId = $"{Environment.ProcessId}-{stamp:x}";
        var root = Path.Combine(baseDirectory ?? DefaultRoot(), mountId);
        Directory.CreateDirectory(root);
        RestrictToOwner(root);
        var store = new OverlayStore(root, mountId, keepOnDispose);
        store.LoadManifest();
        return store;
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

    /// <summary>Осиротевшие каталоги песочницы: их показывает doctor.</summary>
    public static IReadOnlyList<string> FindOrphans(string? baseDirectory = null)
    {
        var root = baseDirectory ?? DefaultRoot();
        if (!Directory.Exists(root)) return [];
        var live = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var process in System.Diagnostics.Process.GetProcesses())
        {
            try { live.Add(process.Id.ToString()); } catch { /* процесс ушёл */ }
        }
        return Directory.GetDirectories(root)
            .Where(dir =>
            {
                var name = Path.GetFileName(dir);
                var dash = name.IndexOf('-');
                return dash <= 0 || !live.Contains(name[..dash]);
            })
            .ToList();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_keepOnDispose) return;
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
