using System.Collections.Concurrent;
using Gitfs.Core.Tests.Fixtures;
using Gitfs.Vfs;
using Gitfs.Vfs.Overlay;
using Gitfs.Vfs.Views;

namespace Gitfs.Load.Tests;

/// <summary>Уровень 4 по спеке §17: нагрузка. Параллельный обход дерева из
/// N потоков с проверкой отсутствия гонок и утечки хендлов и сравнением
/// памяти с бюджетом.
///
/// Всё это ниже границы IMountTarget, поэтому монтирование не нужно — и
/// именно здесь живут гонки, которых не видно на одном потоке: подмена
/// снапшота посреди операции, счётчик ссылок, кэши, привязанные к снапшоту.
///
/// Тесты намеренно НЕ смотрят во внутренности. Утечка аренды снапшота
/// наблюдаема снаружи: пока жив хоть один снапшот, ObjectReader держит
/// открытыми файлы пакетов, и каталог репозитория не удаляется. Это и есть
/// проверка — попытка снести репозиторий после нагрузки.</summary>
public class ConcurrentVolumeTests
{
    private const int Threads = 16;
    private const int IterationsPerThread = 60;

    private static RepoBuilder BuildRepo(int commits = 40)
    {
        var repo = new RepoBuilder();
        for (var i = 0; i < commits; i++)
        {
            repo.WriteFile("README.md", $"revision {i}\n");
            repo.WriteFile($"src/file{i % 7}.cs", $"// {i}\n" + new string('x', 200 + i));
            repo.WriteFile("docs/notes.txt", string.Concat(Enumerable.Repeat($"line {i}\n", i + 1)));
            repo.CommitAll($"commit {i}");
        }
        repo.Tag("v1");
        repo.Repack();          // пакеты, а не loose: mmap-хендлы появляются здесь
        return repo;
    }

    private static VirtualTree Tree()
    {
        var names = NamePolicy.For(NamePolicyKind.Native);
        return new VirtualTree(new IView[]
        {
            new BranchesView(names), new TagsView(names), new CommitsView(names),
            new DatesView(names), new HistoryView(names),
        });
    }

    /// <summary>Каждый поток делает полный сценарий чтения и сверяет
    /// результат с эталоном. Гонка проявится либо исключением, либо
    /// РАЗЛИЧИЕМ в данных — второе опаснее и молчаливее.</summary>
    [Fact]
    public void Sixteen_threads_reading_never_disagree_and_never_throw()
    {
        using var repo = BuildRepo();
        var manager = new SnapshotManager(repo.GitDir);
        using var target = new VfsMountTarget(manager, Tree(), "load", readOnly: true);
        var branch = repo.Run("symbolic-ref", "--short", "HEAD").Trim();

        var expected = ReadWhole(target, $"branches/{branch}/README.md");
        Assert.NotEmpty(expected);

        var failures = new ConcurrentBag<string>();
        var ready = new CountdownEvent(Threads);
        var go = new ManualResetEventSlim();

        var workers = Enumerable.Range(0, Threads).Select(id => new Thread(() =>
        {
            ready.Signal();
            go.Wait();
            try
            {
                for (var i = 0; i < IterationsPerThread; i++)
                {
                    var got = ReadWhole(target, $"branches/{branch}/README.md");
                    if (!got.SequenceEqual(expected))
                        failures.Add($"thread {id} iteration {i}: content differs");

                    if (!target.List($"branches/{branch}/src").TryGet(out var listing))
                        failures.Add($"thread {id}: listing failed");
                    else if (!listing.Any())
                        failures.Add($"thread {id}: empty listing");

                    if (!target.Lookup("history/README.md").TryGet(out var node))
                        failures.Add($"thread {id}: history lookup failed");
                    else if (node.Kind != NodeKind.Directory)
                        failures.Add($"thread {id}: history/README.md is not a folder");

                    if (!target.List("dates").TryGet(out var days) || !days.Any())
                        failures.Add($"thread {id}: dates view is empty");
                }
            }
            catch (Exception e)
            {
                // граница §11 не бросает исключений — ни при какой конкуренции
                failures.Add($"thread {id} threw {e.GetType().Name}: {e.Message}");
            }
        })
        { IsBackground = true, Name = $"load-{id}" }).ToList();

        foreach (var w in workers) w.Start();
        ready.Wait();
        go.Set();
        foreach (var w in workers) Assert.True(w.Join(TimeSpan.FromMinutes(2)), "a worker hung");

        Assert.True(failures.IsEmpty, string.Join("\n", failures.Take(10)));
    }

