using Gitfs.Core.Tests.Fixtures;
using Gitfs.Vfs.Overlay;
using Gitfs.Vfs.Views;

namespace Gitfs.Vfs.Tests;

/// <summary>Аренда снапшота берётся один раз за операцию и отпускается
/// ровно один раз (спека §8). Цена ошибки здесь несоразмерна её размеру:
/// лишний Release освобождает снапшот, который менеджер продолжает
/// публиковать, TryAddRef с нуля не проходит никогда — и каждый следующий
/// Acquire крутится вечно. На Linux однопоточный цикл FUSE превращает это
/// в намертво зависший том.</summary>
public class SnapshotLeaseTests : IDisposable
{
    private readonly string _overlayBase = Path.Combine(Path.GetTempPath(),
        "gitfs-lease-" + Guid.NewGuid().ToString("N")[..8]);

    public void Dispose()
    {
        if (Directory.Exists(_overlayBase)) Directory.Delete(_overlayBase, recursive: true);
    }

    private static RepoBuilder BuildRepo()
    {
        var repo = new RepoBuilder();
        repo.WriteFile("README.md", "content\n");
        repo.CommitAll("first");
        return repo;
    }

    private (VfsMountTarget Target, OverlayStore Store, SnapshotManager Manager) Open(RepoBuilder repo)
    {
        var names = NamePolicy.Windows;
        var manager = new SnapshotManager(repo.GitDir);
        var tree = new VirtualTree(new IView[] { new BranchesView(names) });
        var store = OverlayStore.Create(_overlayBase, names: names);
        return (new VfsMountTarget(manager, tree, "fixture", readOnly: false, overlay: store),
            store, manager);
    }

    /// <summary>Подкладывает КАТАЛОГ на место файла песочницы: любая
    /// попытка записи по этому виртуальному пути обязана отказать.
    /// Удалить каталог песочницы целиком нельзя — его держит собственный
    /// замок, и тест падал бы не по делу.</summary>
    private static void MakeWritesFail(OverlayStore store, string virtualPath)
    {
        var key = virtualPath.Replace('\\', '/').Trim('/');   // как OverlayStore.Normalize
        var storage = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(key))).ToLowerInvariant();
        Directory.CreateDirectory(Path.Combine(store.Root, storage));
    }

    /// <summary>Запись по несуществующему пути, когда песочница отказывает.
    /// Раньше аренда отпускалась дважды: один раз веткой, второй — catch.</summary>
    [Fact]
    public void A_failing_create_does_not_release_the_lease_twice()
    {
        using var repo = BuildRepo();
        var (target, store, manager) = Open(repo);
        using var _t = target;

        MakeWritesFail(store, "branches/main/created.txt");

        // Операция обязана отказать кодом, а не исключением (спека §11)…
        var failed = target.Open("branches/main/created.txt", OpenMode.Write);
        Assert.False(failed.IsOk);

        // …и главное — снапшот обязан остаться живым. До исправления
        // счётчик уходил в ноль, и следующая строка не возвращалась вовсе.
        var lookup = target.Lookup("branches/main/README.md");
        Assert.True(lookup.IsOk, $"the volume wedged after a failed create: {lookup.Error}");
        Assert.Equal(NodeKind.File, lookup.Value.Kind);

        // и снапшот всё ещё выдаёт содержимое, а не закрытый ObjectReader
        var handle = target.Open("branches/main/README.md", OpenMode.Read).Value;
        var buffer = new byte[64];
        Assert.True(target.Read(handle, 0, buffer).TryGet(out var read));
        Assert.True(read > 0);
        target.Close(handle);
    }

    /// <summary>То же самое для надгробия: удалили файл, пересоздание
    /// сорвалось — том обязан остаться живым.</summary>
    [Fact]
    public void A_failing_recreate_after_delete_does_not_wedge_the_volume()
    {
        using var repo = BuildRepo();
        var (target, store, _) = Open(repo);
        using var _t = target;

        Assert.True(target.Delete("branches/main/README.md").IsOk);
        MakeWritesFail(store, "branches/main/README.md");

        var failed = target.Open("branches/main/README.md", OpenMode.Write);
        Assert.False(failed.IsOk);

        Assert.True(target.List("branches/main").IsOk, "the volume wedged after a failed recreate");
    }

    /// <summary>Аренду нельзя отпустить дважды — не «не следует», а
    /// нельзя. Прежде это было соглашением, и одно нарушение вешало том
    /// навсегда; теперь второй Dispose не доходит до счётчика.</summary>
    [Fact]
    public void A_lease_cannot_be_released_twice()
    {
        using var repo = BuildRepo();
        using var manager = new SnapshotManager(repo.GitDir);

        var lease = manager.Acquire();
        lease.Dispose();
        lease.Dispose();
        lease.Dispose();

        // снапшот жив: менеджер по-прежнему держит свою ссылку
        using var again = manager.Acquire();
        Assert.NotNull(again.Snapshot.Refs);
    }

    /// <summary>А вот освободить БОЛЬШЕ, чем взято, — ошибка программиста,
    /// и она обязана быть видна там, где произошла, а не превращаться в
    /// зависание где-то ещё через час.</summary>
    [Fact]
    public void Releasing_more_than_was_acquired_is_reported()
    {
        using var repo = BuildRepo();
        var snapshot = RepoSnapshot.Load(repo.GitDir);
        snapshot.Dispose();                       // ссылка создателя снята
        Assert.Throws<InvalidOperationException>(() => snapshot.Dispose());
    }

    /// <summary>Успешные операции ничего не ломают: счётчик возвращается
    /// туда же, откуда начал, сколько бы их ни было.</summary>
    [Fact]
    public void Many_successful_operations_leave_the_snapshot_reusable()
    {
        using var repo = BuildRepo();
        var (target, _, _) = Open(repo);
        using var _t = target;

        for (var i = 0; i < 200; i++)
        {
            Assert.True(target.Lookup("branches/main/README.md").IsOk);
            var handle = target.Open("branches/main/README.md", OpenMode.Read).Value;
            target.Close(handle);
            Assert.True(target.List("branches/main").IsOk);
            Assert.False(target.Open("branches/main/missing.txt", OpenMode.Read).IsOk);
        }

        Assert.True(target.Lookup("branches/main/README.md").IsOk);
    }
}
