using Gitfs.Core.Tests.Fixtures;
using Gitfs.Vfs.Views;

namespace Gitfs.Vfs.Tests;

/// <summary>Симлинк из git доходит до границы как таковой (спека §3.5).
/// Настоящим симлинком его делает адаптер — на Linux через readlink, на
/// Windows он остаётся файлом с путём внутри, потому что создание симлинков
/// там требует привилегии. Но ЗНАТЬ, что это симлинк, граница обязана на
/// обеих: иначе адаптеру нечего решать.</summary>
public class SymlinkTests
{
    private static (VfsMountTarget Target, RepoBuilder Repo) Open()
    {
        var repo = new RepoBuilder();
        repo.WriteFile("target.txt", "real content here\n");
        repo.CommitAll("seed");

        // симлинк кладём через индекс: рабочая копия на Windows его не сделает
        var blob = repo.RunWithInput("target.txt"u8.ToArray(), "hash-object", "-w", "--stdin").Trim();
        repo.Run("update-index", "--add", "--cacheinfo", $"120000,{blob},link.txt");
        var deep = repo.RunWithInput("../target.txt"u8.ToArray(), "hash-object", "-w", "--stdin").Trim();
        repo.Run("update-index", "--add", "--cacheinfo", $"120000,{deep},nested/up.txt");
        repo.Run("commit", "-q", "-m", "symlinks");

        var names = NamePolicy.Windows;
        var manager = new SnapshotManager(repo.GitDir);
        var tree = new VirtualTree(new IView[] { new BranchesView(names), new HistoryView(names) });
        return (new VfsMountTarget(manager, tree, "fixture"), repo);
    }

    [Fact]
    public void A_symlink_is_reported_as_a_symlink_not_as_a_file()
    {
        var (target, repo) = Open();
        using var _r = repo;
        using var _t = target;

        Assert.Equal(NodeKind.Symlink, target.Lookup("branches/main/link.txt").Value.Kind);
        Assert.Equal(NodeKind.Symlink, target.Lookup("branches/main/nested/up.txt").Value.Kind);
        Assert.Equal(NodeKind.File, target.Lookup("branches/main/target.txt").Value.Kind);
    }

    [Fact]
    public void The_content_of_a_symlink_is_its_target_path()
    {
        // это и читает readlink: в git цель лежит содержимым блоба,
        // а не отдельным полем
        var (target, repo) = Open();
        using var _r = repo;
        using var _t = target;

        Assert.Equal("target.txt", Read(target, "branches/main/link.txt"));
        Assert.Equal("../target.txt", Read(target, "branches/main/nested/up.txt"));
    }

    [Fact]
    public void The_reported_size_matches_the_target_length()
    {
        // ls -l показывает именно это число, и оно обязано совпадать с
        // длиной цели, иначе readlink и stat расходятся
        var (target, repo) = Open();
        using var _r = repo;
        using var _t = target;

        Assert.Equal("target.txt".Length, target.Lookup("branches/main/link.txt").Value.Size);
        Assert.Equal("../target.txt".Length, target.Lookup("branches/main/nested/up.txt").Value.Size);
    }

    [Fact]
    public void A_symlink_is_listed_as_a_symlink_too()
    {
        // Проводник и ls читают листинг, а не Lookup: если вид записи
        // теряется здесь, симлинк выглядит обычным файлом при просмотре
        // папки, даже когда Lookup отвечает правильно
        var (target, repo) = Open();
        using var _r = repo;
        using var _t = target;

        var listing = target.List("branches/main").Value.ToDictionary(e => e.Name, e => e.Info);
        Assert.Equal(NodeKind.Symlink, listing["link.txt"].Kind);
        Assert.Equal(NodeKind.File, listing["target.txt"].Kind);
    }

    [Fact]
    public void A_symlink_never_opens_as_a_folder_of_versions()
    {
        // history/ раскрывает папкой только настоящие файлы: цепочка версий
        // символической ссылки — бессмыслица, и это уже было закрыто ревью M4
        var (target, repo) = Open();
        using var _r = repo;
        using var _t = target;

        var node = target.Lookup("history/link.txt");
        Assert.False(node.IsOk && node.Value.Kind == NodeKind.Directory,
            "a symlink was expanded into a version folder");
    }

    private static string Read(VfsMountTarget target, string path)
    {
        var handle = target.Open(path, OpenMode.Read).Value;
        try
        {
            var buffer = new byte[512];
            var read = target.Read(handle, 0, buffer).Value;
            return System.Text.Encoding.UTF8.GetString(buffer, 0, read);
        }
        finally { target.Close(handle); }
    }
}
