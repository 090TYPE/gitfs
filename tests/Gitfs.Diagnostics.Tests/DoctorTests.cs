using Gitfs.Core.Tests.Fixtures;
using Gitfs.Vfs.Overlay;

namespace Gitfs.Diagnostics.Tests;

/// <summary>doctor существует ради одного: сказать «не хватает вот этого» ДО
/// того, как пользователь попробует смонтировать. Проверки здесь охраняют
/// именно это свойство — оно уже дважды ломалось молча.</summary>
public class DoctorTests
{
    private static Check Find(IReadOnlyList<Check> checks, string name) =>
        checks.FirstOrDefault(c => c.Name == name)
        ?? throw new Xunit.Sdk.XunitException(
            $"no check named '{name}'; got: {string.Join(", ", checks.Select(c => c.Name))}");

    [Fact]
    public void The_first_check_is_the_driver_this_platform_actually_needs()
    {
        // Под Linux первой строкой стояло «winfsp: не нужен на этой
        // платформе» — сообщение, не несущее информации, на месте
        // единственного важного вопроса: есть ли libfuse3.
        var checks = Doctor.Run(null);
        var expected = OperatingSystem.IsWindows() ? "winfsp"
            : OperatingSystem.IsLinux() ? "fuse"
            : "adapter";
        Assert.Equal(expected, checks[0].Name);
    }

    [Fact]
    public void The_other_platforms_driver_is_not_mentioned_at_all()
    {
        var names = Doctor.Run(null).Select(c => c.Name).ToList();
        if (OperatingSystem.IsWindows()) Assert.DoesNotContain("fuse", names);
        if (OperatingSystem.IsLinux()) Assert.DoesNotContain("winfsp", names);
    }

    [Fact]
    public void A_platform_without_an_adapter_is_a_failure_not_a_shrug()
    {
        // macOS: адаптера нет, и doctor обязан сказать это отказом, а не
        // молчанием — иначе пользователь узнает об этом от кнопки «Смонтировать»
        if (OperatingSystem.IsWindows() || OperatingSystem.IsLinux()) return;
        var driver = Doctor.Run(null)[0];
        Assert.Equal(CheckStatus.Fail, driver.Status);
        Assert.NotNull(driver.Fix);
    }

    [Fact]
    public void Every_failure_carries_exactly_one_thing_to_do()
    {
        // макет дизайн-отдела: каждая строка — либо «дальше можно», либо
        // ровно одно действие. Отказ без подсказки — тупик.
        foreach (var check in Doctor.Run(null).Where(c => c.Status == CheckStatus.Fail))
            Assert.False(string.IsNullOrWhiteSpace(check.Fix), $"{check.Name} fails without a fix");
    }

    [Fact]
    public void A_repository_is_reported_with_its_git_directory()
    {
        using var repo = new RepoBuilder();
        repo.WriteFile("a.txt", "one\n");
        repo.CommitAll("first");

        var checks = Doctor.Run(repo.Root);
        var repository = Find(checks, "repository");
        Assert.Equal(CheckStatus.Ok, repository.Status);
        Assert.Equal(repo.GitDir, repository.Value, ignoreCase: true);
        Assert.Equal(CheckStatus.Ok, Find(checks, "hash format").Status);
    }

    [Fact]
    public void A_path_that_is_not_a_repository_is_reported_as_such()
    {
        var empty = Path.Combine(Path.GetTempPath(), "gitfs-doctor-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(empty);
        try
        {
            var repository = Find(Doctor.Run(empty), "repository");
            Assert.Equal(CheckStatus.Fail, repository.Status);
            Assert.False(string.IsNullOrWhiteSpace(repository.Fix));
        }
        finally { Directory.Delete(empty, recursive: true); }
    }

    [Fact]
    public void The_commit_graph_is_noticed_when_it_exists()
    {
        using var repo = new RepoBuilder();
        repo.WriteFile("a.txt", "one\n");
        repo.CommitAll("first");

        Assert.Equal(CheckStatus.Warn, Find(Doctor.Run(repo.Root), "accelerators").Status);

        repo.Run("commit-graph", "write", "--reachable");
        var after = Find(Doctor.Run(repo.Root), "accelerators");
        Assert.Equal(CheckStatus.Ok, after.Status);
        Assert.Contains("commit-graph", after.Value);
    }

    [Fact]
    public void A_live_sandbox_is_never_reported_as_an_orphan()
    {
        // doctor однажды предлагал удалить песочницу работающего тома
        var baseDir = Path.Combine(Path.GetTempPath(), "gitfs-doctor-ov-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            using var live = OverlayStore.Create(baseDir);
            Assert.Empty(OverlayStore.FindOrphans(baseDir));
        }
        finally { if (Directory.Exists(baseDir)) Directory.Delete(baseDir, recursive: true); }
    }

    [Fact]
    public void Doctor_advertises_only_commands_that_exist()
    {
        // doctor однажды советовал `gitfs unmount --purge`, которой не было
        // ни в каком виде. Совет обязан называть настоящую команду.
        var known = new[] { "gitfs purge", "gitfs tree", "gitfs mount", "gitfs doctor", "gitfs cat", "gitfs list" };
        foreach (var check in Doctor.Run(null))
        {
            var fix = check.Fix;
            if (fix is null || !fix.Contains("gitfs ", StringComparison.Ordinal)) continue;
            Assert.True(known.Any(k => fix.Contains(k, StringComparison.Ordinal)),
                $"'{check.Name}' suggests a gitfs command that is not in the CLI: {fix}");
        }
    }

    [Fact]
    public void Doctor_reads_the_environment_and_never_writes_to_it()
    {
        using var repo = new RepoBuilder();
        repo.WriteFile("a.txt", "one\n");
        repo.CommitAll("first");
        var before = repo.Run("status", "--porcelain");
        var objectsBefore = Directory.GetFiles(Path.Combine(repo.GitDir, "objects"), "*",
            SearchOption.AllDirectories).Length;

        Doctor.Run(repo.Root);

        Assert.Equal(before, repo.Run("status", "--porcelain"));
        Assert.Equal(objectsBefore, Directory.GetFiles(Path.Combine(repo.GitDir, "objects"), "*",
            SearchOption.AllDirectories).Length);
    }
}
