
using Gitfs.Cli;
using Gitfs.Diagnostics;
using Gitfs.Core;
using Gitfs.Vfs;
using Gitfs.Vfs.Views;
using Gitfs.Vfs.Overlay;
#if GITFS_WINFSP
using Gitfs.Mount.WinFsp;
#endif
#if GITFS_FUSE
using Gitfs.Mount.Fuse;
#endif

if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
{
    PrintUsage();
    return 0;
}

try
{
    return args[0] switch
    {
        "doctor" => RunDoctor(args.Skip(1).ToArray()),
        "tree" => Tree(args.Skip(1).ToArray()),
        "cat" => Cat(args.Skip(1).ToArray()),
        "list" => List(),
        "mount" => Mount(args.Skip(1).ToArray()),
        "unmount" => Unmount(args.Skip(1).ToArray()),
        "purge" => Purge(),
        _ => Unknown(args[0]),
    };
}
catch (Exception e)
{
    Console.Error.WriteLine(Ansi.ErrStream(CheckStatus.Fail, "fail ") + e.Message);
    return 1;
}


/// <summary>Ошибка одним вызовом: строка «что случилось» и, если он есть,
/// рецепт «→ как починить». Раньше половина ошибок печаталась без рецепта —
/// формат брифа §3 соблюдался там, где о нём вспомнили, а вспомнили не
/// везде. Здесь его нельзя забыть: он в сигнатуре.</summary>
static int Fail(string what, params string[] fixes)
{
    Console.Error.WriteLine(Ansi.ErrStream(CheckStatus.Fail, "fail ") + what);
    foreach (var fix in fixes)
        Console.Error.WriteLine(Ansi.DimErr("     → " + fix));
    return 1;
}

static void PrintUsage()
{
    Console.WriteLine("""
        gitfs — git history mounted as a drive

          gitfs doctor [<repo>]        check the environment (and a repository)
          gitfs tree <repo> [<path>]   walk the virtual tree without mounting
          gitfs cat <repo> <path>      print a file from the virtual tree
          gitfs mount <repo> <target>  mount a repository (Ctrl+C unmounts)
          gitfs list                   list every volume gitfs is holding
          gitfs unmount <target>       release a volume gitfs is holding
          gitfs purge                  remove overlays left by crashed runs

        Views: branches, tags, commits, dates, history.

        Mount options (the Advanced section of the desktop app):
          --views=<a,b,c>              which views to build (default: all five)
          --read-only                  refuse every write to the volume
          --keep-overlay               keep the write sandbox after unmount
          --portable-names             apply the Windows name rules everywhere
          --history-ref=<ref|sha>      show history as of this point, not HEAD
          --commit-limit=<n>           how many commits commits/ lists (200)
          --history-limit=<n>          version ceiling per file (500)
          --cache-mb=<n>               tree and listing cache budget (96)
          --max-blob-mb=<n>            ceiling for one cached object (8)
        """);
}

/// <summary>Все пять вьюх спеки §3 по настройкам монтирования.</summary>
static VirtualTree BuildTree(MountOptions? options = null, MountLog? log = null,
    OverlayStore? overlay = null, string repositoryName = "repository")
{
    var opts = options ?? MountOptions.Default;
    var names = NamePolicy.For(opts.NamePolicy);
    // Вьюхи выбираются ключом --views. Раньше их всегда было пять, и колонка
    // VIEWS в `gitfs list` не могла отличаться ни у одного тома — то есть
    // выбор, который есть в диалоге, для скриптов не существовал.
    bool On(string view) => opts.Views.Contains(view, StringComparer.OrdinalIgnoreCase);
    var views = new List<IView>();
    if (On("branches")) views.Add(new BranchesView(names));
    if (On("tags")) views.Add(new TagsView(names));
    if (On("commits")) views.Add(new CommitsView(names, opts.CommitLimit));
    if (On("dates")) views.Add(new DatesView(names));
    if (On("history")) views.Add(new HistoryView(names, opts.HistoryLimit, log: log));
    // .gitfs/ появляется только у смонтированного тома: у `gitfs tree` нет ни
    // песочницы, ни журнала, и показывать пустую диагностику незачем.
    if (log is not null) views.Add(new GitfsView(names, log, overlay));
    return new VirtualTree(views, repositoryName);
}

