using Gitfs.Core;
using Gitfs.Core.Tests.Fixtures;
using Gitfs.Vfs.Overlay;
using Gitfs.Vfs.Views;

namespace Gitfs.Vfs.Tests;

/// <summary>Находки адверсариального ревью M6. Каждый тест здесь описывает
/// дефект, который существовал и был воспроизведён, — а не свойство, которое
/// хотелось бы иметь.</summary>
public class ReviewFindingsTests : IDisposable
{
    private readonly string _overlayBase = Path.Combine(Path.GetTempPath(),
        "gitfs-review-" + Guid.NewGuid().ToString("N")[..8]);

    public void Dispose()
    {
        if (Directory.Exists(_overlayBase)) Directory.Delete(_overlayBase, recursive: true);
    }

    private static RepoBuilder BuildRepo()
    {
        var repo = new RepoBuilder();
        repo.WriteFile("README.md", "repository content that is long enough to notice\n");
        repo.WriteFile("src/app.cs", "class A {}\n");
        repo.CommitAll("first");
        return repo;
    }

    private (VfsMountTarget Target, OverlayStore Store) Open(RepoBuilder repo, NamePolicy? policy = null)
    {
        var names = policy ?? NamePolicy.Windows;
        var manager = new SnapshotManager(repo.GitDir);
        var tree = new VirtualTree(new IView[] { new BranchesView(names), new CommitsView(names) });
        var store = OverlayStore.Create(_overlayBase, names: names);
        return (new VfsMountTarget(manager, tree, "fixture", readOnly: false, overlay: store), store);
    }

    // ---------- F7: удаление директории ----------

    [Fact]
    public void Deleting_a_directory_is_refused_rather_than_half_done()
    {
        // Надгробие — точный ключ: оно прячет ровно один путь. Раньше
        // «удаление» директории проходило, каталог исчезал из листинга, а
        // файлы под ним продолжали читаться — и вернуть их могло только
        // перемонтирование.
        using var repo = BuildRepo();
        var (target, _) = Open(repo);
        using var _t = target;

        var result = target.Delete("branches/main/src");
        Assert.Equal(GitfsError.IsADirectory, result.Error);

        // директория на месте, и её содержимое видно
        Assert.Equal(NodeKind.Directory, target.Lookup("branches/main/src").Value.Kind);
        Assert.Contains("app.cs", target.List("branches/main/src").Value.Select(e => e.Name));
        Assert.Contains("src", target.List("branches/main").Value.Select(e => e.Name));
    }

    [Fact]
    public void Deleting_a_view_root_is_refused_too()
    {
        using var repo = BuildRepo();
        var (target, _) = Open(repo);
        using var _t = target;

        Assert.Equal(GitfsError.IsADirectory, target.Delete("branches").Error);
        Assert.Equal(GitfsError.IsADirectory, target.Delete("branches/main").Error);
        Assert.Contains("branches", target.List("").Value.Select(e => e.Name));
    }

    // ---------- F5: воскрешение удалённого содержимого ----------

    [Fact]
    public void A_storage_file_orphaned_by_a_failed_write_never_outlives_its_tombstone()
    {
        // Имя файла песочницы — хеш пути, поэтому мусор от сорвавшейся
        // записи лежит ровно там, куда придёт следующее создание того же
        // пути. Прежде Hide удалял его только при наличии записи в
        // манифесте, а PrepareForWrite усыновлял «если файл уже есть» — и
        // «новый пустой файл» печатал начало только что удалённого блоба.
        using var repo = BuildRepo();
        var (target, store) = Open(repo);
        using var _t = target;

        var storage = StorageNameFor("branches/main/README.md");
        File.WriteAllText(Path.Combine(store.Root, storage), "LEFTOVER FROM A FAILED WRITE");

        Assert.True(target.Delete("branches/main/README.md").IsOk);

        var handle = target.Open("branches/main/README.md", OpenMode.Write).Value;
        target.Close(handle);

        Assert.Equal(0, target.Lookup("branches/main/README.md").Value.Size);
        var reader = target.Open("branches/main/README.md", OpenMode.Read).Value;
        var buffer = new byte[64];
        var read = target.Read(reader, 0, buffer).Value;
        target.Close(reader);
        Assert.Equal(0, read);
    }

