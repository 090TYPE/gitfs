using Gitfs.App;
using Gitfs.Core.Tests.Fixtures;
using Gitfs.Vfs.Overlay;

namespace Gitfs.App.Tests;

/// <summary>Корень песочниц ОДИН на машину, а проверки в нём считают
/// каталоги. Пока классы шли параллельно, соседний тест успевал создать
/// свою песочницу между «до» и «после» — и проверка падала на том, к чему
/// не имела отношения. Общее имя коллекции выстраивает их в очередь.</summary>
[CollectionDefinition("overlay-root", DisableParallelization = true)]
public sealed class OverlayRootCollection { }

/// <summary>MountService был единственным компонентом без единого теста — и
/// за один день дал два дефекта: песочница не освобождалась вовсе, а вместе
/// с ней репозиторий оставался заблокированным до конца жизни приложения.
/// Оба живут в учёте ресурсов, а не в разборе git, и видны без драйвера.</summary>
[Collection("overlay-root")]
public class MountServiceTests
{
    private static RepoBuilder BuildRepo()
    {
        var repo = new RepoBuilder();
        repo.WriteFile("README.md", "content\n");
        repo.WriteFile("src/app.cs", "class A {}\n");
        repo.CommitAll("first");
        return repo;
    }

    private static readonly string[] AllViews =
        { "branches", "tags", "commits", "dates", "history" };

    // ---------- что приложение обещает пользователю ----------

    [Fact]
    public void The_mount_button_is_enabled_exactly_where_an_adapter_exists()
    {
        var service = new MountService();
        var expected = OperatingSystem.IsWindows() || OperatingSystem.IsLinux();
        Assert.Equal(expected, service.CanMount);
    }

    [Fact]
    public void A_blocked_button_always_says_why()
    {
        // «Смонтировать» без объяснения — тупик: пользователь не знает,
        // чего не хватает и можно ли это исправить
        var service = new MountService();
        if (service.CanMount) { Assert.Null(service.MountBlockedReason); return; }
        Assert.False(string.IsNullOrWhiteSpace(service.MountBlockedReason));
    }

    [Fact]
    public void The_tree_contains_exactly_the_views_that_were_asked_for()
    {
        using var repo = BuildRepo();
        using var manager = new Gitfs.Vfs.SnapshotManager(repo.GitDir);

        var only = MountService.BuildTree(new[] { "branches", "history" });
        var listed = only.List(manager.Current, "")!.Select(e => e.Name).ToList();
        Assert.Equal(new[] { "branches", "history" }, listed.OrderBy(n => n));

        var all = MountService.BuildTree(AllViews);
        Assert.Equal(AllViews.OrderBy(n => n),
            all.List(manager.Current, "")!.Select(e => e.Name).OrderBy(n => n));
    }

    [Fact]
    public void Asking_for_no_views_gives_an_empty_tree_rather_than_all_of_them()
    {
        using var repo = BuildRepo();
        using var manager = new Gitfs.Vfs.SnapshotManager(repo.GitDir);
        var none = MountService.BuildTree(Array.Empty<string>());
        Assert.Empty(none.List(manager.Current, "")!);
    }