/// <summary>Разбор ключей монтирования — та же секция Advanced, что и в
/// диалоге приложения. Два входа в один продукт обязаны уметь одно и то же:
/// настройка, доступная только через окно, для сценариев и CI не существует.
/// Возвращает null и печатает причину, если ключ или значение непонятны —
/// молча взять значение по умолчанию значило бы сделать не то, что просили.</summary>
static MountOptions? ParseOptions(IEnumerable<string> flags)
{
    var options = MountOptions.Default;
    foreach (var flag in flags)
    {
        var eq = flag.IndexOf('=');
        var key = eq < 0 ? flag : flag[..eq];
        var raw = eq < 0 ? null : flag[(eq + 1)..];

        bool Number(out int value)
        {
            if (int.TryParse(raw, out value)) return true;
            Fail($"{key} needs a number, got '{raw}'",
                "give it a whole number, for example " + key + "=200");
            return false;
        }

        switch (key)
        {
            case "--views":
                // Вьюхи задаются и здесь: без ключа `mount` всегда печатал
                // «5 views», а колонка VIEWS в list не могла отличаться ни у
                // одного тома — выбор из диалога для скриптов не существовал.
                if (string.IsNullOrWhiteSpace(raw))
                {
                    Console.Error.WriteLine("fail --views needs a comma-separated list");
                    Console.Error.WriteLine("     → for example: --views=branches,history");
                    return null;
                }
                var wanted = raw.Split(',', StringSplitOptions.RemoveEmptyEntries
                                            | StringSplitOptions.TrimEntries);
                foreach (var view in wanted)
                {
                    if (MountOptions.AllViews.Contains(view, StringComparer.OrdinalIgnoreCase)) continue;
                    Console.Error.WriteLine($"fail unknown view: {view}");
                    Console.Error.WriteLine("     → pick from " + string.Join(", ", MountOptions.AllViews));
                    return null;
                }
                options = options with { Views = wanted };
                break;
            case "--read-only": options = options with { ReadOnly = true }; break;
            case "--keep-overlay": options = options with { KeepOverlay = true }; break;
            case "--portable-names":
                options = options with { NamePolicy = NamePolicyKind.Portable }; break;
            case "--history-ref":
                if (string.IsNullOrWhiteSpace(raw))
                {
                    Fail("--history-ref needs a ref or a sha",
                        "for example --history-ref=v1.0 or --history-ref=main");
                    return null;
                }
                options = options with { HistoryRef = raw }; break;
            case "--commit-limit":
                if (!Number(out var commits)) return null;
                options = options with { CommitLimit = commits }; break;
            case "--history-limit":
                if (!Number(out var history)) return null;
                options = options with { HistoryLimit = history }; break;
            case "--cache-mb":
                if (!Number(out var cache)) return null;
                options = options with { CacheMegabytes = cache }; break;
            case "--max-blob-mb":
                if (!Number(out var blob)) return null;
                options = options with { MaxCachedBlobMegabytes = blob }; break;
            default:
                Console.Error.WriteLine($"fail unknown option: {flag}");
                Console.Error.WriteLine("     → gitfs --help lists the mount options");
                return null;
        }
    }

    if (options.Validate() is { } problem)
    {
        Fail(problem, "gitfs --help lists what each option accepts");
        return null;
    }
    return options;
}

static int RunDoctor(string[] rest)
{
    var repo = rest.FirstOrDefault();
    var checks = Doctor.Run(repo is null ? null : Path.GetFullPath(repo));
    Console.Write(Report.Render(checks));
    return Report.ExitCode(checks);
}

