using Gitfs.Core.Objects;
using Gitfs.Core.Tests.Fixtures;
using Gitfs.Vfs;
using Gitfs.Vfs.Overlay;
using Gitfs.Vfs.Views;

namespace Gitfs.App.Tests;

/// <summary>Секция Advanced в диалоге монтирования. Один тест на одно поле,
/// и каждый проверяет ПОВЕДЕНИЕ, а не то, что значение доехало до свойства:
/// поле, которое красиво хранится и ни на что не влияет, — ровно тот обман,
/// ради предотвращения которого эти настройки и собраны в одном месте.
///
/// Все проверки идут без драйвера файловой системы: настройки действуют
/// ниже границы монтирования, поэтому их видно на голом дереве вьюх.</summary>
[Collection("overlay-root")]
public class MountOptionsTests
{
    private static readonly string[] AllViews =
        { "branches", "tags", "commits", "dates", "history" };

    // ---------- лимиты вьюх ----------

    [Fact]
    public void Commit_limit_decides_how_many_commits_the_view_lists()
    {
        using var repo = new RepoBuilder();
        for (var i = 0; i < 6; i++)
        {
            repo.WriteFile("f.txt", $"v{i}\n");
            repo.CommitAll($"c{i}");
        }

        Assert.Equal(3, CommitsIn(repo, new MountOptions { CommitLimit = 3 }));
        Assert.Equal(6, CommitsIn(repo, new MountOptions { CommitLimit = 100 }));

        static int CommitsIn(RepoBuilder repo, MountOptions options)
        {
            using var snapshot = RepoSnapshot.Load(repo.GitDir, options: options);
            var tree = MountService.BuildTree(AllViews, options);
            return tree.List(snapshot, "commits")!.Count();
        }
    }

    /// <summary>Урезание истории обязано быть ВИДНЫМ: в папке версий
    /// появляется .truncated. Молчаливое усечение — это утверждение «файл
    /// менялся столько-то раз», которое неверно.</summary>
    [Fact]
    public void History_limit_shows_a_truncated_marker_instead_of_hiding_versions()
    {
        using var repo = new RepoBuilder();
        for (var i = 0; i < 5; i++)
        {
            repo.WriteFile("f.txt", $"v{i}\n");
            repo.CommitAll($"c{i}");
        }

        // считаем именно версии: latest.txt — тоже .txt, но это не ревизия
        static int Revisions(IEnumerable<string> names) =>
            names.Count(n => n.Length > 4 && char.IsAsciiDigit(n[0]) && n[4] == '-');

        var narrow = Versions(repo, new MountOptions { HistoryLimit = 2 });
        Assert.Equal(2, Revisions(narrow));
        Assert.Contains(HistoryView.TruncatedMarker, narrow);

        var wide = Versions(repo, new MountOptions { HistoryLimit = 100 });
        Assert.Equal(5, Revisions(wide));
        Assert.DoesNotContain(HistoryView.TruncatedMarker, wide);

        static List<string> Versions(RepoBuilder repo, MountOptions options)
        {
            using var snapshot = RepoSnapshot.Load(repo.GitDir, options: options);
            var tree = MountService.BuildTree(AllViews, options);
            return tree.List(snapshot, "history/f.txt")!.Select(e => e.Name).ToList();
        }
    }

    // ---------- опорная точка ----------

    /// <summary>«Покажи историю такой, какой она была на v1.0». Проверяется
    /// содержимым дерева, а не датой: дата совпала бы и по случайности.</summary>
    [Fact]
    public void History_ref_moves_the_starting_point_off_head()
    {
        using var repo = new RepoBuilder();
        repo.WriteFile("old.txt", "one\n");
        repo.CommitAll("first");
        repo.Tag("v1.0");
        repo.WriteFile("new.txt", "two\n");
        repo.CommitAll("second");

        Assert.Contains("new.txt", HistoryRoot(repo, new MountOptions()));

        var atTag = HistoryRoot(repo, new MountOptions { HistoryRef = "v1.0" });
        Assert.Contains("old.txt", atTag);
        Assert.DoesNotContain("new.txt", atTag);

        // короткое имя ветки и полное имя ссылки — одна и та же точка
        Assert.Equal(HistoryRoot(repo, new MountOptions { HistoryRef = "main" }),
                     HistoryRoot(repo, new MountOptions { HistoryRef = "refs/heads/main" }));

        // и полный SHA тоже: git разрешает имя именно в таком порядке
        var sha = repo.Run("rev-parse", "refs/tags/v1.0^{commit}").Trim();
        Assert.Equal(atTag, HistoryRoot(repo, new MountOptions { HistoryRef = sha }));

        static List<string> HistoryRoot(RepoBuilder repo, MountOptions options)
        {
            using var snapshot = RepoSnapshot.Load(repo.GitDir, options: options);
            var tree = MountService.BuildTree(AllViews, options);
            return tree.List(snapshot, "history")!.Select(e => e.Name).ToList();
        }
    }

