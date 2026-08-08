using Gitfs.Cli;
using Gitfs.Core;
using Gitfs.Vfs;
using Gitfs.Vfs.Views;
using Gitfs.Mount.WinFsp;
using Gitfs.Vfs.Overlay;

if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
{
    PrintUsage();
    return 0;
}

try
{
    return args[0] switch
    {
        "doctor" => Doctor(args.Skip(1).ToArray()),
        "tree" => Tree(args.Skip(1).ToArray()),
        "cat" => Cat(args.Skip(1).ToArray()),
        "list" => List(),
        "mount" => Mount(args.Skip(1).ToArray()),
        "unmount" => Unmount(),
        _ => Unknown(args[0]),
    };
}
catch (Exception e)
{
    Console.Error.WriteLine($"fail {e.Message}");
    return 1;
}

static void PrintUsage()
{
    Console.WriteLine("""
        gitfs — git history mounted as a drive

          gitfs doctor [<repo>]        check the environment (and a repository)
          gitfs tree <repo> [<path>]   walk the virtual tree without mounting
          gitfs cat <repo> <path>      print a file from the virtual tree
          gitfs mount <repo> <target>  mount a repository
          gitfs unmount <target>       unmount
          gitfs list                   list current mounts

        Views: branches, tags, commits, dates, history.
        """);
}

/// <summary>Все пять вьюх спеки §3 плюс политика имён текущей платформы.</summary>
static VirtualTree BuildTree()
{
    var names = NamePolicy.For(NamePolicyKind.Native);
    return new VirtualTree(new IView[]
    {
        new BranchesView(names),
        new TagsView(names),
        new CommitsView(names),
        new DatesView(names),
        new HistoryView(names),
    });
}

static int Doctor(string[] rest)
{
    var repo = rest.FirstOrDefault();
    var checks = Diagnostics.Run(repo is null ? null : Path.GetFullPath(repo));
    Console.Write(Report.Render(checks));
    return Report.ExitCode(checks);
}

/// <summary>Обход виртуального дерева без монтирования: то же, что увидит
/// Проводник, когда появится адаптер. Полезно и как диагностика, и для GIF.</summary>
static int Tree(string[] rest)
{
    if (rest.Length == 0)
    {
        Console.Error.WriteLine("fail gitfs tree needs a repository path");
        return 1;
    }
    var gitDir = Diagnostics.ResolveGitDir(Path.GetFullPath(rest[0]));
    if (gitDir is null)
    {
        Console.Error.WriteLine($"fail no .git directory in {rest[0]}");
        Console.Error.WriteLine("     → point gitfs at a repository root");
        return 1;
    }

    using var snapshot = RepoSnapshot.Load(gitDir);
    var tree = BuildTree();
    var path = rest.Length > 1 ? rest[1] : "/";

    var node = tree.Resolve(snapshot, path);
    if (node is null)
    {
        Console.Error.WriteLine($"fail no such path: {path}");
        return 1;
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
    var gitDir = Diagnostics.ResolveGitDir(Path.GetFullPath(rest[0]));
    if (gitDir is null)
    {
        Console.Error.WriteLine($"fail no .git directory in {rest[0]}");
        return 1;
    }

    var manager = new SnapshotManager(gitDir);
    using var target = new VfsMountTarget(manager, BuildTree(),
        new DirectoryInfo(Path.GetFullPath(rest[0])).Name);

    var opened = target.Open(rest[1], OpenMode.Read);
    if (!opened.TryGet(out var handle))
    {
        Console.Error.WriteLine($"fail cannot open {rest[1]}: {opened.Error}");
        return 1;
    }
    try
    {
        if (handle.IsDirectory)
        {
            Console.Error.WriteLine($"fail {rest[1]} is a directory");
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
                Console.Error.WriteLine($"fail read failed: {read.Error}");
                return 1;
            }
            if (count == 0) break;
            stdout.Write(buffer, 0, count);
            offset += count;
        }
        return 0;
    }
    finally { target.Close(handle); }
}

static int List()
{
    Console.WriteLine("REPOSITORY        MOUNT  VIEWS              UPTIME");
    Console.WriteLine("(no mounts in this process)");
    var orphans = OverlayStore.FindOrphans();
    if (orphans.Count > 0)
    {
        Console.WriteLine();
        Console.WriteLine($"{orphans.Count} orphaned overlay(s) from earlier runs:");
        foreach (var dir in orphans) Console.WriteLine($"  {dir}");
    }
    return 0;
}

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

    var checks = Diagnostics.Run(repoPath);
    var blockers = checks.Where(c => c.Status == CheckStatus.Fail).ToList();
    if (blockers.Count > 0)
    {
        Console.Error.Write(Report.Render(blockers));
        return 1;
    }

    var gitDir = Diagnostics.ResolveGitDir(repoPath)!;
    var manager = new SnapshotManager(gitDir);
    var tree = BuildTree();
    var name = new DirectoryInfo(repoPath).Name;
    // overlay включён: без него Word и Excel не откроют файл из старого
    // коммита — они пишут lock-файлы рядом с открываемым (спека §10)
    var overlay = OverlayStore.Create();
    var target = new VfsMountTarget(manager, tree, name, readOnly: false, overlay: overlay);

    var started = DateTime.UtcNow;
    try
    {
        using var mount = GitfsMount.Mount(target, mountPoint,
            line => Console.Error.WriteLine($"     {line}"));
        var elapsed = (DateTime.UtcNow - started).TotalSeconds;
        Console.WriteLine($"mounted {mountPoint} · {name} · 5 views · {elapsed:0.0}s");
        Console.WriteLine("press Ctrl+C to unmount");

        using var stop = new ManualResetEventSlim();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.Set(); };
        stop.Wait();
        Console.WriteLine($"unmounting {mountPoint}");
        return 0;
    }
    catch (MountException e)
    {
        Console.Error.WriteLine($"fail {e.Message}");
        Console.Error.WriteLine($"     → install it from {Diagnostics.WinFspDownload} and run gitfs doctor again");
        return 1;
    }
}

static int Unmount()
{
    Console.Error.WriteLine("fail nothing is mounted");
    return 1;
}

static int Unknown(string command)
{
    Console.Error.WriteLine($"fail unknown command '{command}'");
    Console.Error.WriteLine("     → run gitfs --help for the list");
    return 1;
}
