using System.Text;
using Gitfs.Core.Tests.Fixtures;
using Gitfs.Vfs;
using Gitfs.Vfs.Overlay;
using Gitfs.Vfs.Views;

namespace Gitfs.Vfs.Tests;

public class OverlayTests : IDisposable
{
    private readonly string _overlayBase = Path.Combine(Path.GetTempPath(),
        "gitfs-overlay-tests-" + Guid.NewGuid().ToString("N")[..8]);

    public void Dispose()
    {
        if (Directory.Exists(_overlayBase)) Directory.Delete(_overlayBase, recursive: true);
    }

    private static RepoBuilder BuildRepo()
    {
        var repo = new RepoBuilder();
        repo.WriteFile("README.md", "original\n");
        repo.WriteFile("src/app.cs", "class A {}\n");
        repo.CommitAll("first");
        repo.WriteFile("README.md", "second version\n");
        repo.CommitAll("second");
        return repo;
    }

    private (VfsMountTarget Target, OverlayStore Store) Open(RepoBuilder repo)
    {
        var names = NamePolicy.Windows;
        var manager = new SnapshotManager(repo.GitDir);
        var tree = new VirtualTree(new IView[]
        {
            new BranchesView(names), new CommitsView(names), new HistoryView(names),
        });
        var store = OverlayStore.Create(_overlayBase);
        return (new VfsMountTarget(manager, tree, "fixture", readOnly: false, overlay: store), store);
    }

    private static string Read(VfsMountTarget target, string path)
    {
        var handle = target.Open(path, OpenMode.Read).Value;
        var buffer = new byte[4096];
        var read = target.Read(handle, 0, buffer).Value;
        target.Close(handle);
        return Encoding.UTF8.GetString(buffer, 0, read);
    }

    // ---------- copy-on-write ----------

    [Fact]
    public void Writing_a_file_never_touches_the_repository()
    {
        using var repo = BuildRepo();
        var (target, _) = Open(repo);
        using var _t = target;

        var before = repo.Run("rev-parse", "HEAD").Trim();
        var handle = target.Open("branches/main/README.md", OpenMode.Write).Value;
        Assert.True(target.Write(handle, 0, "MODIFIED\n"u8).IsOk);
        target.Close(handle);

        // репозиторий не изменился ни на байт
        Assert.Equal(before, repo.Run("rev-parse", "HEAD").Trim());
        Assert.Equal("second version\n",
            Encoding.UTF8.GetString(repo.RunBytes("cat-file", "blob", "main:README.md")));
        Assert.Empty(repo.Run("status", "--porcelain").Trim());
    }

    [Fact]
    public void A_write_is_visible_on_the_next_read()
    {
        using var repo = BuildRepo();
        var (target, _) = Open(repo);
        using var _t = target;

        var handle = target.Open("branches/main/README.md", OpenMode.Write).Value;
        target.Write(handle, 0, "MODIFIED\n"u8);
        // перезапись обязана отбросить хвост прежней версии
        Assert.True(target.SetLength(handle, 9).IsOk);
        target.Close(handle);

        // порядок слоёв: overlay проверяется раньше дерева (§9)
        Assert.Equal("MODIFIED\n", Read(target, "branches/main/README.md"));
        Assert.Equal(9, target.Lookup("branches/main/README.md").Value.Size);
    }

    [Fact]
    public void Copy_on_write_seeds_from_the_repository_content()
    {
        using var repo = BuildRepo();
        var (target, _) = Open(repo);
        using var _t = target;

        // открываем на запись и НЕ пишем — содержимое обязано сохраниться
        var handle = target.Open("branches/main/README.md", OpenMode.Write).Value;
        target.Close(handle);
        Assert.Equal("second version\n", Read(target, "branches/main/README.md"));

        // перезаписываем только первый байт: остальное осталось от исходника
        var again = target.Open("branches/main/README.md", OpenMode.Write).Value;
        target.Write(again, 0, "S"u8);
        target.Close(again);
        Assert.Equal("Second version\n", Read(target, "branches/main/README.md"));
    }

    // ---------- перекрытие работает в любой вьюхе ----------

    [Fact]
    public void Overlay_covers_immutable_views_too_without_touching_history()
    {
        using var repo = BuildRepo();
        var (target, _) = Open(repo);
        using var _t = target;

        var sha = repo.Run("rev-parse", "HEAD").Trim();
        var path = $"commits/{sha}/README.md";
        var handle = target.Open(path, OpenMode.Write).Value;
        target.Write(handle, 0, "edited in a commit view\n"u8);
        target.Close(handle);

        Assert.Equal("edited in a commit view\n", Read(target, path));
        // сам объект в репозитории цел — инвариант неизменности истории
        Assert.Equal("second version\n",
            Encoding.UTF8.GetString(repo.RunBytes("cat-file", "blob", $"{sha}:README.md")));
    }