    /// <summary>Опорная точка, которой нет, не должна выдавать пустое дерево
    /// молча — но и ронять том тоже нельзя. history/ становится пустой,
    /// остальные вьюхи продолжают работать.</summary>
    [Fact]
    public void A_history_ref_that_does_not_exist_leaves_the_rest_of_the_volume_alive()
    {
        using var repo = new RepoBuilder();
        repo.WriteFile("f.txt", "one\n");
        repo.CommitAll("first");

        var options = new MountOptions { HistoryRef = "no-such-thing" };
        using var snapshot = RepoSnapshot.Load(repo.GitDir, options: options);
        var tree = MountService.BuildTree(AllViews, options);

        Assert.Empty(tree.List(snapshot, "history") ?? []);
        Assert.NotEmpty(tree.List(snapshot, "branches")!);
    }

    // ---------- политика имён ----------

    /// <summary>portable — правила Windows на любой системе. Имя с двоеточием
    /// невозможно создать в рабочем каталоге на Windows, поэтому дерево
    /// пишется напрямую через git mktree: настройка проверяется одинаково на
    /// обеих платформах.</summary>
    [Fact]
    public void Portable_names_apply_the_windows_rules_on_every_platform()
    {
        using var repo = new RepoBuilder();
        repo.WriteFile("plain.txt", "x\n");
        repo.CommitAll("first");

        var blob = repo.Run("rev-parse", "HEAD:plain.txt").Trim();
        var tree = repo.RunWithInput(
            System.Text.Encoding.UTF8.GetBytes($"100644 blob {blob}\ta:b.txt\n"),
            "mktree").Trim();
        var commit = repo.Run("commit-tree", tree, "-m", "colon").Trim();
        repo.Run("update-ref", "refs/heads/main", commit);

        var portable = Listing(repo, NamePolicyKind.Portable);
        Assert.Contains("a%3Ab.txt", portable);
        Assert.DoesNotContain("a:b.txt", portable);

        var native = Listing(repo, NamePolicyKind.Native);
        if (OperatingSystem.IsWindows())
            // на Windows native И ЕСТЬ портируемая политика — разницы нет
            // и быть не должно; проверять здесь нечего, кроме этого равенства
            Assert.Equal(portable, native);
        else
            Assert.Contains("a:b.txt", native);

        static List<string> Listing(RepoBuilder repo, NamePolicyKind kind)
        {
            var options = new MountOptions { NamePolicy = kind };
            using var snapshot = RepoSnapshot.Load(repo.GitDir, options: options);
            var tree = MountService.BuildTree(AllViews, options);
            return tree.List(snapshot, "branches/main")!.Select(e => e.Name).ToList();
        }
    }

    // ---------- размеры кэшей ----------

    [Fact]
    public void Cache_megabytes_reaches_the_caches_of_the_snapshot()
    {
        using var repo = new RepoBuilder();
        repo.WriteFile("f.txt", "x\n");
        repo.CommitAll("first");

        using var small = RepoSnapshot.Load(repo.GitDir,
            options: new MountOptions { CacheMegabytes = 6, MaxCachedBlobMegabytes = 1 });
        using var large = RepoSnapshot.Load(repo.GitDir,
            options: new MountOptions { CacheMegabytes = 600, MaxCachedBlobMegabytes = 1 });

        Assert.Equal((6L << 20) * 2 / 3, small.TreeCache.MaxCost);
        Assert.Equal((6L << 20) / 3, small.ListingCache.MaxCost);
        Assert.True(large.TreeCache.MaxCost > small.TreeCache.MaxCost * 10);
    }

    /// <summary>«Max cached blob» — потолок для ОДНОЙ записи, а не для кэша.
    /// До этой правки поле существовало в диалоге и не влияло ни на что:
    /// LruCache отсекал только то, что не влезало в бюджет целиком.</summary>
    [Fact]
    public void Max_cached_blob_keeps_one_giant_out_without_shrinking_the_cache()
    {
        var cache = new LruCache<string, byte[]>(1000, b => b.Length, maxItemCost: 100);

        cache.Set("small", new byte[50]);
        cache.Set("giant", new byte[500]);   // влезает в 1000, но не в 100

        Assert.True(cache.TryGet("small", out _));
        Assert.False(cache.TryGet("giant", out _));
        Assert.Equal(50, cache.Used);

        // без потолка тот же гигант попадает в кэш — значит, различие даёт
        // именно настройка, а не что-то ещё
        var open = new LruCache<string, byte[]>(1000, b => b.Length);
        open.Set("giant", new byte[500]);
        Assert.True(open.TryGet("giant", out _));
    }

