using System.Diagnostics;
using Gitfs.Diagnostics;
using Gitfs.Vfs;
using Gitfs.Vfs.Views;

// Замеры против бюджетов спеки §16. Каждый бюджет — утверждение, которое
// либо выполняется, либо нет; вывод показывает и то, и другое честно.

var repoPath = args.Length > 0 ? Path.GetFullPath(args[0]) : Directory.GetCurrentDirectory();
var gitDir = Doctor.ResolveGitDir(repoPath);
if (gitDir is null)
{
    Console.Error.WriteLine($"fail no .git directory in {repoPath}");
    return 1;
}

Console.WriteLine($"repository: {repoPath}");
Console.WriteLine($"objects:    {DirectorySize(Path.Combine(gitDir, "objects")) / 1024 / 1024} MB");
Console.WriteLine();

var results = new List<(string Budget, string Measured, bool Pass)>();
var names = NamePolicy.For(NamePolicyKind.Native);

VirtualTree BuildTree() => new(new IView[]
{
    new BranchesView(names), new TagsView(names), new CommitsView(names),
    new DatesView(names), new HistoryView(names),
});

// ---------- 1. Монтирование: < 500 мс независимо от размера истории ----------
{
    var sw = Stopwatch.StartNew();
    var manager = new SnapshotManager(gitDir);
    var tree = BuildTree();
    using var target = new VfsMountTarget(manager, tree, "bench");
    var _ = target.GetVolumeInfo();
    sw.Stop();
    Record("mount < 500 ms", $"{sw.Elapsed.TotalMilliseconds:0} ms",
        sw.Elapsed.TotalMilliseconds < 500);
}

// общий таргет для остальных замеров
var manager2 = new SnapshotManager(gitDir);
using var mount = new VfsMountTarget(manager2, BuildTree(), "bench");

// ---------- 2. Lookup: p50 < 1 мс, p99 < 10 мс ----------
{
    var paths = CollectPaths(mount, "branches/main", 400);
    // прогрев: бюджеты §16 заявлены на прогретом кэше
    foreach (var p in paths) mount.Lookup(p);

    var samples = new List<double>(paths.Count * 3);
    for (var round = 0; round < 3; round++)
    {
        foreach (var p in paths)
        {
            var sw = Stopwatch.StartNew();
            mount.Lookup(p);
            sw.Stop();
            samples.Add(sw.Elapsed.TotalMilliseconds);
        }
    }
    samples.Sort();
    var p50 = Percentile(samples, 0.50);
    var p99 = Percentile(samples, 0.99);
    Record("lookup p50 < 1 ms", $"{p50:0.000} ms", p50 < 1.0);
    Record("lookup p99 < 10 ms", $"{p99:0.000} ms", p99 < 10.0);
    Console.WriteLine($"           ({samples.Count} samples over {paths.Count} paths)");
}

// ---------- 3. Листинг директории: < 50 мс ----------
{
    var biggest = FindLargestDirectory(mount, "branches/main");
    mount.List(biggest.Path); // прогрев
    var sw = Stopwatch.StartNew();
    var listed = mount.List(biggest.Path);
    sw.Stop();
    var count = listed.IsOk ? listed.Value.Count() : 0;
    Record("listing < 50 ms", $"{sw.Elapsed.TotalMilliseconds:0.0} ms for {count} entries",
        sw.Elapsed.TotalMilliseconds < 50);
}

// ---------- 4. Последовательное чтение: > 100 МБ/с ----------
{
    var (path, size) = FindLargestFile(mount, "branches/main");
    if (size < 4096)
    {
        // на крошечных файлах измерять пропускную способность бессмысленно —
        // честнее пропустить, чем показать ноль и выдать его за провал
        Console.WriteLine($"skip  sequential read              largest file is {size} B");
    }
    else
    {
        var handle = mount.Open(path, OpenMode.Read).Value;
        var buffer = new byte[64 * 1024];
        var sw = Stopwatch.StartNew();
        long total = 0, rounds = 0;
        // гоняем один файл несколько раз, чтобы набрать измеримый объём
        while (sw.Elapsed.TotalMilliseconds < 300)
        {
            long offset = 0;
            while (true)
            {
                var read = mount.Read(handle, offset, buffer);
                if (!read.IsOk || read.Value == 0) break;
                offset += read.Value;
                total += read.Value;
            }
            rounds++;
        }
        sw.Stop();
        mount.Close(handle);
        var mbps = total / 1024.0 / 1024.0 / sw.Elapsed.TotalSeconds;
        Record("sequential read > 100 MB/s", $"{mbps:0} MB/s ({size / 1024} KB × {rounds})",
            mbps > 100);
    }
}

