using System.Text;
using Gitfs.Core.Tests.Fixtures;
using Gitfs.Vfs;
using Gitfs.Vfs.Views;

namespace Gitfs.Vfs.Tests;

/// <summary>Оформление корневых папок в Проводнике (бриф §2.2 — по нему это
/// ОСНОВНАЯ поверхность продукта: своих экранов у gitfs почти нет).
///
/// Проверяется то, что действительно в нашей власти: какие байты том отдаёт,
/// какими атрибутами и на какой платформе. Нарисует ли Проводник иконку по
/// этому desktop.ini — решает Windows, и обещать это за него нельзя; здесь
/// подтверждается, что всё, чего он ждёт, лежит на месте.</summary>
public class ShellFoldersTests : IDisposable
{
    private readonly bool _wasEnabled = ShellFolders.Enabled;

    public void Dispose() => ShellFolders.Enabled = _wasEnabled;

    private static (RepoBuilder Repo, SnapshotManager Manager, VirtualTree Tree) Mounted()
    {
        var repo = new RepoBuilder();
        repo.WriteFile("f.txt", "x\n");
        repo.CommitAll("first");
        var names = NamePolicy.For(NamePolicyKind.Native);
        var tree = new VirtualTree(new IView[]
        {
            new BranchesView(names),
            new HistoryView(names),
            new GitfsView(names, new MountLog()),
        }, "myrepo");
        return (repo, new SnapshotManager(repo.GitDir), tree);
    }

    // ---------- на Linux этого нет вовсе ----------

    /// <summary>Ни один файловый менеджер вне Windows про desktop.ini не
    /// знает. Отдавать его там значило бы положить служебный файл в КАЖДУЮ
    /// вьюху — видимый мусор ради оформления, которого не будет.</summary>
    [Fact]
    public void Nothing_is_served_when_the_shell_does_not_ask_for_it()
    {
        ShellFolders.Enabled = false;
        var (repo, manager, tree) = Mounted();
        using var _r = repo;
        using var _m = manager;

        Assert.Null(ShellFolders.DesktopIniFor("branches"));
        Assert.Null(ShellFolders.AutorunFor("myrepo"));
        Assert.DoesNotContain(ShellFolders.DesktopIni,
            tree.List(manager.Current, "branches")!.Select(e => e.Name));
        Assert.DoesNotContain(ShellFolders.AutorunInf,
            tree.List(manager.Current, "")!.Select(e => e.Name));
        Assert.Null(tree.Resolve(manager.Current, "branches/" + ShellFolders.DesktopIni));
    }

    // ---------- на Windows всё на месте ----------

    [Fact]
    public void Every_view_root_carries_its_own_desktop_ini()
    {
        ShellFolders.Enabled = true;
        var (repo, manager, tree) = Mounted();
        using var _r = repo;
        using var _m = manager;

        foreach (var view in new[] { "branches", "history", ".gitfs" })
        {
            var listed = tree.List(manager.Current, view)!.Select(e => e.Name).ToList();
            Assert.Contains(ShellFolders.DesktopIni, listed);

            var node = tree.Resolve(manager.Current, $"{view}/{ShellFolders.DesktopIni}");
            Assert.NotNull(node);
            var bytes = tree.ReadSynthetic(manager.Current, $"{view}/{ShellFolders.DesktopIni}");
            Assert.NotNull(bytes);
            Assert.Equal(node!.Value.Size, bytes!.LongLength);
        }
    }

    /// <summary>Проводник смотрит в desktop.ini ТОЛЬКО у папки с атрибутом
    /// SYSTEM, а сам файл обязан быть скрытым — иначе он торчит в каждой
    /// вьюхе служебной строкой.</summary>
    [Fact]
    public void The_attributes_are_the_ones_the_shell_requires()
    {
        ShellFolders.Enabled = true;
        var (repo, manager, tree) = Mounted();
        using var _r = repo;
        using var _m = manager;

        var folder = tree.Resolve(manager.Current, "branches");
        Assert.True(folder!.Value.System, "the view folder is not marked system");

        var ini = tree.Resolve(manager.Current, "branches/" + ShellFolders.DesktopIni);
        Assert.True(ini!.Value.Hidden, "desktop.ini would be visible in every view");

        // и в листинге корня папка помечена так же, как её отдаёт Resolve
        var root = tree.List(manager.Current, "")!.First(e => e.Name == "branches");
        Assert.True(root.Info.System);
    }

