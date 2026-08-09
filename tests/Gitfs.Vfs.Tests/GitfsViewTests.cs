using System.Text;
using Gitfs.Core.Tests.Fixtures;
using Gitfs.Vfs;
using Gitfs.Vfs.Overlay;
using Gitfs.Vfs.Views;

namespace Gitfs.Vfs.Tests;

/// <summary>Служебная вьюха `.gitfs/` — спека §3 (дерево) и §14
/// (диагностика). Смысл её в том, что понять поведение тома можно НЕ ВЫХОДЯ
/// из тома: ни отдельного инструмента, ни доступа к машине.</summary>
public class GitfsViewTests
{
    private static RepoBuilder Repo()
    {
        var repo = new RepoBuilder();
        repo.WriteFile("a.txt", "one\n");
        repo.CommitAll("first");
        return repo;
    }

    private static VirtualTree Tree(MountLog log, OverlayStore? overlay = null)
    {
        var names = NamePolicy.For(NamePolicyKind.Native);
        return new VirtualTree(new IView[]
        {
            new BranchesView(names),
            new HistoryView(names, log: log),
            new GitfsView(names, log, overlay),
        });
    }

    [Fact]
    public void The_service_view_is_listed_at_the_root()
    {
        using var repo = Repo();
        using var manager = new SnapshotManager(repo.GitDir);
        var names = Tree(new MountLog()).List(manager.Current, "")!.Select(e => e.Name).ToList();
        Assert.Contains(GitfsView.ViewName, names);
    }

    [Fact]
    public void It_holds_status_and_log()
    {
        using var repo = Repo();
        using var manager = new SnapshotManager(repo.GitDir);
        var listed = Tree(new MountLog())
            .List(manager.Current, GitfsView.ViewName)!.Select(e => e.Name).ToList();
        Assert.Contains(GitfsView.StatusFile, listed);
        Assert.Contains(GitfsView.LogFile, listed);
    }

    /// <summary>Размер в stat обязан совпасть с числом отданных байт: Проводник
    /// и `cat` верят stat, и файл, объявленный нулевым, читается как пустой
    /// независимо от того, что за ним стоит.</summary>
    [Fact]
    public void The_declared_size_matches_the_bytes_that_come_out()
    {
        using var repo = Repo();
        using var manager = new SnapshotManager(repo.GitDir);
        var tree = Tree(new MountLog());

        foreach (var file in new[] { GitfsView.StatusFile, GitfsView.LogFile })
        {
            var path = GitfsView.ViewName + "/" + file;
            var node = tree.Resolve(manager.Current, path);
            Assert.NotNull(node);
            var bytes = tree.ReadSynthetic(manager.Current, path);
            Assert.NotNull(bytes);
            Assert.Equal(node!.Value.Size, bytes!.LongLength);
            Assert.True(bytes.Length > 0, $"{file} came out empty");
        }
    }

    [Fact]
    public void Status_reports_the_settings_the_volume_was_mounted_with()
    {
        using var repo = Repo();
        var options = new MountOptions { ReadOnly = true, HistoryRef = "main", CommitLimit = 7 };
        using var manager = new SnapshotManager(repo.GitDir, options: options);
        var text = Read(Tree(new MountLog()), manager, GitfsView.StatusFile);

        Assert.Contains("read-only", text);
        Assert.Contains("main", text);
        Assert.Contains("7", text);
        Assert.Contains(repo.GitDir, text);
    }

    /// <summary>Спека §14 перечисляет, что попадает в журнал. Обрезание
    /// истории — первое в списке, и оно обязано доехать до файла на томе.</summary>
    [Fact]
    public void Truncating_history_shows_up_in_the_log_file()
    {
        using var repo = new RepoBuilder();
        for (var i = 0; i < 5; i++)
        {
            repo.WriteFile("f.txt", $"v{i}\n");
            repo.CommitAll($"c{i}");
        }

        var log = new MountLog();
        var names = NamePolicy.For(NamePolicyKind.Native);
        var tree = new VirtualTree(new IView[]
        {
            new HistoryView(names, limit: 2, log: log),
            new GitfsView(names, log),
        });
        using var manager = new SnapshotManager(repo.GitDir);

        var before = Read(tree, manager, GitfsView.LogFile);
        Assert.DoesNotContain("truncated", before);

        foreach (var _ in tree.List(manager.Current, "history/f.txt")!) { }

        var after = Read(tree, manager, GitfsView.LogFile);
        Assert.Contains("truncated", after);
        Assert.Contains("f.txt", after);
    }

