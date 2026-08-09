using System.Text;
using Gitfs.Core.Tests.Fixtures;
using Gitfs.Vfs;
using Gitfs.Vfs.Views;

namespace Gitfs.Vfs.Tests;

/// <summary>Сабмодуль. Спека §3: gitlink — «пустая директория с файлом-маркером
/// .gitfs-submodule, внутри — OID и путь».
///
/// Без маркера сабмодуль выглядел папкой, которая не открывается: щёлкнув по
/// ней, человек получал пустоту и никакого объяснения — ни что это, ни куда
/// идти дальше.</summary>
public class SubmoduleMarkerTests
{
    /// <summary>Репозиторий с настоящим gitlink. Клонировать сабмодуль не
    /// нужно и негде: запись 160000 пишется прямо в дерево через mktree —
    /// ровно то, что видит gitfs, когда читает чужой репозиторий.</summary>
    private static (RepoBuilder Repo, string Sha) WithSubmodule()
    {
        var repo = new RepoBuilder();
        repo.WriteFile("readme.md", "top\n");
        repo.CommitAll("first");

        var sha = repo.Run("rev-parse", "HEAD").Trim();
        // mktree строит ОДНО плоское дерево и слэш в имени отвергает —
        // вложенность собирается снизу вверх, как её и хранит git.
        var inner = repo.RunWithInput(
            Encoding.UTF8.GetBytes($"160000 commit {sha}\tlib\n"), "mktree").Trim();
        var tree = repo.RunWithInput(Encoding.UTF8.GetBytes(
            $"100644 blob {repo.Run("rev-parse", "HEAD:readme.md").Trim()}\treadme.md\n" +
            $"040000 tree {inner}\tvendor\n"), "mktree").Trim();
        var commit = repo.Run("commit-tree", tree, "-m", "with submodule").Trim();
        repo.Run("update-ref", "refs/heads/main", commit);
        return (repo, sha);
    }

    private static VirtualTree Tree()
    {
        var names = NamePolicy.For(NamePolicyKind.Native);
        return new VirtualTree(new IView[]
        {
            new BranchesView(names),
            new CommitsView(names),
            new HistoryView(names),
        });
    }

    [Fact]
    public void A_submodule_is_a_folder_holding_exactly_one_marker()
    {
        var (repo, _) = WithSubmodule();
        using var _repo = repo;
        using var manager = new SnapshotManager(repo.GitDir);

        var listed = Tree().List(manager.Current, "branches/main/vendor/lib")!
            .Select(e => e.Name).ToList();
        Assert.Equal(new[] { ViewBase.SubmoduleMarker }, listed);
    }

    [Fact]
    public void The_marker_names_the_commit_and_the_path()
    {
        var (repo, sha) = WithSubmodule();
        using var _repo = repo;
        using var manager = new SnapshotManager(repo.GitDir);
        var tree = Tree();

        var path = "branches/main/vendor/lib/" + ViewBase.SubmoduleMarker;
        var bytes = tree.ReadSynthetic(manager.Current, path);
        Assert.NotNull(bytes);

        var text = Encoding.UTF8.GetString(bytes!);
        Assert.Contains(sha, text);              // какой именно коммит
        Assert.Contains("vendor/lib", text);     // и где он в дереве
        Assert.Contains("submodule", text);      // и что это вообще такое
    }

    /// <summary>Размер в stat обязан совпасть с числом отданных байт: файл,
    /// объявленный нулевым, читается как пустой независимо от содержимого.</summary>
    [Fact]
    public void The_declared_size_matches_the_bytes()
    {
        var (repo, _) = WithSubmodule();
        using var _repo = repo;
        using var manager = new SnapshotManager(repo.GitDir);
        var tree = Tree();

        var path = "branches/main/vendor/lib/" + ViewBase.SubmoduleMarker;
        var node = tree.Resolve(manager.Current, path);
        Assert.NotNull(node);
        Assert.Equal(NodeKind.File, node!.Value.Kind);
        Assert.Equal(tree.ReadSynthetic(manager.Current, path)!.LongLength, node.Value.Size);
    }

    /// <summary>Маркер живёт во ВСЕХ вьюхах, где встречается gitlink, а не
    /// только в branches/: он реализован в базе вьюх именно поэтому.</summary>
    [Fact]
    public void The_marker_shows_up_through_the_commits_view_too()
    {
        var (repo, sha) = WithSubmodule();
        using var _repo = repo;
        using var manager = new SnapshotManager(repo.GitDir);
        var tree = Tree();

        var head = repo.Run("rev-parse", "HEAD").Trim();
        var listed = tree.List(manager.Current, $"commits/{head}/vendor/lib")!
            .Select(e => e.Name).ToList();
        Assert.Equal(new[] { ViewBase.SubmoduleMarker }, listed);

        var bytes = tree.ReadSynthetic(manager.Current,
            $"commits/{head}/vendor/lib/" + ViewBase.SubmoduleMarker);
        Assert.NotNull(bytes);
        Assert.Contains(sha, Encoding.UTF8.GetString(bytes!));
    }

    [Fact]
    public void An_ordinary_folder_gets_no_marker()
    {
        // иначе маркер появился бы всюду и перестал что-либо значить
        using var repo = new RepoBuilder();
        repo.WriteFile("dir/f.txt", "x\n");
        repo.CommitAll("first");
        using var manager = new SnapshotManager(repo.GitDir);

        var listed = Tree().List(manager.Current, "branches/main/dir")!
            .Select(e => e.Name).ToList();
        Assert.DoesNotContain(ViewBase.SubmoduleMarker, listed);
        Assert.Contains("f.txt", listed);
    }

    [Fact]
    public void The_marker_reads_through_the_mount_target_like_any_file()
    {
        var (repo, sha) = WithSubmodule();
        using var _repo = repo;
        using var manager = new SnapshotManager(repo.GitDir);
        using var target = new VfsMountTarget(manager, Tree(), "repo");

        var path = "branches/main/vendor/lib/" + ViewBase.SubmoduleMarker;
        var opened = target.Open(path, OpenMode.Read);
        Assert.True(opened.IsOk, opened.Error.ToString());
        try
        {
            var buffer = new byte[1024];
            var read = target.Read(opened.Value, 0, buffer);
            Assert.True(read.IsOk);
            Assert.Contains(sha, Encoding.UTF8.GetString(buffer, 0, read.Value));
        }
        finally { target.Close(opened.Value); }
    }
}