    [Fact]
    public void Free_drive_letters_never_offers_one_that_is_taken()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Empty(MountService.FreeDriveLetters());
            return;
        }
        var used = DriveInfo.GetDrives().Select(d => d.Name[0]).ToHashSet();
        var offered = MountService.FreeDriveLetters();
        Assert.All(offered, letter => Assert.DoesNotContain(letter, used));
        Assert.All(offered, letter => Assert.InRange(letter, 'G', 'Z'));
    }

    [Fact]
    public void A_path_that_is_not_a_repository_is_refused_before_anything_is_created()
    {
        var service = new MountService();
        if (!service.CanMount) return;

        var empty = Path.Combine(Path.GetTempPath(), "gitfs-app-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(empty);
        try
        {
            var before = SandboxCount();
            var e = Assert.Throws<InvalidOperationException>(
                () => service.Mount(empty, FreeMountPoint(), AllViews));
            Assert.Contains(".git", e.Message);
            // и никакой песочницы по дороге не осталось
            Assert.Equal(before, SandboxCount());
            Assert.Empty(service.Entries);
        }
        finally { Directory.Delete(empty, recursive: true); }
    }

    /// <summary>Самый ценный тест здесь: неудачное монтирование не должно
    /// оставлять песочницу. Проверяется без драйвера — точка монтирования
    /// заведомо непригодна, и до создания тома дело не доходит.
    ///
    /// Считаются КАТАЛОГИ, а не то, что FindOrphans назовёт сиротой: сразу
    /// после броска замок ещё удерживается незавершённым объектом, поэтому
    /// брошенная песочница сиротой не выглядит. Первая версия этого теста
    /// именно на этом и зеленела при намеренно убранной починке.</summary>
    [Fact]
    public void A_failed_mount_leaves_no_sandbox_behind()
    {
        var service = new MountService();
        if (!service.CanMount) return;

        using var repo = BuildRepo();
        var before = SandboxCount();

        Assert.ThrowsAny<Exception>(
            () => service.Mount(repo.Root, ImpossibleMountPoint(), AllViews));

        Assert.Equal(before, SandboxCount());
        Assert.Empty(service.Entries);
    }

    /// <summary>Считаются только СВОИ песочницы. Корень общий на машину:
    /// туда пишут и другие тестовые сборки, идущие параллельно, и настоящий
    /// gitfs, если он в эту минуту смонтирован. Счёт всех подряд делал
    /// проверку зависимой от посторонних процессов — она и падала в общем
    /// прогоне решения, оставаясь зелёной поодиночке.</summary>
    internal static int SandboxCount()
    {
        var root = OverlayStore.DefaultRoot();
        if (!Directory.Exists(root)) return 0;
        var mine = Environment.ProcessId + "-";
        return Directory.GetDirectories(root)
            .Count(d => Path.GetFileName(d).StartsWith(mine, StringComparison.Ordinal));
    }

    /// <summary>Точка монтирования, которую адаптер обязан отвергнуть НЕ
    /// обращаясь к драйверу. Первая версия брала здесь системный диск — и
    /// на машине с установленным WinFsp попытка встать поверх C: не
    /// возвращалась вовсе: этот тест подвесил CI на сорок минут. Тест не
    /// имеет права трогать драйвер ради проверки учёта ресурсов.</summary>
    private static string ImpossibleMountPoint() => OperatingSystem.IsWindows()
        ? "1:"                                                  // не буква диска
        : "/nonexistent-" + Guid.NewGuid().ToString("N")[..8];  // каталога нет

    [Fact]
    public void A_failed_mount_does_not_leave_the_repository_locked()
    {
        // вторая половина того же дефекта: SnapshotManager держал открытыми
        // файлы пакетов, и после неудачи репозиторий нельзя было удалить
        var service = new MountService();
        if (!service.CanMount) return;

        var repo = BuildRepo();
        repo.Repack();                    // теперь есть packfile, который можно удержать
        var root = repo.Root;

        Assert.ThrowsAny<Exception>(() => service.Mount(root, ImpossibleMountPoint(), AllViews));

        // Здесь НЕ должно быть GC.Collect(). Первая версия звала его прямо
        // перед проверкой — и утечка исчезала сама: недостижимые объекты
        // финализировались, репозиторий разблокировался, тест зеленел над
        // настоящим дефектом. Освобождение обязано быть сделано кодом, а не
        // сборщиком мусора когда-нибудь потом.
        foreach (var f in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            try { File.SetAttributes(f, FileAttributes.Normal); } catch { }
        }
        var problem = TryDelete(root);
        Assert.True(problem is null, $"the repository stayed locked after a failed mount: {problem}");

        // На Linux у этой проверки нет зубов: там открытый файл спокойно
        // удаляется. Говорим это прямо, а не делаем вид, что покрыты обе.
        if (!OperatingSystem.IsWindows())
            Assert.True(true, "this assertion only has teeth on Windows");
    }

    // ---------- учёт монтирований ----------

    [Fact]
    public void Entries_is_a_copy_so_the_ui_cannot_see_a_half_built_list()
    {
        // список читается из потока интерфейса, а меняется из фонового:
        // отдать наружу живую коллекцию значит однажды поймать исключение
        // посреди отрисовки
        var service = new MountService();
        var first = service.Entries;
        var second = service.Entries;
        Assert.NotSame(first, second);
    }

    [Fact]
    public void Unmounting_something_that_was_never_mounted_is_harmless()
    {
        var service = new MountService();
        var ghost = new MountEntry("ghost", "C:/nowhere", "Z:", "bt cdh", DateTimeOffset.UtcNow);
        service.Unmount(ghost);          // не бросает
        service.UnmountAll();            // и на пустом списке тоже
        Assert.Empty(service.Entries);
    }

    [Fact]
    public void Uptime_reads_as_minutes_then_hours()
    {
        var now = DateTimeOffset.UtcNow;
        Assert.Equal("0m", Entry(now).Uptime);
        Assert.Equal("5m", Entry(now.AddMinutes(-5)).Uptime);
        Assert.Equal("1h 30m", Entry(now.AddMinutes(-90)).Uptime);
        Assert.Equal("25h 00m", Entry(now.AddHours(-25)).Uptime);

        static MountEntry Entry(DateTimeOffset since) =>
            new("r", "p", "G:", "bt cdh", since);
    }

    // ---------- служебное ----------

    private static string FreeMountPoint()
    {
        if (OperatingSystem.IsWindows())
        {
            var free = MountService.FreeDriveLetters();
            return free.Count > 0 ? free[^1] + ":" : "Z:";
        }
        var dir = Path.Combine(Path.GetTempPath(), "gitfs-mnt-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static string? TryDelete(string root)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try { Directory.Delete(root, recursive: true); return null; }
            catch (IOException e) { if (attempt == 2) return e.Message; Thread.Sleep(200); }
            catch (UnauthorizedAccessException e) { if (attempt == 2) return e.Message; Thread.Sleep(200); }
        }
        return null;
    }
}
