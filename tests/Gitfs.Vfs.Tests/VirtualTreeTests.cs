using Gitfs.Core;
using Gitfs.Core.Tests.Fixtures;
using Gitfs.Vfs;
using Gitfs.Vfs.Views;

namespace Gitfs.Vfs.Tests;

public class VirtualTreeTests
{
    /// <summary>Ветки со слэшем, юникод, симлинк, коллизия регистра
    /// (README+readme в одном дереве через индекс — в рабочей копии Windows
    /// их не создать, а в git-дереве можно).</summary>
    private static RepoBuilder BuildRepo()
    {
        var repo = new RepoBuilder();
        repo.WriteFile("README.md", "hello\n");
        repo.WriteFile("src/Program.cs", "class P {}\n");
        repo.WriteFile("src/утилиты.cs", "// юникод\n");
        var lower = repo.RunWithInput("lower\n"u8.ToArray(), "hash-object", "-w", "--stdin").Trim();
        var upper = repo.RunWithInput("UPPER\n"u8.ToArray(), "hash-object", "-w", "--stdin").Trim();
        repo.Run("add", "-A");
        repo.Run("update-index", "--add", "--cacheinfo", $"100644,{upper},case/README");
        repo.Run("update-index", "--add", "--cacheinfo", $"100644,{lower},case/readme");
        repo.Run("commit", "-m", "base");
        repo.Branch("feature/login");
        repo.WriteFile("src/Program.cs", "class P { int X; }\n");
        // не add -A: он синхронизировал бы индекс с рабочей копией и удалил
        // case/README+case/readme, существующие только в индексе
        repo.Run("add", "src/Program.cs");
        repo.Run("commit", "-m", "main tip");
        return repo;
    }

    private static (RepoSnapshot Snap, VirtualTree Tree) Open(RepoBuilder repo)
    {
        var snap = RepoSnapshot.Load(repo.GitDir);
        var tree = new VirtualTree(new IView[] { new BranchesView(NamePolicy.Windows) });
        return (snap, tree);
    }

    // ---------- снапшот и эпоха ----------

    [Fact]
    public void Snapshot_refresh_detects_new_commit_and_keeps_instance_otherwise()
    {
        using var repo = BuildRepo();
        var manager = new SnapshotManager(repo.GitDir);
        var first = manager.Current;
        Assert.Same(first, manager.Refresh(force: true)); // без изменений — тот же

        repo.WriteFile("new.txt", "x\n");
        var sha = repo.CommitAll("next");
        var refreshed = manager.Refresh(force: true);
        Assert.NotSame(first, refreshed);
        Assert.Equal(sha, refreshed.Refs.HeadTarget?.ToString());
    }

    // ---------- корень и роутинг ----------

    [Fact]
    public void Root_lists_views_and_unknown_view_is_null()
    {
        using var repo = BuildRepo();
        var (snap, tree) = Open(repo);
        using var _ = snap;

        Assert.Equal(new[] { "branches" }, tree.List(snap, "/")!.Select(e => e.Name).ToArray());
        Assert.Null(tree.Resolve(snap, "/nosuch"));
        Assert.Null(tree.Resolve(snap, "branches/../etc"));    // «..» отвергается разбором
    }

    // ---------- вьюха branches ----------

    [Fact]
    public void Branch_names_with_slash_become_nested_directories()
    {
        using var repo = BuildRepo();
        var (snap, tree) = Open(repo);
        using var _ = snap;

        Assert.Equal(new[] { "feature", "main" },
            tree.List(snap, "branches")!.Select(e => e.Name).ToArray());
        Assert.Equal(new[] { "login" },
            tree.List(snap, "branches/feature")!.Select(e => e.Name).ToArray());
        Assert.Equal(NodeKind.Directory, tree.Resolve(snap, "branches/feature")!.Value.Kind);
        Assert.Equal(NodeKind.Directory, tree.Resolve(snap, "branches/feature/login")!.Value.Kind);
    }

    [Fact]
    public void File_resolution_matches_rev_parse_and_cat_file()
    {
        using var repo = BuildRepo();
        var (snap, tree) = Open(repo);
        using var _ = snap;

        var node = tree.Resolve(snap, "branches/main/src/Program.cs");
        Assert.Equal(NodeKind.File, node!.Value.Kind);
        Assert.Equal(repo.Run("rev-parse", "main:src/Program.cs").Trim(),
            node.Value.BlobId.ToString());
        Assert.Equal(long.Parse(repo.Run("cat-file", "-s", "main:src/Program.cs").Trim()),
            node.Value.Size);
        // дата узла — дата вершины ветки (§3.4)
        var tipDate = long.Parse(repo.Run("log", "-1", "--format=%ct", "main").Trim());
        Assert.Equal(tipDate, node.Value.Timestamp.ToUnixTimeSeconds());
    }

    [Fact]
    public void Old_branch_sees_old_content()
    {
        using var repo = BuildRepo();
        var (snap, tree) = Open(repo);
        using var _ = snap;

        // feature/login отстала от main на один коммит
        var old = tree.Resolve(snap, "branches/feature/login/src/Program.cs");
        Assert.Equal(repo.Run("rev-parse", "feature/login:src/Program.cs").Trim(),
            old!.Value.BlobId.ToString());
        Assert.NotEqual(
            tree.Resolve(snap, "branches/main/src/Program.cs")!.Value.BlobId,
            old.Value.BlobId);
    }

    [Fact]
    public void Listing_matches_ls_tree_and_encodes_names()
    {
        using var repo = BuildRepo();
        var (snap, tree) = Open(repo);
        using var _ = snap;

        var names = tree.List(snap, "branches/main/src")!.Select(e => e.Name).ToArray();
        Assert.Contains("Program.cs", names);
        Assert.Contains("утилиты.cs", names);

        var caseDir = tree.List(snap, "branches/main/case")!.Select(e => e.Name).ToArray();
        Assert.Equal(new[] { "README", "readme~2" }, caseDir); // порядок git-дерева
    }

    [Fact]
    public void Case_collision_suffix_resolves_to_the_right_blob()
    {
        using var repo = BuildRepo();
        var (snap, tree) = Open(repo);
        using var _ = snap;

        var upper = tree.Resolve(snap, "branches/main/case/README");
        var lower = tree.Resolve(snap, "branches/main/case/readme~2");
        Assert.Equal(repo.Run("rev-parse", "main:case/README").Trim(), upper!.Value.BlobId.ToString());
        Assert.Equal(repo.Run("rev-parse", "main:case/readme").Trim(), lower!.Value.BlobId.ToString());
        Assert.NotEqual(upper.Value.BlobId, lower.Value.BlobId);
        // сырое имя второго файла НЕ резолвится — только отображаемое
        Assert.Null(tree.Resolve(snap, "branches/main/case/readme"));
    }

    [Fact]
    public void Missing_paths_return_null()
    {
        using var repo = BuildRepo();
        var (snap, tree) = Open(repo);
        using var _ = snap;

        Assert.Null(tree.Resolve(snap, "branches/nosuch"));
        Assert.Null(tree.Resolve(snap, "branches/main/no/such/file"));
        Assert.Null(tree.Resolve(snap, "branches/main/README.md/x")); // сквозь файл
    }
}
