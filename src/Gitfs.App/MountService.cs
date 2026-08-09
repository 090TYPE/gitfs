using Gitfs.Diagnostics;
using Gitfs.Vfs;
using Gitfs.Vfs.Overlay;
using Gitfs.Vfs.Views;
#if GITFS_WINFSP
using Gitfs.Mount.WinFsp;
#endif
#if GITFS_FUSE
using Gitfs.Mount.Fuse;
#endif

namespace Gitfs.App;

/// <summary>Состояние тома (макет 02/03). «reopening» — не выдумка ради
/// цветного кружка: репозиторий пересобрался под смонтированным томом, и
/// объекты пришлось открыть заново. Именно это бриф §4.1 называет
/// деградацией — и считается она по журналу тома, а не по посторонним
/// предупреждениям окружения.</summary>
public enum MountHealth { Ok, Reopening }

public sealed record MountEntry(string Repository, string Path, string MountPoint,
    string Views, DateTimeOffset Since)
{
    /// <summary>Пересчитывается службой при каждом чтении списка, а не
    /// хранится: том живёт часами, и состояние, снятое в момент
    /// монтирования, к вечеру не значит ничего.</summary>
    public MountHealth Health { get; init; } = MountHealth.Ok;

    public int Reopens { get; init; }

    public string HealthWord => Health == MountHealth.Ok ? "ok" : "reopening";

    /// <summary>Для разметки: строке нужен признак, а не перечисление —
    /// класс включается булевым свойством, и пульс появляется у больного
    /// тома сам, без кода в обработчиках.</summary>
    public bool IsDegraded => Health != MountHealth.Ok;

    public string Uptime
    {
        get
        {
            var span = DateTimeOffset.UtcNow - Since;
            return span.TotalHours >= 1
                ? $"{(int)span.TotalHours}h {span.Minutes:00}m"
                : $"{span.Minutes}m";
        }
    }
}

/// <summary>Монтирования процесса. На платформах без адаптера файловой
/// системы (Linux/macOS до вехи M6) CanMount = false: дерево можно
/// просматривать в приложении, но том не создаётся.</summary>
public sealed class MountService
{
    public static MountService Instance { get; } = new();

    // Mount/Unmount выполняются в фоновом потоке, Entries читается из UI —
    // список обязан быть под замком (находка ревью).
    private readonly object _gate = new();
    private readonly List<MountEntry> _entries = new();
    private readonly Dictionary<string, LiveMount> _live = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Том вместе со своей песочницей. Раньше снятие тома освобождало
    /// только том, и каталог песочницы оставался на диске НАВСЕГДА — по одному
    /// на каждое монтирование за всю историю машины.
    /// Порядок обязателен: сперва том (иначе операция в полёте пишет в уже
    /// удалённый каталог), потом песочница.</summary>
    private sealed record LiveMount(IDisposable Mount, OverlayStore Overlay,
        SnapshotManager? Snapshots, MountLog? Log = null) : IDisposable
    {
        public void Dispose()
        {
            try { Mount.Dispose(); }
            finally { Overlay.Dispose(); }
        }
    }

    /// <summary>Живые счётчики тома для панели деталей (макет 03). Всё
    /// читается у работающих кэшей — ни одного придуманного числа.
    /// null, если тома с такой точкой монтирования в этом процессе нет.</summary>
    public sealed record MountStats(
        long TreeHits, long TreeMisses, long TreeBytes, long TreeBudget,
        long ListingHits, long ListingMisses,
        long PathHits, long PathMisses,
        long DeltaHits, long SizeHits, int Packs, long PackedObjects, int Reopens,
        int OverlayFiles, long OverlayBytes)
    {
        /// <summary>Доля попаданий по всем кэшам вместе. Ноль обращений — не
        /// «ноль процентов», а «ещё не спрашивали»: показывать 0% на свежем
        /// томе значит наговаривать на кэш.</summary>
        public double? HitRate
        {
            get
            {
                var hits = TreeHits + ListingHits + PathHits;
                var total = hits + TreeMisses + ListingMisses + PathMisses;
                return total == 0 ? null : (double)hits / total;
            }
        }
    }