// ---------- 5. Первое открытие history/<файл>/ ----------
{
    var (path, _) = FindLargestFile(mount, "branches/main");
    var historyPath = "history/" + path["branches/main/".Length..];
    // холодный снапшот: кэш истории пуст
    var cold = new SnapshotManager(gitDir);
    using var coldMount = new VfsMountTarget(cold, BuildTree(), "bench-cold");
    var sw = Stopwatch.StartNew();
    var listed = coldMount.List(historyPath);
    var versions = listed.IsOk ? listed.Value.Count() : 0;
    sw.Stop();
    Record("history first open < 2000 ms",
        $"{sw.Elapsed.TotalMilliseconds:0} ms, {versions} versions over {CommitCount(gitDir)} commits",
        sw.Elapsed.TotalMilliseconds < 2000);

    // и он же прогретый — показывает эффект кэша истории
    var sw2 = Stopwatch.StartNew();
    coldMount.List(historyPath);
    sw2.Stop();
    Console.WriteLine($"           (warm: {sw2.Elapsed.TotalMilliseconds:0.00} ms)");
}

// ---------- 6. Память в покое ----------
{
    GC.Collect();
    GC.WaitForPendingFinalizers();
    GC.Collect();
    var mb = GC.GetTotalMemory(forceFullCollection: true) / 1024.0 / 1024.0;
    Record("managed heap < 320 MB", $"{mb:0} MB", mb < 320);
}

Console.WriteLine();
var passed = results.Count(r => r.Pass);
Console.WriteLine($"=== {passed}/{results.Count} budgets met ===");
foreach (var r in results.Where(r => !r.Pass))
    Console.WriteLine($"  MISSED  {r.Budget}: {r.Measured}");
return results.All(r => r.Pass) ? 0 : 1;

// ---------- вспомогательное ----------

void Record(string budget, string measured, bool pass)
{
    results.Add((budget, measured, pass));
    Console.WriteLine($"{(pass ? "ok  " : "MISS")}  {budget,-30} {measured}");
}

static double Percentile(List<double> sorted, double q)
{
    if (sorted.Count == 0) return 0;
    var index = (int)Math.Ceiling(q * sorted.Count) - 1;
    return sorted[Math.Clamp(index, 0, sorted.Count - 1)];
}

static List<string> CollectPaths(VfsMountTarget target, string root, int limit)
{
    var paths = new List<string>();
    var queue = new Queue<string>();
    queue.Enqueue(root);
    while (queue.Count > 0 && paths.Count < limit)
    {
        var current = queue.Dequeue();
        var listed = target.List(current);
        if (!listed.IsOk) continue;
        foreach (var entry in listed.Value)
        {
            var child = current + "/" + entry.Name;
            paths.Add(child);
            if (entry.Info.Kind == NodeKind.Directory && queue.Count < limit) queue.Enqueue(child);
            if (paths.Count >= limit) break;
        }
    }
    return paths;
}

static (string Path, int Count) FindLargestDirectory(VfsMountTarget target, string root)
{
    var best = (Path: root, Count: 0);
    var queue = new Queue<string>();
    queue.Enqueue(root);
    var visited = 0;
    while (queue.Count > 0 && visited++ < 200)
    {
        var current = queue.Dequeue();
        var listed = target.List(current);
        if (!listed.IsOk) continue;
        var entries = listed.Value.ToList();
        if (entries.Count > best.Count) best = (current, entries.Count);
        foreach (var entry in entries.Where(e => e.Info.Kind == NodeKind.Directory))
            queue.Enqueue(current + "/" + entry.Name);
    }
    return best;
}

static (string Path, long Size) FindLargestFile(VfsMountTarget target, string root)
{
    var best = (Path: "", Size: 0L);
    var queue = new Queue<string>();
    queue.Enqueue(root);
    var visited = 0;
    while (queue.Count > 0 && visited++ < 200)
    {
        var current = queue.Dequeue();
        var listed = target.List(current);
        if (!listed.IsOk) continue;
        foreach (var entry in listed.Value)
        {
            var child = current + "/" + entry.Name;
            if (entry.Info.Kind == NodeKind.Directory) queue.Enqueue(child);
            else if (entry.Info.Size > best.Size) best = (child, entry.Info.Size);
        }
    }
    return best;
}

static int CommitCount(string gitDir)
{
    try
    {
        var psi = new ProcessStartInfo("git", "rev-list --count HEAD")
        {
            WorkingDirectory = Path.GetDirectoryName(gitDir) ?? gitDir,
            RedirectStandardOutput = true,
            UseShellExecute = false,
        };
        using var p = Process.Start(psi)!;
        var output = p.StandardOutput.ReadToEnd().Trim();
        p.WaitForExit(5000);
        return int.TryParse(output, out var n) ? n : 0;
    }
    catch (Exception) { return 0; }
}

static long DirectorySize(string path)
{
    if (!Directory.Exists(path)) return 0;
    try
    {
        return Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories)
            .Sum(f => new FileInfo(f).Length);
    }
    catch (IOException) { return 0; }
}