/// <summary>Обход виртуального дерева без монтирования: то же, что увидит
/// Проводник, когда появится адаптер. Полезно и как диагностика, и для GIF.</summary>
static int Tree(string[] rest)
{
    // Ключи те же, что у mount: дерево обязано показывать ровно то, что
    // покажет том с этими настройками, иначе обход теряет смысл как проверка.
    var options = ParseOptions(rest.Where(a => a.StartsWith("--")));
    if (options is null) return 1;
    rest = rest.Where(a => !a.StartsWith("--")).ToArray();

    if (rest.Length == 0)
    {
        return Fail("gitfs tree needs a repository path",
            "for example: gitfs tree . history");
    }
    var gitDir = Doctor.ResolveGitDir(Path.GetFullPath(rest[0]));
    if (gitDir is null)
    {
        Console.Error.WriteLine($"fail no .git directory in {rest[0]}");
        Console.Error.WriteLine("     → point gitfs at a repository root");
        return 1;
    }

    using var snapshot = RepoSnapshot.Load(gitDir, options: options);
    var tree = BuildTree(options);
    var path = rest.Length > 1 ? rest[1] : "/";

    var node = tree.Resolve(snapshot, path);
    if (node is null)
    {
        return Fail($"no such path: {path}",
            "gitfs tree <repo> lists what the root holds");
    }
    if (node.Value.Kind != NodeKind.Directory)
    {
        Console.WriteLine($"{node.Value.Kind.ToString().ToLowerInvariant(),-9} " +
                          $"{node.Value.Size,10}  {node.Value.Timestamp.UtcDateTime:yyyy-MM-dd}  {path}");
        return 0;
    }
    foreach (var entry in tree.List(snapshot, path) ?? [])
    {
        var kind = entry.Info.Kind == NodeKind.Directory ? "dir" : entry.Info.Kind.ToString().ToLowerInvariant();
        var size = entry.Info.Kind == NodeKind.Directory ? "" : entry.Info.Size.ToString();
        Console.WriteLine($"{kind,-9} {size,10}  {entry.Info.Timestamp.UtcDateTime:yyyy-MM-dd}  {entry.Name}");
    }
    return 0;
}

/// <summary>Содержимое узла виртуального дерева — то же, что отдаст ФС.</summary>
static int Cat(string[] rest)
{
    if (rest.Length < 2)
    {
        Console.Error.WriteLine("fail gitfs cat needs a repository and a path");
        Console.Error.WriteLine(@"     → for example: gitfs cat . history/README.md/latest.md");
        return 1;
    }
    var gitDir = Doctor.ResolveGitDir(Path.GetFullPath(rest[0]));
    if (gitDir is null)
    {
        return Fail($"no .git directory in {rest[0]}",
            "point gitfs at a repository root");
    }

    var manager = new SnapshotManager(gitDir);
    using var target = new VfsMountTarget(manager, BuildTree(),
        new DirectoryInfo(Path.GetFullPath(rest[0])).Name);

    var opened = target.Open(rest[1], OpenMode.Read);
    if (!opened.TryGet(out var handle))
    {
        Fail($"cannot open {rest[1]}: {opened.Error}",
            "gitfs tree <repo> <path> shows what is there");
        return 1;
    }
    try
    {
        if (handle.IsDirectory)
        {
            Fail($"{rest[1]} is a directory", "gitfs cat prints files; use gitfs tree for folders");
            return 1;
        }
        var buffer = new byte[64 * 1024];
        using var stdout = Console.OpenStandardOutput();
        long offset = 0;
        while (true)
        {
            var read = target.Read(handle, offset, buffer);
            if (!read.TryGet(out var count))
            {
                return Fail($"read failed: {read.Error}",
                    "the object may be corrupt; git fsck says for sure");
            }
            if (count == 0) break;
            stdout.Write(buffer, 0, count);
            offset += count;
        }
        return 0;
    }
    finally { target.Close(handle); }
}

/// <summary>Таблица монтирований (бриф §3): репозиторий → точка → вьюхи →
/// аптайм. Раньше печатался ОДИН заголовок и строка «(no mounts in this
/// process)» — том создаёт отдельный процесс, который живёт до Ctrl+C, и
/// заголовок над пустотой был честен ровно наполовину: тома есть, просто не
/// здесь. Теперь список читается у песочниц: замок уже отличает живое от
/// брошенного, а рядом с ним лежит описание тома.</summary>
static int List()
{
    var live = OverlayStore.FindLive();
    Console.WriteLine(Ansi.Accent($"{"REPOSITORY",-18}{"MOUNT",-12}{"VIEWS",-14}UPTIME"));
    if (live.Count == 0)
    {
        Console.WriteLine(Ansi.Dim("(nothing mounted)"));
    }
    foreach (var mount in live)
    {
        var uptime = mount.Since == DateTimeOffset.MinValue
            ? "—"
            : Humanize(DateTimeOffset.UtcNow - mount.Since);
        Console.WriteLine($"{Trim(mount.Repository, 17),-18}{Trim(mount.MountPoint, 11),-12}" +
                          $"{Trim(mount.Views, 13),-14}{uptime}");
    }

    var orphans = OverlayStore.FindOrphans();
    if (orphans.Count > 0)
    {
        Console.WriteLine();
        Console.WriteLine($"{orphans.Count} orphaned overlay(s) from earlier runs:");
        foreach (var dir in orphans) Console.WriteLine(Ansi.Dim("  " + dir));
    }
    return 0;
}

