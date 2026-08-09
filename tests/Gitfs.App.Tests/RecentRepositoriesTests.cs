using Gitfs.App;
using Gitfs.Core.Tests.Fixtures;

namespace Gitfs.App.Tests;

/// <summary>Список недавних репозиториев — фишки под полем пути (макет 03).
/// Проверки написаны против ФАЙЛА, а не против свойства в памяти: список
/// живёт между запусками, и всё интересное с ним случается именно на диске.</summary>
public class RecentRepositoriesTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(),
        "gitfs-recent-" + Guid.NewGuid().ToString("N"));

    private RecentRepositories New() =>
        new(Path.Combine(_dir, "recent.txt"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (Exception) { }
    }

    [Fact]
    public void An_empty_history_is_an_empty_list_and_not_a_crash()
    {
        // первый запуск: файла нет вовсе
        Assert.Empty(New().Load());
    }

    [Fact]
    public void The_freshest_repository_comes_first()
    {
        using var a = Repo();
        using var b = Repo();
        var recent = New();

        recent.Remember(a.Root);
        recent.Remember(b.Root);

        var listed = recent.Load().Select(e => e.Path).ToList();
        Assert.Equal(2, listed.Count);
        Assert.Equal(Path.GetFullPath(b.Root), listed[0]);
    }

    [Fact]
    public void Mounting_the_same_repository_twice_does_not_list_it_twice()
    {
        using var a = Repo();
        using var b = Repo();
        var recent = New();

        recent.Remember(a.Root);
        recent.Remember(b.Root);
        recent.Remember(a.Root);   // вернулись к первому

        var listed = recent.Load().Select(e => e.Path).ToList();
        Assert.Equal(2, listed.Count);                       // а не три
        Assert.Equal(Path.GetFullPath(a.Root), listed[0]);   // и он снова сверху
    }

    [Fact]
    public void The_list_stops_growing_at_its_capacity()
    {
        var recent = New();
        // Каталогов заводить не нужно: важно, что список не растёт.
        for (var i = 0; i < RecentRepositories.Capacity * 3; i++)
            recent.Remember(Path.Combine(_dir, "repo-" + i));

        Assert.Equal(RecentRepositories.Capacity, recent.Load().Count);
    }

    /// <summary>Исчезнувший репозиторий обязан остаться в списке — но
    /// ПОМЕЧЕННЫМ. Диалог гасит такую фишку. Молча выбросить её значило бы
    /// стереть след того, что человек делал.</summary>
    [Fact]
    public void A_repository_that_is_gone_stays_listed_but_marked()
    {
        var repo = Repo();
        var root = repo.Root;
        var recent = New();
        recent.Remember(root);

        Assert.True(recent.Load().Single().StillARepository);

        repo.Dispose();                       // каталог исчез
        var entry = recent.Load().Single();
        Assert.Equal(Path.GetFullPath(root), entry.Path);
        Assert.False(entry.StillARepository); // и это видно
    }

    [Fact]
    public void A_directory_that_is_not_a_repository_is_marked_too()
    {
        var plain = Path.Combine(_dir, "not-a-repo");
        Directory.CreateDirectory(plain);
        var recent = New();
        recent.Remember(plain);

        Assert.False(recent.Load().Single().StillARepository);
    }

    [Fact]
    public void Forgetting_removes_exactly_one_entry()
    {
        using var a = Repo();
        using var b = Repo();
        var recent = New();
        recent.Remember(a.Root);
        recent.Remember(b.Root);

        recent.Forget(Path.GetFullPath(a.Root));

        var listed = recent.Load().Select(e => e.Path).ToList();
        Assert.Equal(new[] { Path.GetFullPath(b.Root) }, listed);
    }

    /// <summary>Список — удобство. Испорченный файл не имеет права мешать
    /// приложению открыться, а пустые строки не должны становиться фишками
    /// без названия.</summary>
    [Fact]
    public void A_damaged_file_costs_the_list_but_not_the_application()
    {
        var path = Path.Combine(_dir, "recent.txt");
        Directory.CreateDirectory(_dir);
        File.WriteAllText(path, "\n\n   \n");

        var recent = New();
        Assert.Empty(recent.Load());

        // и он остаётся пригодным к записи
        using var a = Repo();
        recent.Remember(a.Root);
        Assert.Single(recent.Load());
    }

    [Fact]
    public void The_name_on_a_chip_is_the_folder_name_not_the_whole_path()
    {
        using var a = Repo();
        var recent = New();
        recent.Remember(a.Root);

        var entry = recent.Load().Single();
        Assert.Equal(new DirectoryInfo(a.Root).Name, entry.Name);
        Assert.NotEqual(entry.Path, entry.Name);
    }

    [Fact]
    public void A_relative_path_is_stored_as_an_absolute_one()
    {
        // иначе фишка сработает только из того каталога, где её создали
        var recent = New();
        recent.Remember(".");
        var stored = recent.Load().Single().Path;
        Assert.True(Path.IsPathRooted(stored), $"stored a relative path: {stored}");
    }

    private static RepoBuilder Repo()
    {
        var repo = new RepoBuilder();
        repo.WriteFile("f.txt", "x\n");
        repo.CommitAll("first");
        return repo;
    }
}
