using Gitfs.Cli;
using Gitfs.Core;
using Gitfs.Vfs;
using Gitfs.Vfs.Views;
using Gitfs.Mount.WinFsp;

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
          gitfs mount <repo> <target>  mount a repository
          gitfs unmount <target>       unmount
          gitfs list                   list current mounts

        Views: branches, tags, commits, dates, history.
        """);
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
    var tree = new VirtualTree(new IView[] { new BranchesView(NamePolicy.For(NamePolicyKind.Native)) });
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
                          $"{node.Value.Size,10}  {node.Value.Timestamp:yyyy-MM-dd}  {path}");
        return 0;
    }
    foreach (var entry in tree.List(snapshot, path) ?? [])
    {
        var kind = entry.Info.Kind == NodeKind.Directory ? "dir" : entry.Info.Kind.ToString().ToLowerInvariant();
        var size = entry.Info.Kind == NodeKind.Directory ? "" : entry.Info.Size.ToString();
        Console.WriteLine($"{kind,-9} {size,10}  {entry.Info.Timestamp:yyyy-MM-dd}  {entry.Name}");
    }
    return 0;
}

static int List()
{
    Console.WriteLine("REPOSITORY        MOUNT  VIEWS              UPTIME");
    Console.WriteLine("(no mounts — the filesystem adapter lands in M3)");
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
    var tree = new VirtualTree(new IView[] { new BranchesView(NamePolicy.For(NamePolicyKind.Native)) });
    var name = new DirectoryInfo(repoPath).Name;
    var target = new VfsMountTarget(manager, tree, name);

    var started = DateTime.UtcNow;
    try
    {
        using var mount = GitfsMount.Mount(target, mountPoint,
            line => Console.Error.WriteLine($"     {line}"));
        var elapsed = (DateTime.UtcNow - started).TotalSeconds;
        Console.WriteLine($"mounted {mountPoint} · {name} · 1 view · {elapsed:0.0}s");
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