    [Fact]
    public void A_per_item_ceiling_can_never_exceed_the_whole_budget()
    {
        // иначе «потолок» тихо становится больше кэша и перестаёт быть потолком
        var cache = new LruCache<string, byte[]>(100, b => b.Length, maxItemCost: 100_000);
        Assert.Equal(100, cache.MaxItemCost);
        cache.Set("giant", new byte[500]);
        Assert.False(cache.TryGet("giant", out _));
    }

    // ---------- режим тома и песочница ----------

    [Fact]
    public void Read_only_refuses_a_write_and_names_the_reason()
    {
        using var repo = new RepoBuilder();
        repo.WriteFile("f.txt", "x\n");
        repo.CommitAll("first");

        using var manager = new SnapshotManager(repo.GitDir);
        using var overlay = OverlayStore.Create();
        using var target = new VfsMountTarget(manager, MountService.BuildTree(AllViews),
            "repo", readOnly: true, overlay: overlay);

        var opened = target.Open("branches/main/f.txt", OpenMode.Write);
        Assert.False(opened.IsOk);
        Assert.Equal(GitfsError.AccessDenied, opened.Error);

        // и удаление тоже: том только на чтение — это не «почти только»
        Assert.Equal(GitfsError.AccessDenied, target.Delete("branches/main/f.txt").Error);
    }

    [Fact]
    public void A_writable_volume_actually_accepts_the_same_write()
    {
        // вторая половина предыдущей проверки: без неё «отказано» могло бы
        // означать что угодно — например, что путь просто не открывается
        using var repo = new RepoBuilder();
        repo.WriteFile("f.txt", "x\n");
        repo.CommitAll("first");

        using var manager = new SnapshotManager(repo.GitDir);
        using var overlay = OverlayStore.Create();
        using var target = new VfsMountTarget(manager, MountService.BuildTree(AllViews),
            "repo", readOnly: false, overlay: overlay);

        var opened = target.Open("branches/main/f.txt", OpenMode.Write);
        Assert.True(opened.IsOk, $"a writable volume refused a write: {opened.Error}");
        target.Close(opened.Value);
    }

    [Fact]
    public void Keeping_the_overlay_is_the_difference_between_a_directory_that_survives_and_one_that_does_not()
    {
        string kept, dropped;
        using (var overlay = OverlayStore.Create(keepOnDispose: true))
        {
            kept = overlay.Root;
            Assert.True(Directory.Exists(kept));
        }
        using (var overlay = OverlayStore.Create(keepOnDispose: false))
        {
            dropped = overlay.Root;
            Assert.True(Directory.Exists(dropped));
        }

        Assert.True(Directory.Exists(kept), "keep overlay was asked for and the sandbox vanished");
        Assert.False(Directory.Exists(dropped), "the sandbox outlived the volume that owned it");

        Directory.Delete(kept, recursive: true); // за собой прибираем сами
    }

    // ---------- проверка значений ----------

    [Theory]
    [InlineData(0, 500, 96, 8)]
    [InlineData(200, 0, 96, 8)]
    [InlineData(200, 500, 0, 8)]
    [InlineData(200, 500, 96, 0)]
    [InlineData(200, 500, 4, 8)]      // потолок объекта больше всего кэша
    [InlineData(200_000, 500, 96, 8)]
    public void Impossible_numbers_are_named_before_anything_is_created(
        int commits, int history, int cache, int blob)
    {
        var options = new MountOptions
        {
            CommitLimit = commits,
            HistoryLimit = history,
            CacheMegabytes = cache,
            MaxCachedBlobMegabytes = blob,
        };
        var problem = options.Validate();
        Assert.False(string.IsNullOrWhiteSpace(problem), "an impossible setting was accepted");
        // сообщение адресовано человеку, а не журналу
        Assert.DoesNotContain("Exception", problem);
    }

    [Fact]
    public void The_defaults_are_valid()
    {
        Assert.Null(MountOptions.Default.Validate());
        Assert.Null(new MountOptions().Validate());
    }

    /// <summary>Диалог гасит кнопку, но Mount вызывается не только из него.
    /// Отказ обязан случиться ДО того, как появится песочница.</summary>
    [Fact]
    public void Mount_refuses_impossible_settings_without_creating_anything()
    {
        var service = new MountService();
        if (!service.CanMount) return;

        using var repo = new RepoBuilder();
        repo.WriteFile("f.txt", "x\n");
        repo.CommitAll("first");

        var before = MountServiceTests.SandboxCount();

        Assert.Throws<ArgumentException>(() => service.Mount(repo.Root, "Z:", AllViews,
            new MountOptions { CacheMegabytes = 0 }));

        Assert.Equal(before, MountServiceTests.SandboxCount());
        Assert.Empty(service.Entries);
    }
}