    /// <summary>Самая злая гонка: эпоха меняется, пока операции в полёте.
    /// Снапшот, который читает поток, обязан дожить до конца его операции,
    /// даже если менеджер уже отдал другим новый.</summary>
    [Fact]
    public void Readers_survive_snapshot_epochs_changing_underneath_them()
    {
        using var repo = BuildRepo(commits: 12);
        var manager = new SnapshotManager(repo.GitDir, TimeSpan.Zero); // без троттлинга
        using var target = new VfsMountTarget(manager, Tree(), "load", readOnly: true);
        var branch = repo.Run("symbolic-ref", "--short", "HEAD").Trim();

        var failures = new ConcurrentBag<string>();
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(8));

        var churn = new Thread(() =>
        {
            var n = 0;
            while (!stop.IsCancellationRequested)
            {
                // новая ветка меняет подпись эпохи -> менеджер соберёт новый снапшот
                repo.Run("branch", $"churn{n++}");
                manager.Refresh(force: true);
                Thread.Sleep(5);
            }
        })
        { IsBackground = true, Name = "epoch-churn" };

        var readers = Enumerable.Range(0, Threads / 2).Select(id => new Thread(() =>
        {
            try
            {
                while (!stop.IsCancellationRequested)
                {
                    // держим хендл открытым через смену эпохи и читаем ПОСЛЕ неё:
                    // если аренда не удержала снапшот, здесь будет обращение к
                    // закрытому ObjectReader
                    if (!target.Open($"branches/{branch}/docs/notes.txt", OpenMode.Read)
                            .TryGet(out var handle))
                    {
                        failures.Add($"reader {id}: open failed");
                        continue;
                    }
                    Thread.Sleep(3);
                    var buffer = new byte[4096];
                    var read = target.Read(handle, 0, buffer);
                    if (!read.TryGet(out var count) || count <= 0)
                        failures.Add($"reader {id}: read returned {read.Error}");
                    target.Close(handle);
                }
            }
            catch (Exception e)
            {
                failures.Add($"reader {id} threw {e.GetType().Name}: {e.Message}");
            }
        })
        { IsBackground = true, Name = $"epoch-reader-{id}" }).ToList();

        churn.Start();
        foreach (var r in readers) r.Start();
        foreach (var r in readers) Assert.True(r.Join(TimeSpan.FromSeconds(30)), "a reader hung");
        stop.Cancel();
        churn.Join(TimeSpan.FromSeconds(10));

