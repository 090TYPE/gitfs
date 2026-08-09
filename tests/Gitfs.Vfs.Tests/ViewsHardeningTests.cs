using Gitfs.Core;
using Gitfs.Core.Tests.Fixtures;
using Gitfs.Vfs;
using Gitfs.Vfs.Views;

namespace Gitfs.Vfs.Tests;

/// <summary>Регрессии на находки адверсариального ревью M4.</summary>
public class ViewsHardeningTests
{
    private static VirtualTree Tree(int historyLimit = 500, int scanLimit = 20000)
    {
        var names = NamePolicy.Windows;
        return new VirtualTree(new IView[]
        {
            new BranchesView(names), new TagsView(names), new CommitsView(names),
            new DatesView(names), new HistoryView(names, historyLimit, scanLimit),
        });
    }

    private static string[] Names(IEnumerable<DirEntry>? e) => e!.Select(x => x.Name).ToArray();

    // ---------- critical: shallow-клон ----------

    [Fact]
    public void Shallow_clone_serves_available_history_instead_of_failing()
    {
        using var origin = new RepoBuilder();
        for (var i = 0; i < 5; i++)
        {
            origin.WriteFile("data.txt", string.Concat(Enumerable.Repeat("x\n", i + 1)));
            origin.CommitAll($"commit {i}");
        }
        // клонируем на глубину 2: у граничного коммита родителя нет в объектах
        var clonePath = Path.Combine(Path.GetTempPath(), "gitfs-shallow-" + Guid.NewGuid().ToString("N")[..8]);
        origin.Run("clone", "--depth", "2", "--no-local",
            new Uri(origin.Root).AbsoluteUri, clonePath);
        try
        {
            using var snap = RepoSnapshot.Load(Path.Combine(clonePath, ".git"));
            var tree = Tree();

            // до фикса RevWalker здесь вылетал FileNotFoundException
            var versions = Names(tree.List(snap, "history/data.txt"));
            Assert.NotEmpty(versions);
            Assert.Contains(HistoryView.TruncatedMarker, versions); // обрыв виден
            Assert.NotEmpty(Names(tree.List(snap, "commits")));
            Assert.NotEmpty(Names(tree.List(snap, "dates")));
        }
        finally
        {
            foreach (var f in Directory.EnumerateFiles(clonePath, "*", SearchOption.AllDirectories))
                File.SetAttributes(f, FileAttributes.Normal);
            Directory.Delete(clonePath, recursive: true);
        }
    }

    // ---------- critical: объект в loose И в паке ----------

    [Fact]
    public void Prefix_lookup_is_not_confused_by_an_object_in_both_loose_and_pack()
    {
        using var repo = new RepoBuilder();
        repo.WriteFile("a.txt", "a\n");
        repo.CommitAll("first");
        var head = repo.Run("rev-parse", "HEAD").Trim();
        // repack БЕЗ -d: объекты остаются и в паке, и в loose
        repo.Run("repack", "-a");

        using var snap = RepoSnapshot.Load(repo.GitDir);
        var tree = Tree();
        // до фикса один и тот же объект считался двумя кандидатами
        Assert.NotNull(tree.Resolve(snap, $"commits/{head[..7]}/a.txt"));
        Assert.Equal(ObjectId.Parse(head), snap.Objects.FindByPrefix(head[..8]));
    }

    // ---------- critical: симлинк и гитлинк — не файлы истории ----------

