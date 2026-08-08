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
        // git на Windows сам блокирует aux.c в индексе (core.protectNTFS) —
        // ровно тот класс реп «созданных на Linux», ради которого существует %RES
        repo.Run("config", "core.protectNTFS", "false");
        repo.WriteFile("README.md", "hello\n");
        repo.WriteFile("src/Program.cs", "class P {}\n");
        repo.WriteFile("src/утилиты.cs", "// юникод\n");
        var lower = repo.RunWithInput("lower\n"u8.ToArray(), "hash-object", "-w", "--stdin").Trim();
        var upper = repo.RunWithInput("UPPER\n"u8.ToArray(), "hash-object", "-w", "--stdin").Trim();
        var auxBlob = repo.RunWithInput("aux!\n"u8.ToArray(), "hash-object", "-w", "--stdin").Trim();
        var linkTarget = repo.RunWithInput("README.md"u8.ToArray(), "hash-object", "-w", "--stdin").Trim();
        repo.Run("add", "-A");
        repo.Run("update-index", "--add", "--cacheinfo", $"100644,{upper},case/README");
        repo.Run("update-index", "--add", "--cacheinfo", $"100644,{lower},case/readme");
        repo.Run("update-index", "--add", "--cacheinfo", $"100644,{auxBlob},aux.c");
        repo.Run("update-index", "--add", "--cacheinfo", $"120000,{linkTarget},link");
        repo.Run("commit", "-m", "base");
        var baseSha = repo.Run("rev-parse", "HEAD").Trim();
        repo.Run("update-index", "--add", "--cacheinfo", $"160000,{baseSha},sub");
        repo.Run("commit", "-m", "base+gitlink");
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
        // Windows-политика регистронезависима, как и сам том: сырое «readme»
        // попадает в первую запись (README) — ровно как на NTFS. Различает
        // два файла именно суффикс ~2, поэтому оба остаются достижимы.
        Assert.Equal(upper.Value.BlobId, tree.Resolve(snap, "branches/main/case/readme")!.Value.BlobId);
        // на регистрозависимой политике таких коллизий не возникает вовсе
        var posix = new VirtualTree(new IView[] { new BranchesView(NamePolicy.Posix) });
        Assert.Equal(repo.Run("rev-parse", "main:case/readme").Trim(),
            posix.Resolve(snap, "branches/main/case/readme")!.Value.BlobId.ToString());
    }

    [Fact]
    public void Symlink_and_gitlink_have_their_kinds_and_gitlink_blocks_descent()
    {
        using var repo = BuildRepo();
        var (snap, tree) = Open(repo);
        using var _ = snap;

        var link = tree.Resolve(snap, "branches/main/link");
        Assert.Equal(NodeKind.Symlink, link!.Value.Kind);
        Assert.Equal(9, link.Value.Size); // len("README.md")

        var sub = tree.Resolve(snap, "branches/main/sub");
        Assert.Equal(NodeKind.Submodule, sub!.Value.Kind);
        Assert.Equal(0, sub.Value.Size);
        Assert.Null(tree.Resolve(snap, "branches/main/sub/anything")); // сквозь гитлинк нельзя
    }

    [Fact]
    public void Reserved_name_roundtrips_through_display_name()
    {
        using var repo = BuildRepo();
        var (snap, tree) = Open(repo);
        using var _ = snap;

        Assert.Contains("aux%RES.c",
            tree.List(snap, "branches/main")!.Select(e => e.Name).ToArray());
        var node = tree.Resolve(snap, "branches/main/aux%RES.c");
        Assert.Equal(repo.Run("rev-parse", "main:aux.c").Trim(), node!.Value.BlobId.ToString());
        Assert.Null(tree.Resolve(snap, "branches/main/aux.c")); // сырое имя не резолвится
    }

    [Fact]
    public void Broken_ref_yields_null_not_exception_and_does_not_break_listing()
    {
        using var repo = BuildRepo();
        File.WriteAllText(Path.Combine(repo.GitDir, "refs", "heads", "broken"),
            new string('a', 40) + "\n"); // висячий ref на несуществующий объект
        var (snap, tree) = Open(repo);
        using var _ = snap;

        Assert.Null(tree.Resolve(snap, "branches/broken"));
        Assert.Empty(tree.List(snap, "branches/broken")!);

        var listed = tree.List(snap, "branches")!.Select(e => e.Name).ToArray();
        // один битый ref не ломает перечисление всей вьюхи…
        Assert.Contains("main", listed);
        // …но и сам в листинге не показывается: List и Resolve обязаны
        // отвечать одинаково про одно имя (ревью M4)
        Assert.DoesNotContain("broken", listed);
    }

    [Fact]
    public void Deleting_a_branch_with_older_mtime_is_detected()
    {
        using var repo = BuildRepo();
        repo.Run("branch", "victim");
        repo.Run("branch", "newer");
        // victim заведомо не носитель max(mtime) — max-подпись была бы слепа
        File.SetLastWriteTimeUtc(Path.Combine(repo.GitDir, "refs", "heads", "victim"),
            DateTime.UtcNow.AddMinutes(-5));
        var manager = new SnapshotManager(repo.GitDir);
        Assert.True(manager.Current.Refs.All.ContainsKey("refs/heads/victim"));

        repo.Run("branch", "-D", "victim");
        Assert.False(manager.Refresh(force: true).Refs.All.ContainsKey("refs/heads/victim"));
    }

    [Fact]
    public void Throttle_swallows_changes_until_window_passes()
    {
        using var repo = BuildRepo();
        var manager = new SnapshotManager(repo.GitDir, TimeSpan.FromMinutes(10));
        var first = manager.Current;
        repo.WriteFile("t.txt", "x\n");
        repo.Run("add", "t.txt");
        repo.Run("commit", "-m", "change");
        Assert.Same(first, manager.Refresh());              // окно не прошло
        Assert.NotSame(first, manager.Refresh(force: true)); // force обходит троттлинг
    }

    [Fact]
    public void Root_listing_date_agrees_with_view_resolve_date()
    {
        using var repo = BuildRepo();
        var (snap, tree) = Open(repo);
        using var _ = snap;

        var entry = tree.List(snap, "/")!.Single(e => e.Name == "branches");
        var resolved = tree.Resolve(snap, "/branches");
        Assert.Equal(resolved!.Value.Timestamp, entry.Info.Timestamp);
        var headDate = long.Parse(repo.Run("log", "-1", "--format=%ct", "HEAD").Trim());
        Assert.Equal(headDate, entry.Info.Timestamp.ToUnixTimeSeconds());
    }

    [Fact]
    public void List_rejects_dot_paths_and_unknown_views()
    {
        using var repo = BuildRepo();
        var (snap, tree) = Open(repo);
        using var _ = snap;

        Assert.Null(tree.List(snap, "branches/../etc"));
        Assert.Null(tree.List(snap, "branches/main/./src"));
        Assert.Null(tree.List(snap, "/nosuch"));
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