    [Fact]
    public void One_event_per_rule_rather_than_one_per_lookup()
    {
        // сто тысяч экранированных имён не должны превращать журнал в одну
        // повторяющуюся строку (спека §14: «первое на каждое правило»)
        var log = new MountLog();
        for (var i = 0; i < 50; i++) log.Once("rule", "escaped", "name was encoded");
        Assert.Single(log.Lines);
    }

    [Fact]
    public void The_log_stops_growing_and_says_that_it_did()
    {
        var log = new MountLog(capacity: 5);
        for (var i = 0; i < 20; i++) log.Add("test", "line " + i);

        Assert.Equal(5, log.Lines.Count);
        var text = log.Render();
        Assert.Contains("dropped", text);       // молчаливое усечение недопустимо
        Assert.Contains("line 19", text);       // свежие остались
        Assert.DoesNotContain("line 0", text);
    }

    [Fact]
    public void An_empty_log_says_so_instead_of_being_a_blank_file()
    {
        var text = new MountLog().Render();
        Assert.False(string.IsNullOrWhiteSpace(text));
    }

    // ---------- overlay/ ----------

    [Fact]
    public void What_the_user_wrote_shows_up_under_overlay()
    {
        using var repo = Repo();
        using var overlay = OverlayStore.Create();
        using var manager = new SnapshotManager(repo.GitDir);
        var log = new MountLog();
        var tree = Tree(log, overlay);

        using (var target = new VfsMountTarget(manager, tree, "repo", readOnly: false,
                   overlay: overlay))
        {
            var opened = target.Open("branches/main/a.txt", OpenMode.Write);
            Assert.True(opened.IsOk, opened.Error.ToString());
            target.Write(opened.Value, 0, Encoding.UTF8.GetBytes("changed"));
            target.Close(opened.Value);
        }

        var listed = tree.List(manager.Current, GitfsView.ViewName + "/" + GitfsView.OverlayDir)!
            .Select(e => e.Name).ToList();
        Assert.NotEmpty(listed);
    }

    /// <summary>Спека §10 обещает ВИДИМОСТЬ песочницы, а не второй способ в
    /// неё писать. Открытие на запись через .gitfs обязано быть отвергнуто.</summary>
    [Fact]
    public void The_service_view_refuses_writes()
    {
        using var repo = Repo();
        using var overlay = OverlayStore.Create();
        using var manager = new SnapshotManager(repo.GitDir);
        using var target = new VfsMountTarget(manager, Tree(new MountLog(), overlay), "repo",
            readOnly: false, overlay: overlay);

        var status = target.Open(GitfsView.ViewName + "/" + GitfsView.StatusFile, OpenMode.Write);
        Assert.False(status.IsOk);
        Assert.Equal(GitfsError.AccessDenied, status.Error);
    }

    [Fact]
    public void Status_and_log_read_through_the_mount_target_like_any_file()
    {
        // диагностика доступна «без отдельного инструмента» — то есть через
        // обычные Open/Read, теми же вызовами, что и любой файл тома
        using var repo = Repo();
        using var manager = new SnapshotManager(repo.GitDir);
        using var target = new VfsMountTarget(manager, Tree(new MountLog()), "repo");

        var opened = target.Open(GitfsView.ViewName + "/" + GitfsView.StatusFile, OpenMode.Read);
        Assert.True(opened.IsOk, opened.Error.ToString());
        try
        {
            var buffer = new byte[4096];
            var read = target.Read(opened.Value, 0, buffer);
            Assert.True(read.IsOk);
            Assert.True(read.Value > 0, "status.txt read back empty through the mount target");
            Assert.Contains("gitfs status",
                Encoding.UTF8.GetString(buffer, 0, read.Value));
        }
        finally { target.Close(opened.Value); }
    }

    private static string Read(VirtualTree tree, SnapshotManager manager, string file)
    {
        var bytes = tree.ReadSynthetic(manager.Current, GitfsView.ViewName + "/" + file);
        return bytes is null ? "" : Encoding.UTF8.GetString(bytes);
    }
}