    [Fact]
    public void Symlinks_and_gitlinks_do_not_pretend_to_have_version_folders()
    {
        using var repo = new RepoBuilder();
        repo.WriteFile("a.txt", "a\n");
        repo.CommitAll("first");
        var target = repo.RunWithInput("a.txt"u8.ToArray(), "hash-object", "-w", "--stdin").Trim();
        var commit = repo.Run("rev-parse", "HEAD").Trim();
        repo.Run("update-index", "--add", "--cacheinfo", $"120000,{target},link");
        repo.Run("update-index", "--add", "--cacheinfo", $"160000,{commit},sub");
        repo.Run("commit", "-m", "special modes");

        using var snap = RepoSnapshot.Load(repo.GitDir);
        var tree = Tree();

        // Папкой версий раскрывается только обычный файл: цепочка версий
        // символической ссылки или подмодуля бессмысленна (ревью M4).
        Assert.Equal(NodeKind.Directory, tree.Resolve(snap, "history/a.txt")!.Value.Kind);
        Assert.NotEqual(NodeKind.Directory, tree.Resolve(snap, "history/link")!.Value.Kind);
        Assert.NotEqual(NodeKind.Directory, tree.Resolve(snap, "history/sub")!.Value.Kind);

        // И при этом они РАЗРЕШАЮТСЯ — как то, чем являются. Возвращать
        // «нет такого» на имя, которое сам же перечислил, нельзя: `ls -l`
        // спрашивает про каждую запись листинга и падает на первой такой.
        Assert.Equal(NodeKind.Symlink, tree.Resolve(snap, "history/link")!.Value.Kind);
        Assert.Equal(NodeKind.Submodule, tree.Resolve(snap, "history/sub")!.Value.Kind);

        // Инвариант целиком: всё, что перечислено, обязано разрешаться.
        foreach (var entry in tree.List(snap, "history")!)
        {
            var resolved = tree.Resolve(snap, "history/" + entry.Name);
            Assert.True(resolved is not null,
                $"history/ lists '{entry.Name}' and cannot resolve it");
        }
    }

    // ---------- major: удаление и воссоздание ----------

    [Fact]
    public void Delete_then_recreate_does_not_glue_revisions_together()
    {
        using var repo = new RepoBuilder();
        repo.WriteFile("a.txt", "v1\n");
        repo.CommitAll("c1 create v1");
        repo.WriteFile("a.txt", "v2\n");
        repo.CommitAll("c2 change to v2");
        repo.Run("rm", "-q", "a.txt");
        repo.Run("commit", "-m", "c3 delete");
        repo.WriteFile("noise.txt", "x\n");
        repo.CommitAll("c4 noise");
        repo.WriteFile("a.txt", "v2\n");     // то же содержимое, что было в c2
        repo.CommitAll("c5 recreate v2");

        using var snap = RepoSnapshot.Load(repo.GitDir);
        var versions = Names(Tree().List(snap, "history/a.txt"))
            .Where(n => n != "latest.txt" && n != HistoryView.TruncatedMarker).ToArray();

        // git log -- a.txt показывает c5, c3, c2, c1; наши ревизии — три
        // содержательные точки: c5(v2), c2(v2), c1(v1). До фикса c2 исчезал,
        // и история утверждала, что v2 появился только в c5.
        Assert.Equal(3, versions.Length);
    }

    // ---------- major: §3.1 путь был и файлом, и директорией ----------

    [Fact]
    public void A_path_that_was_both_file_and_directory_shows_both_sides()
    {
        using var repo = new RepoBuilder();
        repo.WriteFile("thing", "i was a file\n");
        repo.CommitAll("thing as file");
        repo.Run("rm", "-q", "thing");
        repo.WriteFile("thing/child.txt", "now a directory\n");
        repo.CommitAll("thing as directory");

        using var snap = RepoSnapshot.Load(repo.GitDir);
        var listing = Names(Tree().List(snap, "history/thing"));
        // в HEAD это директория, поэтому дочерние записи обязаны быть видны
        Assert.Contains("child.txt", listing);
    }

    // ---------- minor: строгая грамматика имён версий ----------

    [Theory]
    [InlineData("0001-.txt")]          // пустой sha
    [InlineData("0001-8c1.txt")]       // sha короче семи
    [InlineData("+001-8c1384d.txt")]   // знак вместо цифры
    [InlineData(" 001-8c1384d.txt")]   // пробел вместо цифры
    [InlineData("latest.exe")]         // чужое расширение
    [InlineData("latest")]             // расширение потеряно
    public void Phantom_version_names_do_not_resolve(string leaf)
    {
        using var repo = new RepoBuilder();
        repo.WriteFile("data.txt", "v1\n");
        repo.CommitAll("first");

        using var snap = RepoSnapshot.Load(repo.GitDir);
        Assert.Null(Tree().Resolve(snap, "history/data.txt/" + leaf));
    }

    [Fact]
    public void Compound_extensions_are_preserved_and_matched()
    {
        using var repo = new RepoBuilder();
        repo.WriteFile("archive.tar.gz", "payload\n");
        repo.CommitAll("first");

        using var snap = RepoSnapshot.Load(repo.GitDir);
        var tree = Tree();
        var names = Names(tree.List(snap, "history/archive.tar.gz"));
        Assert.Contains("latest.gz", names);
        Assert.NotNull(tree.Resolve(snap, "history/archive.tar.gz/latest.gz"));
    }

