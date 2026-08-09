namespace Gitfs.App;

/// <summary>Недавние репозитории — фишки под полем пути в макете (03).
///
/// Формат нарочно примитивный: по одному пути в строке, свежий сверху.
/// Человек может открыть этот файл, прочитать его и стереть строку — для
/// списка из десяти путей это ценнее любого формата, который читается только
/// программой. Ошибки чтения и записи НИКОГДА не выходят наружу: список
/// недавних — удобство, и приложение, которое не открылось из-за него,
/// потеряло больше, чем этот список стоит.</summary>
public sealed class RecentRepositories
{
    public const int Capacity = 8;

    public static RecentRepositories Instance { get; } = new(DefaultPath());

    private readonly string _path;
    private readonly object _gate = new();

    public RecentRepositories(string path) => _path = path;

    public static string DefaultPath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "gitfs", "recent.txt");

    public sealed record Entry(string Path, string Name, bool StillARepository);

    /// <summary>Свежие сверху. Исчезнувшие пути НЕ выбрасываются: тихо
    /// укоротить список — значит соврать о том, что человек монтировал.
    /// Вместо этого они помечены, и диалог покажет их погашенными.</summary>
    public IReadOnlyList<Entry> Load()
    {
        var result = new List<Entry>(Capacity);
        foreach (var line in ReadLines())
        {
            bool ok;
            try { ok = MountService.ResolveGitDir(line) is not null; }
            catch (Exception) { ok = false; } // путь на отключённом сетевом диске
            result.Add(new Entry(line, NameOf(line), ok));
        }
        return result;
    }

    /// <summary>Запоминает путь как самый свежий. Дубликаты не копятся:
    /// повторное монтирование того же репозитория поднимает его наверх.</summary>
    public void Remember(string repositoryPath)
    {
        string full;
        try { full = Path.GetFullPath(repositoryPath); }
        catch (Exception) { return; }

        lock (_gate)
        {
            var lines = new List<string> { full };
            foreach (var line in ReadLines())
            {
                if (lines.Count >= Capacity) break;
                if (!PathsEqual(line, full)) lines.Add(line);
            }
            Save(lines);
        }
    }

    public void Forget(string repositoryPath)
    {
        lock (_gate)
        {
            Save(ReadLines().Where(l => !PathsEqual(l, repositoryPath)).ToList());
        }
    }

    // ---------- служебное ----------

    private static bool PathsEqual(string a, string b) => string.Equals(a, b,
        OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase);

    private static string NameOf(string path)
    {
        try
        {
            var name = new DirectoryInfo(path).Name;
            return string.IsNullOrEmpty(name) ? path : name;
        }
        catch (Exception) { return path; }
    }

    private List<string> ReadLines()
    {
        try
        {
            if (!File.Exists(_path)) return new List<string>();
            return File.ReadLines(_path)
                .Select(l => l.Trim())
                .Where(l => l.Length > 0)
                .Take(Capacity)
                .ToList();
        }
        catch (Exception e)
        {
            Program.Log("recent-read", e);
            return new List<string>();
        }
    }

    private void Save(List<string> lines)
    {
        try
        {
            var dir = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            // Через временный файл: прерванная запись оставляла бы список
            // наполовину переписанным, а восстанавливать его неоткуда.
            var temp = _path + ".tmp";
            File.WriteAllLines(temp, lines.Take(Capacity));
            File.Move(temp, _path, overwrite: true);
        }
        catch (Exception e)
        {
            Program.Log("recent-write", e);
        }
    }
}