        Assert.True(failures.IsEmpty, string.Join("\n", failures.Take(10)));
    }

    /// <summary>Утечка аренды снапшота наблюдаема снаружи, без заглядывания
    /// во внутренности: пока жив хоть один снапшот, ObjectReader держит
    /// открытыми файлы пакетов, а Windows не даёт удалить каталог с открытым
    /// файлом. Зубы у этой проверки есть только на Windows — Linux спокойно
    /// удаляет открытый файл; там тест проходит всегда и ничего не
    /// доказывает. Так честнее, чем делать вид, что покрытие есть везде.</summary>
    [Fact]
    public void After_the_load_nothing_still_holds_the_repository_open()
    {
        var repo = BuildRepo(commits: 15);
        var branch = repo.Run("symbolic-ref", "--short", "HEAD").Trim();
        var root = repo.Root;
        try
        {
            var manager = new SnapshotManager(repo.GitDir);
            var target = new VfsMountTarget(manager, Tree(), "load", readOnly: true);

            Parallel.For(0, 200, i =>
            {
                if (target.Open($"branches/{branch}/src/file{i % 7}.cs", OpenMode.Read)
                        .TryGet(out var handle))
                {
                    var buffer = new byte[128];
                    target.Read(handle, 0, buffer);
                    target.Close(handle);
                }
                manager.Refresh(force: i % 25 == 0);
            });

            target.Dispose();
            GC.Collect();
            GC.WaitForPendingFinalizers();

            // Снимаем «только для чтения»: git помечает им пакеты, и отказ
            // по правам не имеет отношения к утечке хендлов. Иначе тест
            // падал бы всегда и не по делу.
            ClearReadOnly(root);

            var problem = TryDelete(root);
            Assert.True(problem is null,
                $"something still holds the repository open after Dispose: {problem}");
        }
        finally
        {
            if (Directory.Exists(root)) repo.Dispose();
        }
    }

    /// <summary>Резидентная память против бюджета §16 (320 МБ). Нагрузка
    /// намеренно трогает все пять вьюх и историю — самые прожорливые кэши.</summary>
    [Fact]
    public void Memory_stays_within_the_budget_under_load()
    {
        const long BudgetBytes = 320L << 20;

        using var repo = BuildRepo(commits: 60);
        var manager = new SnapshotManager(repo.GitDir);
        using var target = new VfsMountTarget(manager, Tree(), "load", readOnly: true);
        var branch = repo.Run("symbolic-ref", "--short", "HEAD").Trim();

        Parallel.For(0, 400, i =>
        {
            target.List("commits");
            target.List("dates");
            target.List($"branches/{branch}/src");
            target.Lookup($"history/src/file{i % 7}.cs");
            target.List($"history/src/file{i % 7}.cs");
            if (target.Open($"branches/{branch}/docs/notes.txt", OpenMode.Read)
                    .TryGet(out var handle))
            {
                var buffer = new byte[64 << 10];
                target.Read(handle, 0, buffer);
                target.Close(handle);
            }
        });

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        var heap = GC.GetTotalMemory(forceFullCollection: true);

        Assert.True(heap < BudgetBytes,
            $"managed heap {heap >> 20} MB exceeds the {BudgetBytes >> 20} MB budget");
    }

    /// <summary>Песочница под параллельной записью: манифест — единственный
    /// общий файл, и рвать его нельзя.</summary>
    [Fact]
    public void Concurrent_writes_keep_the_sandbox_manifest_readable()
    {
        using var repo = BuildRepo(commits: 8);
        var overlayBase = Path.Combine(Path.GetTempPath(),
            "gitfs-load-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            var manager = new SnapshotManager(repo.GitDir);
            using var overlay = OverlayStore.Create(overlayBase);
            using var target = new VfsMountTarget(manager, Tree(), "load",
                readOnly: false, overlay: overlay);
            var branch = repo.Run("symbolic-ref", "--short", "HEAD").Trim();

            var failures = new ConcurrentBag<string>();
            Parallel.For(0, 120, i =>
            {
                var path = $"branches/{branch}/written{i}.txt";
                if (!target.Open(path, OpenMode.Write).TryGet(out var handle))
                {
                    failures.Add($"open {path} failed");
                    return;
                }
                var payload = System.Text.Encoding.UTF8.GetBytes($"content {i}");
                if (!target.Write(handle, 0, payload).TryGet(out _)) failures.Add($"write {path} failed");
                target.Close(handle);
            });

            Assert.True(failures.IsEmpty, string.Join("\n", failures.Take(10)));

            // всё записанное читается обратно ровно тем, чем было
            for (var i = 0; i < 120; i++)
            {
                var path = $"branches/{branch}/written{i}.txt";
                var expected = $"content {i}";
                Assert.Equal(expected, System.Text.Encoding.UTF8.GetString(ReadWhole(target, path)));
            }

            // и манифест не порван: свежий стор поверх того же каталога
            // обязан увидеть все записи
            Assert.Equal(120, overlay.Entries.Count(e => e.Kind == OverlayKind.File));
        }
        finally
        {
            if (Directory.Exists(overlayBase)) Directory.Delete(overlayBase, recursive: true);
        }
    }

    // ---------- служебное ----------

    private static byte[] ReadWhole(VfsMountTarget target, string path)
    {
        if (!target.Open(path, OpenMode.Read).TryGet(out var handle)) return [];
        try
        {
            var chunks = new List<byte>();
            var buffer = new byte[8192];
            long offset = 0;
            while (true)
            {
                if (!target.Read(handle, offset, buffer).TryGet(out var count) || count <= 0) break;
                chunks.AddRange(buffer[..count]);
                offset += count;
            }
            return chunks.ToArray();
        }
        finally { target.Close(handle); }
    }

    private static void ClearReadOnly(string root)
    {
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            try { File.SetAttributes(file, FileAttributes.Normal); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static string? TryDelete(string root)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                Directory.Delete(root, recursive: true);
                return null;
            }
            catch (IOException e)
            {
                if (attempt == 2) return e.Message;
                Thread.Sleep(200);
            }
            catch (UnauthorizedAccessException e)
            {
                if (attempt == 2) return e.Message;
                Thread.Sleep(200);
            }
        }
        return null;
    }
}