    [Fact]
    public void The_ini_points_at_an_icon_that_the_volume_actually_serves()
    {
        ShellFolders.Enabled = true;
        var (repo, manager, tree) = Mounted();
        using var _r = repo;
        using var _m = manager;

        var text = Encoding.UTF8.GetString(
            tree.ReadSynthetic(manager.Current, "branches/" + ShellFolders.DesktopIni)!);
        Assert.Contains("[.ShellClassInfo]", text);
        Assert.Contains("IconResource=", text);

        // ссылка относительная: она обязана работать при любой букве диска
        Assert.Contains(@"..\.gitfs\icons\branches.ico", text);

        // и по этой ссылке на томе ДЕЙСТВИТЕЛЬНО лежит иконка
        var icon = tree.ReadSynthetic(manager.Current, ".gitfs/icons/branches.ico");
        Assert.NotNull(icon);
        Assert.Equal(1, BitConverter.ToUInt16(icon!, 2));      // это ICO
        Assert.True(BitConverter.ToUInt16(icon, 4) >= 4, "the icon lost its sizes");
    }

    [Fact]
    public void Every_view_that_promises_an_icon_has_one()
    {
        ShellFolders.Enabled = true;
        var (repo, manager, tree) = Mounted();
        using var _r = repo;
        using var _m = manager;

        var listed = tree.List(manager.Current, ".gitfs/icons")!.Select(e => e.Name).ToList();
        Assert.NotEmpty(listed);
        foreach (var name in listed)
        {
            var bytes = tree.ReadSynthetic(manager.Current, ".gitfs/icons/" + name);
            Assert.True(bytes is { Length: > 0 }, $"{name} is listed but empty");
        }

        // ссылка из каждого desktop.ini указывает на файл из этого списка
        foreach (var view in new[] { "branches", "history", ".gitfs" })
        {
            var text = Encoding.UTF8.GetString(
                tree.ReadSynthetic(manager.Current, $"{view}/{ShellFolders.DesktopIni}")!);
            var icon = text.Split("IconResource=")[1].Split(',')[0].Split('\\')[^1];
            Assert.Contains(icon, listed);
        }
    }

    [Fact]
    public void The_volume_offers_its_own_icon_through_autorun()
    {
        ShellFolders.Enabled = true;
        var (repo, manager, tree) = Mounted();
        using var _r = repo;
        using var _m = manager;

        var listed = tree.List(manager.Current, "")!.Select(e => e.Name).ToList();
        Assert.Contains(ShellFolders.AutorunInf, listed);

        var text = Encoding.UTF8.GetString(
            tree.ReadSynthetic(manager.Current, ShellFolders.AutorunInf)!);
        Assert.Contains("[autorun]", text);
        Assert.Contains("volume.ico", text);
        Assert.Contains("myrepo", text);       // метка называет репозиторий

        Assert.NotNull(tree.ReadSynthetic(manager.Current, ".gitfs/icons/volume.ico"));
    }

    [Fact]
    public void The_decoration_is_read_only_like_the_rest_of_the_service_view()
    {
        ShellFolders.Enabled = true;
        var (repo, manager, tree) = Mounted();
        using var _r = repo;
        using var _m = manager;
        using var overlay = Gitfs.Vfs.Overlay.OverlayStore.Create();
        using var target = new VfsMountTarget(manager, tree, "myrepo", readOnly: false,
            overlay: overlay);

        var opened = target.Open(".gitfs/icons/branches.ico", OpenMode.Write);
        Assert.False(opened.IsOk);
        Assert.Equal(GitfsError.AccessDenied, opened.Error);
    }
}