    private static string StorageNameFor(string virtualPath) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(virtualPath.Replace('\\', '/').Trim('/'))))
            .ToLowerInvariant();

    // ---------- F8: регистр ----------

    [Fact]
    public void Under_the_posix_policy_the_sandbox_keeps_case_apart()
    {
        // git различает Makefile и makefile, Posix-политика — тоже. Пока
        // песочница жёстко сравнивала без учёта регистра, запись в один из
        // них подменяла другой, а удаление одного прятало оба.
        // Через индекс, а не через файлы: на Windows «Makefile» и «makefile»
        // это один и тот же файл, и обычной записью такую пару не собрать —
        // а в git она существует и встречается в живых репозиториях.
        using var repo = new RepoBuilder();
        repo.WriteFile("seed.txt", "seed\n");
        repo.CommitAll("seed");
        var upper = repo.RunWithInput("UPPER\n"u8.ToArray(),
            "hash-object", "-w", "--stdin").Trim();
        var lower = repo.RunWithInput("lower\n"u8.ToArray(),
            "hash-object", "-w", "--stdin").Trim();
        repo.Run("update-index", "--add", "--cacheinfo", $"100644,{upper},Makefile");
        repo.Run("update-index", "--add", "--cacheinfo", $"100644,{lower},makefile");
        repo.Run("commit", "-q", "-m", "both cases");

        var (target, _) = Open(repo, NamePolicy.Posix);
        using var _t = target;

        var handle = target.Open("branches/main/makefile", OpenMode.Write).Value;
        target.Write(handle, 0, "written to the lowercase one"u8.ToArray());
        target.Close(handle);

        // верхний регистр не тронут
        var reading = target.Open("branches/main/Makefile", OpenMode.Read).Value;
        var buffer = new byte[64];
        var read = target.Read(reading, 0, buffer).Value;
        target.Close(reading);
        Assert.Equal("UPPER\n", System.Text.Encoding.UTF8.GetString(buffer[..read]));

        // и удаление одного не прячет другой
        Assert.True(target.Delete("branches/main/makefile").IsOk);
        Assert.True(target.Lookup("branches/main/Makefile").IsOk);
        Assert.Contains("Makefile", target.List("branches/main").Value.Select(e => e.Name));
        Assert.DoesNotContain("makefile", target.List("branches/main").Value.Select(e => e.Name));
    }

    // ---------- F6: бит исполнения ----------

    [Fact]
    public void The_executable_bit_from_git_reaches_the_boundary()
    {
        using var repo = new RepoBuilder();
        repo.WriteFile("script.sh", "#!/bin/sh\necho hi\n");
        repo.WriteFile("plain.txt", "not executable\n");
        // Порядок важен и различается по платформам: на Linux `git add -A`
        // берёт режим из рабочей копии и отменил бы --chmod=+x, поставленный
        // раньше (на Windows core.fileMode=false, и там это незаметно).
        // Поэтому сначала индексируем, потом выставляем бит, потом коммитим.
        repo.Run("add", "-A");
        repo.Run("update-index", "--chmod=+x", "script.sh");
        repo.Run("commit", "-q", "-m", "with a script");
        Assert.Contains("100755", repo.Run("ls-tree", "HEAD", "script.sh"));

        var (target, _) = Open(repo);
        using var _t = target;

        Assert.True(target.Lookup("branches/main/script.sh").Value.Executable,
            "100755 lost on the way to the boundary — no script from the repo would run");
        Assert.False(target.Lookup("branches/main/plain.txt").Value.Executable);

        var listing = target.List("branches/main").Value.ToDictionary(e => e.Name, e => e.Info);
        Assert.True(listing["script.sh"].Executable);
        Assert.False(listing["plain.txt"].Executable);
    }

    // ---------- F4: битый ускоритель не должен мешать монтированию ----------

    [Theory]
    [InlineData(20)]   // заголовок таблицы чанков
    [InlineData(24)]
    [InlineData(28)]   // поле смещения
    [InlineData(32)]
    [InlineData(40)]
    public void A_corrupt_commit_graph_is_ignored_and_never_fails_a_mount(int offsetToDamage)
    {
        using var repo = BuildRepo();
        repo.Run("commit-graph", "write", "--reachable");
        var path = Path.Combine(repo.GitDir, "objects", "info", "commit-graph");
        var bytes = File.ReadAllBytes(path);
        if (offsetToDamage >= bytes.Length) return;

        File.SetAttributes(path, FileAttributes.Normal);
        bytes[offsetToDamage] ^= 0xff;
        File.WriteAllBytes(path, bytes);

        // Ускоритель необязателен по определению: испорченный файл обязан
        // быть просто проигнорирован. Раньше один перевёрнутый бит в поле
        // смещения давал ArgumentOutOfRangeException из метода, который
        // документирован как «нет графа — не ошибка», и репозиторий
        // переставал монтироваться вовсе.
        var manager = new SnapshotManager(repo.GitDir);
        using var target = new VfsMountTarget(manager,
            new VirtualTree(new IView[] { new BranchesView(NamePolicy.Windows) }), "fixture");

        Assert.True(target.Lookup("branches/main/README.md").IsOk,
            $"a byte flipped at {offsetToDamage} made the repository unmountable");
    }

    [Fact]
    public void A_truncated_commit_graph_is_ignored_too()
    {
        using var repo = BuildRepo();
        repo.Run("commit-graph", "write", "--reachable");
        var path = Path.Combine(repo.GitDir, "objects", "info", "commit-graph");
        var bytes = File.ReadAllBytes(path);
        File.SetAttributes(path, FileAttributes.Normal);

        for (var keep = 8; keep < Math.Min(bytes.Length, 200); keep += 17)
        {
            File.WriteAllBytes(path, bytes.AsSpan(0, keep).ToArray());
            Assert.Null(Gitfs.Core.Accel.CommitGraph.TryLoad(repo.GitDir));
        }
    }
}