    /// <summary>Что записал ЭТОТ ТОМ (макет 03: «просмотр log.txt» в панели
    /// деталей). Панель показывала журнал ПРИЛОЖЕНИЯ — файл, в который пишутся
    /// исключения самого GUI. Он одинаков для всех строк таблицы, и выбор тома
    /// его не менял: подпись «LOG» под выделенным томом обещала одно, а
    /// показывала другое.
    ///
    /// Сначала спрашиваем свой MountLog — это тот же объект, из которого том
    /// строит .gitfs/log.txt, только без похода в файловую систему. Если том
    /// смонтирован ДРУГИМ процессом, читаем сам файл на томе: он там и есть,
    /// ради этого вьюха .gitfs/ и существует.
    ///
    /// Возвращает null, когда журнала не достать вообще, — панель обязана
    /// отличить «пусто» от «неизвестно».</summary>
    public IReadOnlyList<string>? LogFor(string mountPoint, int count)
    {
        LiveMount? live;
        lock (_gate) _live.TryGetValue(mountPoint, out live);
        if (live?.Log is { } log) return Tail(log.Lines, count);

        try
        {
            var path = Path.Combine(mountPoint.EndsWith(':') ? mountPoint + Path.DirectorySeparatorChar : mountPoint,
                ".gitfs", "log.txt");
            if (!File.Exists(path)) return null;

            // Чтение идёт ЧЕРЕЗ ТОМ, то есть через наш же адаптер. Зависший
            // том не имеет права заморозить окно, поэтому у чтения есть срок,
            // и по его истечении панель честно скажет, что не дозналась.
            var read = Task.Run(() => File.ReadAllLines(path));
            return read.Wait(TimeSpan.FromSeconds(2)) ? Tail(read.Result, count) : null;
        }
        catch (Exception e)
        {
            Program.Log("log-for-" + mountPoint, e);
            return null;
        }
    }

    /// <summary>Хвост журнала тома — отдельно, чтобы его можно было проверить
    /// без живого монтирования.</summary>
    internal static IReadOnlyList<string> TailOf(MountLog log, int count) => Tail(log.Lines, count);

    private static IReadOnlyList<string> Tail(IReadOnlyList<string> lines, int count) =>
        lines.Count <= count ? lines : lines.Skip(lines.Count - count).ToList();

    public MountStats? StatsFor(string mountPoint)
    {
        LiveMount? live;
        lock (_gate) _live.TryGetValue(mountPoint, out live);
        if (live?.Snapshots is not { } manager) return null;

        try
        {
            using var lease = manager.Acquire();
            var snapshot = lease.Snapshot;
            return new MountStats(
                snapshot.TreeCache.Hits, snapshot.TreeCache.Misses,
                snapshot.TreeCache.Used, snapshot.TreeCache.MaxCost,
                snapshot.ListingCache.Hits, snapshot.ListingCache.Misses,
                snapshot.PathCache.Hits, snapshot.PathCache.Misses,
                snapshot.Objects.DeltaBaseCacheHits, snapshot.Objects.SizeCacheHits,
                snapshot.Objects.PackCount, snapshot.Objects.PackedObjectCount,
                live.Log?.Reopens ?? 0,
                live.Overlay.Entries.Count, live.Overlay.TotalBytes);
        }
        catch (Exception)
        {
            // Панель деталей не имеет права уронить окно: снапшот могли
            // подменить прямо сейчас, а том — уже сниматься.
            return null;
        }
    }