/// <summary>Время работы одной короткой строкой — как в колонке макета.</summary>
static string Humanize(TimeSpan span) => span.TotalHours >= 1
    ? $"{(int)span.TotalHours}h {span.Minutes:00}m"
    : $"{span.Minutes}m";

/// <summary>Обрезает с многоточием: колонки таблицы обязаны держать ширину,
/// иначе длинное имя репозитория ломает всю сетку.</summary>
static string Trim(string value, int width) =>
    value.Length <= width ? value : value[..(width - 1)] + "…";

static int Mount(string[] rest)
{
    if (rest.Length < 2)
    {
        Console.Error.WriteLine("fail gitfs mount needs a repository and a mount point");
        Console.Error.WriteLine(@"     → for example: gitfs mount C:\src\gitfs G:");
        return 1;
    }
    var repoPath = Path.GetFullPath(rest[0]);
    var mountPoint = rest[1];
    var options = ParseOptions(rest.Skip(2));
    if (options is null) return 1;
#if !GITFS_WINFSP && !GITFS_FUSE
    Console.Error.WriteLine("fail gitfs cannot mount on this platform yet");
    Console.Error.WriteLine("     → macOS needs macFUSE (milestone M8); gitfs tree works everywhere");
    return 1;
#else

    var checks = Doctor.Run(repoPath);
    var blockers = checks.Where(c => c.Status == CheckStatus.Fail).ToList();
    if (blockers.Count > 0)
    {
        Console.Error.Write(Report.Render(blockers));
        return 1;
    }

    var gitDir = Doctor.ResolveGitDir(repoPath)!;
    var log = new MountLog();
    var manager = new SnapshotManager(gitDir, options: options) { Log = log };
    var name = new DirectoryInfo(repoPath).Name;
    // overlay включён: без него Word и Excel не откроют файл из старого
    // коммита — они пишут lock-файлы рядом с открываемым (спека §10).
    // using обязателен: песочница живёт ровно столько, сколько том.
    using var overlay = OverlayStore.Create(keepOnDispose: options.KeepOverlay,
        names: NamePolicy.For(options.NamePolicy));
    log.Add("mount", $"{repoPath} -> {mountPoint}");
    // Описание рядом с замком: другой процесс иначе не узнает, что этот том
    // существует, и `gitfs list` печатал бы заголовок над пустотой.
    overlay.Describe(name, mountPoint,
        string.Join(' ', MountOptions.AllViews.Select(v =>
            options.Views.Contains(v, StringComparer.OrdinalIgnoreCase) ? v[0].ToString() : "·")),
        DateTimeOffset.UtcNow);
    var tree = BuildTree(options, log, overlay, name);
    var target = new VfsMountTarget(manager, tree, name, readOnly: options.ReadOnly,
        overlay: overlay);

    var started = DateTime.UtcNow;
    void Log(string line) => Console.Error.WriteLine($"     {line}");
    try
    {
#if GITFS_WINFSP
        using var mount = GitfsMount.Mount(target, mountPoint, Log, readOnly: options.ReadOnly);
#else
        using var mount = GitfsFuseMount.Mount(target, mountPoint, Log, readOnly: options.ReadOnly);
#endif
        var elapsed = (DateTime.UtcNow - started).TotalSeconds;
        var mode = options.ReadOnly ? "read-only" : "writable";
        Console.WriteLine(Ansi.Ok("mounted ") + $"{mountPoint} · {name} · {options.Views.Count} view" +
                          (options.Views.Count == 1 ? "" : "s") + $" · {mode} · {elapsed:0.0}s");
        if (options.KeepOverlay)
            Console.WriteLine($"overlay kept at {overlay.Root}");
        Console.WriteLine("press Ctrl+C to unmount");

        // Без using — сознательно. Обработчик ProcessExit ниже срабатывает
        // уже ПОСЛЕ выхода из этой области видимости, и на штатном снятии
        // тома он звал Wait по освобождённому объекту: каждое размонтирование
        // заканчивалось «Unhandled exception: ObjectDisposedException» —
        // строкой, которая выглядит как авария там, где всё прошло хорошо.
        // Эти два события не держат ничего, что нужно вернуть системе раньше
        // конца процесса.
        var stop = new ManualResetEventSlim();
        var torndown = new ManualResetEventSlim();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.Set(); };

        // SIGTERM тоже снимает том: без этого docker stop и systemd оставляли
        // бы точку монтирования висеть, а песочницу — на диске.
        //
        // Регистрацию ОБЯЗАТЕЛЬНО держать: PosixSignalRegistration снимает
        // обработчик в финализаторе, а единственная ссылка на него — это
        // возвращаемое значение. Отброшенное, оно переживало один сбор
        // мусора, и обработчик исчезал — а цикл FUSE аллоцирует на каждом
        // readdir, так что это происходило через считанные минуты после
        // монтирования. Дальше SIGTERM снова становился смертельным, и всё
        // держалось на гонке с ProcessExit.
        using var sigterm = System.Runtime.InteropServices.PosixSignalRegistration.Create(
            System.Runtime.InteropServices.PosixSignal.SIGTERM,
            context => { context.Cancel = true; stop.Set(); });

        // Подстраховка на случай, если сигнал всё же дошёл смертельным:
        // обработчик выхода обязан ДОЖДАТЬСЯ снятия тома, а не просто
        // попросить о нём. Иначе рантайм завершает процесс, пока основной
        // поток ещё внутри Dispose, и остаются висящая точка монтирования
        // и брошенная песочница — ровно то, что этот код должен предотвращать.
        EventHandler onExit = (_, _) =>
        {
            stop.Set();
            torndown.Wait(TimeSpan.FromSeconds(10));
        };
        AppDomain.CurrentDomain.ProcessExit += onExit;

        stop.Wait();
        Console.WriteLine($"unmounting {mountPoint}");
        mount.Dispose();
        torndown.Set();
        // Штатный путь прошёл — подстраховка больше не нужна и не должна
        // выполняться: ей нечего ждать, а ждать она умеет только десять секунд.
        AppDomain.CurrentDomain.ProcessExit -= onExit;
        return 0;
    }
