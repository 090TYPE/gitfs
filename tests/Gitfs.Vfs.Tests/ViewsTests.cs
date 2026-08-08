using Gitfs.Core;
using Gitfs.Core.Tests.Fixtures;
using Gitfs.Vfs;
using Gitfs.Vfs.Views;

namespace Gitfs.Vfs.Tests;

public class ViewsTests
{
    /// <summary>История, где data.txt менялся трижды, stable.txt — ни разу
    /// после создания, плюс теги и вторая ветка.</summary>
    private static RepoBuilder BuildRepo()
    {
        var repo = new RepoBuilder();
        repo.WriteFile("stable.txt", "never changes\n");
        repo.WriteFile("data.txt", "v1\n");
        repo.WriteFile("src/app.cs", "class A {}\n");
        repo.CommitAll("first");
        repo.Tag("v1.0", annotated: true, message: "release one");

        repo.WriteFile("data.txt", "v1\nv2\n");
        repo.CommitAll("second");

        repo.WriteFile("noise.txt", "unrelated\n"); // коммит, не трогающий data.txt
        repo.CommitAll("third");

        repo.WriteFile("data.txt", "v1\nv2\nv3\n");
        repo.CommitAll("fourth");
        repo.Tag("v2.0");
        return repo;
    }

    private static (RepoSnapshot Snap, VirtualTree Tree) Open(RepoBuilder repo,
        int commitLimit = 200, int historyLimit = 500)
    {
        var names = NamePolicy.Windows;
        var snapshot = RepoSnapshot.Load(repo.GitDir);
        var tree = new VirtualTree(new IView[]
        {
            new BranchesView(names), new TagsView(names),
            new CommitsView(names, commitLimit), new DatesView(names),
            new HistoryView(names, historyLimit),
        });
        return (snapshot, tree);
    }

    private static string[] Names(IEnumerable<DirEntry>? entries) =>
        entries!.Select(e => e.Name).ToArray();

    // ---------- корень ----------

    [Fact]
    public void Root_lists_all_five_views()
    {
        using var repo = BuildRepo();
        var (snap, tree) = Open(repo);
        using var _ = snap;
        Assert.Equal(new[] { "branches", "tags", "commits", "dates", "history" },
            Names(tree.List(snap, "/")));
    }

    // ---------- tags ----------

    [Fact]
    public void Tags_view_lists_both_kinds_and_peels_annotated_ones()
    {
        using var repo = BuildRepo();
        var (snap, tree) = Open(repo);
        using var _ = snap;

        Assert.Equal(new[] { "v1.0", "v2.0" }, Names(tree.List(snap, "tags")));

        // аннотированный тег: показывается ДЕРЕВО коммита, а не объект тега
        var node = tree.Resolve(snap, "tags/v1.0/data.txt");
        Assert.Equal(repo.Run("rev-parse", "v1.0^{commit}:data.txt").Trim(),
            node!.Value.BlobId.ToString());
        Assert.Equal(3, node.Value.Size); // "v1\n"

        // лёгкий тег указывает на последний коммит
        var latest = tree.Resolve(snap, "tags/v2.0/data.txt");
        Assert.Equal(repo.Run("rev-parse", "v2.0:data.txt").Trim(), latest!.Value.BlobId.ToString());
        Assert.Equal(9, latest.Value.Size); // "v1\nv2\nv3\n"
    }

    // ---------- commits: листинг ≠ резолв (§3.2) ----------

    [Fact]
    public void Commits_listing_is_capped_but_any_sha_still_resolves()
    {
        using var repo = BuildRepo();
        var (snap, tree) = Open(repo, commitLimit: 2);
        using var _ = snap;

        var listed = Names(tree.List(snap, "commits"));
        Assert.Equal(2, listed.Length); // перечисление обрезано лимитом

        // первый коммит в листинг НЕ попал, но путь к нему открывается
        var first = repo.Run("rev-parse", "HEAD~3").Trim();
        Assert.DoesNotContain(first[..7], listed);
        var node = tree.Resolve(snap, $"commits/{first}/data.txt");
        Assert.Equal(repo.Run("rev-parse", "HEAD~3:data.txt").Trim(), node!.Value.BlobId.ToString());
    }

    [Fact]
    public void Commit_prefix_resolves_when_unambiguous_and_fails_when_short()
    {
        using var repo = BuildRepo();
        var (snap, tree) = Open(repo);
        using var _ = snap;

        var head = repo.Run("rev-parse", "HEAD").Trim();
        Assert.NotNull(tree.Resolve(snap, $"commits/{head[..7]}/data.txt"));
        Assert.NotNull(tree.Resolve(snap, $"commits/{head[..12]}/data.txt"));
        Assert.Null(tree.Resolve(snap, $"commits/{head[..4]}/data.txt")); // короче семи
        Assert.Null(tree.Resolve(snap, "commits/zzzzzzz/data.txt"));      // не hex
        Assert.Null(tree.Resolve(snap, "commits/0123456789012345678901234567890123456789"));
    }

    // ---------- dates ----------

    [Fact]
    public void Dates_view_lists_only_days_with_commits()
    {
        using var repo = BuildRepo();
        var (snap, tree) = Open(repo);
        using var _ = snap;

        // фикстура фиксирует дату коммитов — все в одном дне
        var day = repo.Run("log", "-1", "--format=%cd", "--date=format:%Y-%m-%d").Trim();
        Assert.Equal(new[] { day }, Names(tree.List(snap, "dates")));

        var node = tree.Resolve(snap, $"dates/{day}/data.txt");
        Assert.Equal(repo.Run("rev-parse", "HEAD:data.txt").Trim(), node!.Value.BlobId.ToString());
        Assert.Null(tree.Resolve(snap, "dates/1999-01-01"));
        Assert.Null(tree.Resolve(snap, "dates/not-a-date"));
    }