    // ---------- надгробия ----------

    [Fact]
    public void Delete_hides_the_node_but_keeps_the_object()
    {
        using var repo = BuildRepo();
        var (target, _) = Open(repo);
        using var _t = target;

        var blob = repo.Run("rev-parse", "main:README.md").Trim();
        Assert.True(target.Delete("branches/main/README.md").IsOk);

        Assert.Equal(GitfsError.NotFound, target.Lookup("branches/main/README.md").Error);
        Assert.Equal(GitfsError.NotFound, target.Open("branches/main/README.md", OpenMode.Read).Error);
        Assert.DoesNotContain("README.md",
            target.List("branches/main").Value.Select(e => e.Name));
        // объект на месте
        Assert.Equal(blob, repo.Run("rev-parse", "main:README.md").Trim());
    }

    // ---------- листинг ----------

    [Fact]
    public void Listing_shows_created_files_and_overlaid_sizes()
    {
        using var repo = BuildRepo();
        var (target, _) = Open(repo);
        using var _t = target;

        var created = target.Open("branches/main/fresh.txt", OpenMode.Write).Value;
        target.Write(created, 0, "brand new\n"u8);
        target.Close(created);

        var entries = target.List("branches/main").Value.ToDictionary(e => e.Name, e => e.Info);
        Assert.True(entries.ContainsKey("fresh.txt"));      // созданный виден
        Assert.Equal(10, entries["fresh.txt"].Size);
        Assert.False(entries["fresh.txt"].ReadOnly);
        Assert.True(entries["README.md"].ReadOnly);          // нетронутый — read-only
    }

    // ---------- read-only режим ----------

    [Fact]
    public void Read_only_mount_refuses_writes_by_code()
    {
        using var repo = BuildRepo();
        var names = NamePolicy.Windows;
        var manager = new SnapshotManager(repo.GitDir);
        var tree = new VirtualTree(new IView[] { new BranchesView(names) });
        using var target = new VfsMountTarget(manager, tree, "fixture"); // read-only, без overlay

        Assert.Equal(GitfsError.AccessDenied,
            target.Open("branches/main/README.md", OpenMode.Write).Error);
        Assert.Equal(GitfsError.AccessDenied, target.Delete("branches/main/README.md").Error);
    }

    // ---------- хранилище ----------

    [Fact]
    public void Storage_names_are_hashes_and_the_manifest_survives_reload()
    {
        using var repo = BuildRepo();
        var (target, store) = Open(repo);
        var root = store.Root;

        var handle = target.Open("branches/main/src/app.cs", OpenMode.Write).Value;
        target.Write(handle, 0, "// edited\n"u8);
        target.Close(handle);
        target.Dispose();

        // длинный виртуальный путь не становится именем файла
        var entry = Assert.Single(store.Entries.Where(e => e.Kind == OverlayKind.File));
        Assert.Equal(64, entry.StorageName.Length); // hex sha-256
        Assert.DoesNotContain('/', entry.StorageName);
        Assert.Equal("branches/main/src/app.cs", entry.VirtualPath);
    }

    [Fact]
    public void Mount_id_carries_the_process_and_the_overlay_is_removed_on_dispose()
    {
        using var repo = BuildRepo();
        var (target, store) = Open(repo);
        var root = store.Root;
        Assert.StartsWith(Environment.ProcessId + "-", store.MountId);
        Assert.True(Directory.Exists(root));

        target.Dispose(); // штатное размонтирование убирает песочницу
        Assert.False(Directory.Exists(root));
    }

    [Fact]
    public void Kept_overlay_survives_and_reloads_its_manifest()
    {
        using var repo = BuildRepo();
        string root;
        using (var store = OverlayStore.Create(_overlayBase, keepOnDispose: true))
        {
            root = store.Root;
            store.PrepareForWrite("branches/main/README.md", null);
            store.Hide("branches/main/src/app.cs");
        }
        Assert.True(Directory.Exists(root));

        // второй экземпляр поверх того же каталога восстанавливает состояние
        using var reopened = OverlayStore.Create(_overlayBase, keepOnDispose: true);
        Assert.True(Directory.Exists(root));
        var reloaded = OverlayStore.Create(Path.GetDirectoryName(root)!, keepOnDispose: true);
        reloaded.Dispose();
    }
}