#if GITFS_WINFSP
    catch (MountException e)
    {
        // Совет берётся из самой ошибки. Раньше сюда безусловно
        // приклеивалось «поставьте WinFsp» — и на «1: это не буква диска»
        // пользователь получал предложение переустановить драйвер, то есть
        // ложный след вместо подсказки.
        Fail(e.Message, "gitfs doctor checks the environment first");
        if (e.Message.Contains("WinFsp", StringComparison.OrdinalIgnoreCase))
            Console.Error.WriteLine($"     → install it from {Doctor.WinFspDownload} and run gitfs doctor again");
        return 1;
    }
#else
    catch (FuseMountException e)
    {
        return Fail(e.Message,
            "gitfs doctor checks libfuse3, /dev/fuse and fusermount3");
    }
#endif
#endif
}

/// <summary>Том живёт внутри процесса `gitfs mount`, поэтому снимает его
/// Ctrl+C в том окне — отдельной команде снимать нечего. Говорим это прямо,
/// а не «nothing is mounted» без объяснения.</summary>
/// <summary>Снимает том по точке монтирования.
///
/// Раньше у команды была ОДНА ветка — отказ: «том принадлежит тому процессу,
/// нажмите там Ctrl+C». Верно и совершенно бесполезно, когда окна того
/// процесса уже нет: единственным выходом оставался диспетчер задач.
///
/// Теперь рядом с замком песочницы записан её владелец, и снятие — это
/// вежливая просьба ему завершиться: тот же путь, что и Ctrl+C, со всей уже
/// написанной уборкой (том снимается, песочница удаляется). Убивать жёстко
/// нельзя — останется и точка монтирования, и каталог.</summary>
static int Unmount(string[] rest)
{
    var live = OverlayStore.FindLive();
    if (rest.Length == 0)
    {
        if (live.Count == 0)
        {
            Console.Error.WriteLine("fail nothing is mounted");
            Console.Error.WriteLine("     → gitfs list shows what gitfs is holding");
            return 1;
        }
        Console.Error.WriteLine("fail gitfs unmount needs a mount point");
        Console.Error.WriteLine("     → " + string.Join(", ", live.Select(m => m.MountPoint)));
        return 1;
    }

    var wanted = rest[0].TrimEnd('\\', '/');
    var target = live.FirstOrDefault(m =>
        string.Equals(m.MountPoint.TrimEnd('\\', '/'), wanted, StringComparison.OrdinalIgnoreCase));
    if (target is null)
    {
        Console.Error.WriteLine($"fail gitfs is not holding {rest[0]}");
        Console.Error.WriteLine(live.Count == 0
            ? "     → nothing is mounted right now"
            : "     → mounted: " + string.Join(", ", live.Select(m => m.MountPoint)));
        return 1;
    }
    if (target.ProcessId <= 0)
    {
        Console.Error.WriteLine($"fail {target.MountPoint} does not say which process holds it");
        Console.Error.WriteLine("     → it was mounted by an older gitfs; press Ctrl+C in that window");
        return 1;
    }

    try
    {
        using var owner = System.Diagnostics.Process.GetProcessById(target.ProcessId);
        // На Linux — SIGTERM, тот же сигнал, что уже умеет обрабатывать
        // сам mount: он снимает том и удаляет песочницу. Kill() в .NET —
        // это SIGKILL, после которого не отрабатывает ничего.
        //
        // На Windows сигналов нет, и Kill() — единственный способ; WinFsp
        // отпускает том вместе с процессом, а песочницу убираем ниже сами.
        if (OperatingSystem.IsLinux() && Interop.Kill(target.ProcessId, 15) != 0)
        {
            Fail($"could not signal pid {target.ProcessId}",
                "it may belong to another user; try from that session");
            return 1;
        }
        if (!OperatingSystem.IsLinux()) owner.Kill(entireProcessTree: false);

        // Ждём НАСТОЯЩЕГО снятия, а не отправки сигнала: сказать «снято» и
        // оставить точку монтирования висеть — худший исход из возможных.
        owner.WaitForExit(15_000);
        if (!owner.HasExited)
        {
            Console.Error.WriteLine($"fail {target.MountPoint} is still held by pid {target.ProcessId}");
            Console.Error.WriteLine("     → it did not finish in 15 s; look at gitfs list");
            return 1;
        }
    }
    catch (ArgumentException)
    {
        Console.Error.WriteLine($"fail pid {target.ProcessId} is gone but {target.MountPoint} is listed");
        Console.Error.WriteLine("     → gitfs purge removes what it left behind");
        return 1;
    }
    catch (Exception e)
    {
        Fail($"could not unmount {target.MountPoint}: {e.Message}",
            "gitfs list shows what is still held");
        return 1;
    }

    // Песочница на Windows остаётся: там процесс завершается жёстко и убрать
    // её было некому. Замок теперь свободен, каталог опознаётся сиротой —
    // удаляем ровно его, а не «все сироты», чтобы не задеть чужие тома.
    if (Directory.Exists(target.Root))
    {
        try { Directory.Delete(target.Root, recursive: true); }
        catch (IOException)
        {
            Console.Error.WriteLine($"     → sandbox left at {target.Root}; gitfs purge removes it");
        }
        catch (UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"     → sandbox left at {target.Root}; gitfs purge removes it");
        }
    }

    Console.WriteLine(Ansi.Ok("unmounted ") + target.MountPoint);
    return 0;
}


/// <summary>Убирает песочницы, оставшиеся от аварийно завершённых процессов.
/// Живые не трогает: владелец держит замок внутри своего каталога.</summary>
static int Purge()
{
    var (removed, failed) = OverlayStore.PurgeOrphans();
    foreach (var dir in removed) Console.WriteLine($"removed {dir}");
    foreach (var dir in failed)
        Fail($"could not remove {dir}",
            "something still has a file open there; close it and run gitfs purge again");
    if (removed.Count == 0 && failed.Count == 0) Console.WriteLine("overlay clean — nothing to purge");
    return failed.Count > 0 ? 1 : 0;
}

static int Unknown(string command)
{
    Console.Error.WriteLine($"fail unknown command '{command}'");
    Console.Error.WriteLine("     → run gitfs --help for the list");
    return 1;
}