    // ---------- history: ФАЙЛ СТАНОВИТСЯ ПАПКОЙ ----------

    [Fact]
    public void A_file_opens_as_a_folder_of_its_versions()
    {
        using var repo = BuildRepo();
        var (snap, tree) = Open(repo);
        using var _ = snap;

        var node = tree.Resolve(snap, "history/data.txt");
        Assert.Equal(NodeKind.Directory, node!.Value.Kind); // файл — директория!

        var versions = Names(tree.List(snap, "history/data.txt"));
        // три изменения + latest; коммит «third» файл не трогал и версии не создал
        Assert.Equal(4, versions.Length);
        Assert.Equal("latest.txt", versions[^1]);
        Assert.All(versions.Take(3), v => Assert.Matches(@"^\d{4}-[0-9a-f]{7}\.txt$", v));
        // нумерация от свежей: сортировка по имени совпадает с хронологией
        Assert.StartsWith("0001-", versions[0]);
        Assert.StartsWith("0003-", versions[2]);
    }

    [Fact]
    public void Version_files_resolve_to_the_right_blobs()
    {
        using var repo = BuildRepo();
        var (snap, tree) = Open(repo);
        using var _ = snap;

        var versions = tree.List(snap, "history/data.txt")!.ToList();
        var newest = tree.Resolve(snap, "history/data.txt/" + versions[0].Name);
        var oldest = tree.Resolve(snap, "history/data.txt/" + versions[2].Name);
        var latest = tree.Resolve(snap, "history/data.txt/latest.txt");

        Assert.Equal(repo.Run("rev-parse", "HEAD:data.txt").Trim(), newest!.Value.BlobId.ToString());
        Assert.Equal(repo.Run("rev-parse", "HEAD~3:data.txt").Trim(), oldest!.Value.BlobId.ToString());
        Assert.Equal(newest.Value.BlobId, latest!.Value.BlobId); // latest == самая свежая
        Assert.Equal(9, newest.Value.Size);  // "v1\nv2\nv3\n"
        Assert.Equal(3, oldest.Value.Size);  // "v1\n"
    }

    [Fact]
    public void A_file_that_never_changed_has_exactly_one_version()
    {
        using var repo = BuildRepo();
        var (snap, tree) = Open(repo);
        using var _ = snap;

        var versions = Names(tree.List(snap, "history/stable.txt"));
        Assert.Equal(2, versions.Length); // одна ревизия + latest
        Assert.StartsWith("0001-", versions[0]);
        Assert.Equal("latest.txt", versions[1]);
    }

    [Fact]
    public void Directories_stay_directories_and_files_become_folders()
    {
        using var repo = BuildRepo();
        var (snap, tree) = Open(repo);
        using var _ = snap;

        var top = tree.List(snap, "history")!.ToDictionary(e => e.Name, e => e.Info.Kind);
        Assert.Equal(NodeKind.Directory, top["src"]);       // директория осталась собой
        Assert.Equal(NodeKind.Directory, top["data.txt"]);  // файл стал папкой

        var nested = Names(tree.List(snap, "history/src/app.cs"));
        Assert.Contains("latest.cs", nested); // расширение сохранено
    }

    [Fact]
    public void Truncated_marker_appears_only_when_the_list_is_capped()
    {
        using var repo = BuildRepo();

        var (full, fullTree) = Open(repo);
        using (full) Assert.DoesNotContain(HistoryView.TruncatedMarker,
            Names(fullTree.List(full, "history/data.txt")));

        var (capped, cappedTree) = Open(repo, historyLimit: 2);
        using (capped)
        {
            var names = Names(cappedTree.List(capped, "history/data.txt"));
            // молчаливое усечение недопустимо — маркер обязан быть виден
            Assert.Contains(HistoryView.TruncatedMarker, names);
            Assert.NotNull(cappedTree.Resolve(capped, "history/data.txt/.truncated"));
        }
    }

    [Fact]
    public void History_rejects_paths_that_are_not_files_in_the_reference_history()
    {
        using var repo = BuildRepo();
        var (snap, tree) = Open(repo);
        using var _ = snap;

        Assert.Null(tree.Resolve(snap, "history/nosuch.txt"));
        Assert.Null(tree.Resolve(snap, "history/data.txt/9999-abcdef1.txt")); // нет такой ревизии
        Assert.Null(tree.Resolve(snap, "history/data.txt/garbage.txt"));
    }

    // ---------- кэши снапшота (перф-долг M2 п.4) ----------

    [Fact]
    public void Repeated_listings_hit_the_snapshot_caches()
    {
        using var repo = BuildRepo();
        var (snap, tree) = Open(repo);
        using var _ = snap;

        tree.List(snap, "branches/main")!.ToList();
        var hitsBefore = snap.TreeCache.Hits + snap.ListingCache.Hits;
        for (var i = 0; i < 5; i++) tree.List(snap, "branches/main")!.ToList();
        Assert.True(snap.TreeCache.Hits + snap.ListingCache.Hits > hitsBefore);

        // вершина ветки парсится один раз на снапшот
        Assert.True(snap.TipCache.ContainsKey("refs/heads/main"));
    }
}
