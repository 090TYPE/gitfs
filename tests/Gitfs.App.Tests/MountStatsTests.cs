using Gitfs.App;
using Gitfs.Core.Tests.Fixtures;

namespace Gitfs.App.Tests;

/// <summary>Счётчики кэша и хвост журнала — панель деталей тома (макет 03).
/// Числа в интерфейсе стоят дороже своей стоимости: неверные хуже
/// отсутствующих, потому что по ним принимают решения.</summary>
[Collection("overlay-root")]
public class MountStatsTests
{
    [Fact]
    public void A_volume_this_process_does_not_hold_has_no_counters()
    {
        // и это НЕ нули: «0 попаданий из 0» выглядит как простаивающий кэш,
        // а на деле счётчиков просто нет
        var service = new MountService();
        Assert.Null(service.StatsFor("Q:"));
        Assert.Null(service.StatsFor("/nowhere"));
    }

    [Fact]
    public void A_fresh_cache_reports_that_nothing_was_read_rather_than_zero_percent()
    {
        var stats = new MountService.MountStats(
            0, 0, 0, 1024, 0, 0, 0, 0, 0, 0, 1, 0, 0, 0, 0);
        Assert.Null(stats.HitRate);
    }

    [Fact]
    public void The_hit_rate_counts_every_cache_together()
    {
        var stats = new MountService.MountStats(
            TreeHits: 6, TreeMisses: 2, TreeBytes: 0, TreeBudget: 0,
            ListingHits: 2, ListingMisses: 0,
            PathHits: 0, PathMisses: 0,
            DeltaHits: 0, SizeHits: 0, Packs: 1, PackedObjects: 0, Reopens: 0,
            OverlayFiles: 0, OverlayBytes: 0);
        Assert.Equal(0.8, stats.HitRate!.Value, 3);   // 8 из 10
    }

    /// <summary>Счётчики обязаны РАСТИ от чтения. Число, которое стоит на
    /// месте, ничем не отличается от нарисованного.</summary>
    [Fact]
    public void Reading_the_tree_moves_the_counters()
    {
        using var repo = new RepoBuilder();
        repo.WriteFile("a.txt", "one\n");
        repo.WriteFile("dir/b.txt", "two\n");
        repo.CommitAll("first");

        using var manager = new Gitfs.Vfs.SnapshotManager(repo.GitDir);
        var tree = MountService.BuildTree(new[] { "branches", "history" });
        using var lease = manager.Acquire();
        var snapshot = lease.Snapshot;

        var before = snapshot.TreeCache.Hits + snapshot.TreeCache.Misses;
        foreach (var _ in tree.List(snapshot, "branches/main")!) { }
        foreach (var _ in tree.List(snapshot, "branches/main")!) { }
        var after = snapshot.TreeCache.Hits + snapshot.TreeCache.Misses;

        Assert.True(after > before, "reading the tree left the counters untouched");
        Assert.True(snapshot.TreeCache.Hits > 0,
            "the second listing of the same directory was not a cache hit");
    }

    // ---------- хвост журнала ----------

    [Fact]
    public void An_empty_log_is_an_empty_tail_and_not_a_crash()
    {
        var original = Program.LogPath;
        Program.LogPath = Path.Combine(Path.GetTempPath(),
            "gitfs-log-" + Guid.NewGuid().ToString("N"), "app.log");
        try { Assert.Empty(Program.TailLog(6)); }
        finally { Program.LogPath = original; }
    }

    [Fact]
    public void The_tail_is_the_last_lines_in_order()
    {
        WithLog(path =>
        {
            File.WriteAllLines(path, Enumerable.Range(1, 20).Select(i => "line " + i));
            var tail = Program.TailLog(3);
            Assert.Equal(new[] { "line 18", "line 19", "line 20" }, tail);
        });
    }

    /// <summary>Стек исключения — одна строка. Иначе шесть последних событий
    /// в панели превращаются в шесть обрывков одного.</summary>
    [Fact]
    public void A_multi_line_exception_becomes_a_single_line()
    {
        WithLog(path =>
        {
            Exception caught;
            try { throw new InvalidOperationException("boom"); }
            catch (Exception e) { caught = e; }

            Program.Log("test", caught);
            Program.Log("test", new InvalidOperationException("second"));

            var lines = File.ReadAllLines(path);
            Assert.Equal(2, lines.Length);
            Assert.Contains("boom", lines[0]);
            Assert.Contains("second", lines[1]);
            Assert.Equal(2, Program.TailLog(10).Count);
        });
    }

    /// <summary>Журнал открыт этим же процессом на дозапись. Эксклюзивное
    /// чтение отдавало бы «файл занят» вместо содержимого — то есть панель
    /// оставалась бы пустой ровно тогда, когда в журнале что-то есть.</summary>
    [Fact]
    public void The_tail_reads_a_log_that_is_open_for_writing()
    {
        WithLog(path =>
        {
            File.WriteAllLines(path, new[] { "first" });
            using var held = new FileStream(path, FileMode.Append, FileAccess.Write,
                FileShare.ReadWrite);
            using var writer = new StreamWriter(held) { AutoFlush = true };
            writer.WriteLine("second");

            var tail = Program.TailLog(5);
            Assert.Equal(new[] { "first", "second" }, tail);
        });
    }

    private static void WithLog(Action<string> body)
    {
        var original = Program.LogPath;
        var dir = Path.Combine(Path.GetTempPath(), "gitfs-log-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        Program.LogPath = Path.Combine(dir, "app.log");
        try { body(Program.LogPath); }
        finally
        {
            Program.LogPath = original;
            try { Directory.Delete(dir, recursive: true); } catch (Exception) { }
        }
    }
}