    /// <summary>Список монтирований с ЖИВЫМ состоянием каждого: переоткрытия
    /// пакетов считает журнал тома, и таблица обязана показывать сегодняшнее
    /// число, а не то, что было в момент монтирования.</summary>
    public IReadOnlyList<MountEntry> Entries
    {
        get
        {
            lock (_gate)
            {
                return _entries.Select(entry =>
                {
                    if (!_live.TryGetValue(entry.MountPoint, out var live) || live.Log is not { } log)
                        return entry;
                    return entry with
                    {
                        Reopens = log.Reopens,
                        Health = log.Reopens > 0 ? MountHealth.Reopening : MountHealth.Ok,
                    };
                }).ToList();
            }
        }
    }

#if GITFS_WINFSP
    public bool CanMount => OperatingSystem.IsWindows();
    public string? MountBlockedReason => CanMount ? null
        : "the filesystem adapter for this platform is not built yet";
#elif GITFS_FUSE
    public bool CanMount => OperatingSystem.IsLinux();
    public string? MountBlockedReason => CanMount ? null
        : "this build carries the FUSE adapter, which needs Linux";
#else
    public bool CanMount => false;
    public string? MountBlockedReason =>
        "macOS needs macFUSE, which gitfs does not carry yet; the tree is still browsable";
#endif

    /// <summary>Дерево вьюх по настройкам монтирования. Лимиты доходят ровно
    /// до тех вьюх, которые в диалоге и названы: «commit limit» — это commits/,
    /// «history limit» — потолок версий одного файла. DatesView живёт со своим
    /// собственным окном: подписать одним числом две разные величины значит
    /// тихо урезать одну из них.</summary>
    public static VirtualTree BuildTree(IReadOnlyCollection<string> views,
        MountOptions? options = null, MountLog? log = null, OverlayStore? overlay = null,
        string repositoryName = "repository")
    {
        var opts = options ?? MountOptions.Default;
        var names = NamePolicy.For(opts.NamePolicy);
        var list = new List<IView>();
        if (views.Contains("branches")) list.Add(new BranchesView(names));
        if (views.Contains("tags")) list.Add(new TagsView(names));
        if (views.Contains("commits")) list.Add(new CommitsView(names, opts.CommitLimit));
        if (views.Contains("dates")) list.Add(new DatesView(names));
        if (views.Contains("history")) list.Add(new HistoryView(names, opts.HistoryLimit, log: log));
        // Служебная вьюха идёт последней и НЕ выбирается пользователем:
        // диагностика тома — не вьюха истории, а способ понять, почему том
        // ведёт себя так. Отключить её значило бы прятать от человека
        // единственное объяснение, когда оно нужнее всего (спека §14).
        if (log is not null) list.Add(new GitfsView(names, log, overlay));
        return new VirtualTree(list, repositoryName);
    }

    public static string? ResolveGitDir(string repoPath) => Doctor.ResolveGitDir(repoPath);

    public static IReadOnlyList<Check> Diagnose(string? repoPath) => Doctor.Run(repoPath);

    /// <summary>Готовит точку монтирования, если это папка.
    ///
    /// Диалог предлагает ~/mnt/gitfs по умолчанию, кнопка была активна, а
    /// монтирование падало строкой «каталога нет, создайте его сами»: кнопка,
    /// которая не может сработать, — обещание, данное впустую. Каталог,
    /// названный человеком в диалоге, приложение создаёт само.
    ///
    /// Занятый каталог НЕ трогаем: том поверх непустой папки прячет её
    /// содержимое до размонтирования, и делать это молча нельзя.</summary>
    internal static void PrepareMountPoint(string mountPoint)
    {
        if (mountPoint.Length <= 3 && mountPoint.EndsWith(':')) return;   // буква диска
        try
        {
            if (Directory.Exists(mountPoint))
            {
                if (Directory.EnumerateFileSystemEntries(mountPoint).Any())
                    throw new InvalidOperationException(
                        $"{mountPoint} is not empty; a volume would hide what is in it");
                return;
            }
            if (File.Exists(mountPoint))
                throw new InvalidOperationException($"{mountPoint} is a file, not a folder");
            Directory.CreateDirectory(mountPoint);
        }
        catch (InvalidOperationException) { throw; }
        catch (Exception e)
        {
            throw new InvalidOperationException(
                $"cannot use {mountPoint} as a mount point: {e.Message}", e);
        }
    }