    [Fact]
    public void File_without_extension_still_lists_and_resolves()
    {
        using var repo = new RepoBuilder();
        repo.WriteFile("LICENSE", "MIT\n");
        repo.CommitAll("first");

        using var snap = RepoSnapshot.Load(repo.GitDir);
        var tree = Tree();
        var names = Names(tree.List(snap, "history/LICENSE"));
        Assert.Contains("latest", names);
        Assert.NotNull(tree.Resolve(snap, "history/LICENSE/latest"));
        Assert.NotNull(tree.Resolve(snap, "history/LICENSE/" + names[0]));
    }

    // ---------- minor: несколько дней, порядок и UTC ----------

    [Fact]
    public void Dates_view_orders_distinct_days_and_uses_utc()
    {
        using var repo = new RepoBuilder();
        repo.WriteFile("a.txt", "1\n");
        repo.Run("add", "-A");
        repo.RunWithEnv(new[] { ("GIT_COMMITTER_DATE", "2026-03-01T10:00:00 +0000"),
                                ("GIT_AUTHOR_DATE", "2026-03-01T10:00:00 +0000") },
            "commit", "-m", "day one");
        repo.WriteFile("a.txt", "2\n");
        repo.Run("add", "-A");
        // поздний вечер в +05:00 — это уже следующий день локально, но тот же в UTC
        repo.RunWithEnv(new[] { ("GIT_COMMITTER_DATE", "2026-03-02T02:00:00 +0500"),
                                ("GIT_AUTHOR_DATE", "2026-03-02T02:00:00 +0500") },
            "commit", "-m", "day two in +05:00");

        using var snap = RepoSnapshot.Load(repo.GitDir);
        var days = Names(Tree().List(snap, "dates"));
        Assert.Equal(days.OrderBy(d => d, StringComparer.Ordinal), days); // отсортированы
        // 2026-03-02T02:00+05:00 == 2026-03-01T21:00Z — тот же день в UTC
        Assert.Equal(new[] { "2026-03-01" }, days);
    }

    // ---------- major: кэш истории ----------

    [Fact]
    public void Path_history_is_cached_within_a_snapshot()
    {
        using var repo = new RepoBuilder();
        for (var i = 0; i < 30; i++)
        {
            repo.WriteFile("data.txt", string.Concat(Enumerable.Repeat("x\n", i + 1)));
            repo.CommitAll($"commit {i}");
        }

        using var snap = RepoSnapshot.Load(repo.GitDir);
        var tree = Tree();
        tree.List(snap, "history/data.txt")!.ToList();
        var hitsBefore = snap.HistoryCache.Hits;
        for (var i = 0; i < 5; i++)
        {
            tree.List(snap, "history/data.txt")!.ToList();
            tree.Resolve(snap, "history/data.txt");
        }
        Assert.True(snap.HistoryCache.Hits > hitsBefore);
    }

    // ---------- minor: тег на не-коммит ----------

    [Fact]
    public void Tag_pointing_at_a_blob_is_skipped_by_the_tags_view()
    {
        using var repo = new RepoBuilder();
        repo.WriteFile("a.txt", "a\n");
        repo.CommitAll("first");
        var blob = repo.Run("rev-parse", "HEAD:a.txt").Trim();
        repo.Run("update-ref", "refs/tags/blobtag", blob);
        repo.Tag("realtag");

        using var snap = RepoSnapshot.Load(repo.GitDir);
        var tree = Tree();
        var names = Names(tree.List(snap, "tags"));
        Assert.Contains("realtag", names);
        Assert.DoesNotContain("blobtag", names); // не коммит — не показываем
        Assert.Null(tree.Resolve(snap, "tags/blobtag"));
    }

    // ---------- minor: lock-файлы не становятся ветками ----------

    [Fact]
    public void Ref_lock_files_do_not_appear_as_branches()
    {
        using var repo = new RepoBuilder();
        repo.WriteFile("a.txt", "a\n");
        repo.CommitAll("first");
        var head = repo.Run("rev-parse", "HEAD").Trim();
        File.WriteAllText(Path.Combine(repo.GitDir, "refs", "heads", "main.lock"), head + "\n");

        using var snap = RepoSnapshot.Load(repo.GitDir);
        var names = Names(Tree().List(snap, "branches"));
        Assert.Contains("main", names);
        Assert.DoesNotContain("main.lock", names);
    }
}