    public static IReadOnlyList<char> FreeDriveLetters()
    {
        if (!OperatingSystem.IsWindows()) return [];
        var used = DriveInfo.GetDrives().Select(d => d.Name[0]).ToHashSet();
        return "GHIJKLMNOPQRSTUVWXYZ".Where(c => !used.Contains(c)).ToList();
    }

    /// <summary>Монтирует репозиторий. Бросает с человеческим текстом,
    /// если платформа не поддерживает монтирование или драйвер недоступен.</summary>
    public MountEntry Mount(string repoPath, string mountPoint, IReadOnlyCollection<string> views,
        MountOptions? options = null)
    {
        if (!CanMount) throw new InvalidOperationException(MountBlockedReason!);
        var opts = options ?? MountOptions.Default;
        // Диалог уже проверил эти числа, но Mount вызывается не только из него.
        if (opts.Validate() is { } problem) throw new ArgumentException(problem, nameof(options));
        var gitDir = Doctor.ResolveGitDir(repoPath)
            ?? throw new InvalidOperationException($"no .git directory in {repoPath}");
        lock (_gate)
        {
            if (_live.ContainsKey(mountPoint))
                throw new InvalidOperationException($"{mountPoint} is already mounted by gitfs");
        }
        PrepareMountPoint(mountPoint);

#if GITFS_WINFSP || GITFS_FUSE
        var names = NamePolicy.For(opts.NamePolicy);
        var log = new MountLog();
        var manager = new SnapshotManager(gitDir, options: opts) { Log = log };
        var overlay = OverlayStore.Create(keepOnDispose: opts.KeepOverlay, names: names);
        log.Add("mount", $"{repoPath} → {mountPoint}");
        var repoName = new DirectoryInfo(repoPath).Name;
        var target = new VfsMountTarget(manager, BuildTree(views, opts, log, overlay, repoName),
            repoName, readOnly: opts.ReadOnly, overlay: overlay);
        IDisposable mount;
        // выше границы адаптеры отличаются одной строкой — ровно в этом и
        // состояло обещание IMountTarget
        try
        {
#if GITFS_WINFSP
            mount = GitfsMount.Mount(target, mountPoint, readOnly: opts.ReadOnly);
#else
            mount = GitfsFuseMount.Mount(target, mountPoint, readOnly: opts.ReadOnly);
#endif
        }
        // Освобождаем ЦЕЛЬ, а не только песочницу: через неё уходит и
        // SnapshotManager, а он держит каждый packfile отображённым в память
        // без FILE_SHARE_DELETE. После неудачного монтирования репозиторий
        // нельзя было ни удалить, ни переместить, пока не отработает сборщик
        // мусора. Успешный путь этим не страдал — там всё освобождает
        // GitfsMount.Dispose; текла ровно ветка отказа.
        catch { target.Dispose(); throw; }
        lock (_gate) _live[mountPoint] = new LiveMount(mount, overlay, manager, log);
#endif
        var entry = new MountEntry(new DirectoryInfo(repoPath).Name, repoPath, mountPoint,
            string.Join(' ', new[] { "branches", "tags", "commits", "dates", "history" }
                .Select(v => views.Contains(v) ? v[0].ToString() : "·")),
            DateTimeOffset.UtcNow);
        lock (_gate) _entries.Add(entry);
        return entry;
    }

    public void Unmount(MountEntry entry)
    {
        LiveMount? mount;
        lock (_gate)
        {
            _live.Remove(entry.MountPoint, out mount);
            _entries.Remove(entry);
        }
        mount?.Dispose(); // снимаем том вне замка: teardown может быть долгим
    }

    public void UnmountAll()
    {
        List<LiveMount> mounts;
        lock (_gate)
        {
            mounts = _live.Values.ToList();
            _live.Clear();
            _entries.Clear();
        }
        foreach (var mount in mounts)
        {
            try { mount.Dispose(); }
            catch (Exception e) { Program.Log("unmount-all", e); }
        }
    }
}
