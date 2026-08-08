using Gitfs.Diagnostics;
using Gitfs.Core.Tests.Fixtures;

namespace Gitfs.Vfs.Tests;

public class CliTests
{
    [Fact]
    public void Doctor_reports_repository_facts_for_a_real_repo()
    {
        using var repo = new RepoBuilder();
        repo.WriteFile("a.txt", "a\n");
        repo.CommitAll("first");

        var checks = Doctor.Run(repo.Root);
        var byName = checks.ToDictionary(c => c.Name);

        Assert.Equal(CheckStatus.Ok, byName["repository"].Status);
        Assert.Equal(CheckStatus.Ok, byName["hash format"].Status);
        Assert.Equal("sha1", byName["hash format"].Value);
        Assert.Equal(CheckStatus.Ok, byName["history"].Status);
        Assert.Equal(CheckStatus.Ok, byName["git"].Status);
    }

    [Fact]
    public void Doctor_fails_clearly_when_the_path_is_not_a_repository()
    {
        var tmp = Path.Combine(Path.GetTempPath(), "gitfs-not-a-repo-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(tmp);
        try
        {
            var checks = Doctor.Run(tmp);
            var repoCheck = checks.Single(c => c.Name == "repository");
            Assert.Equal(CheckStatus.Fail, repoCheck.Status);
            Assert.NotNull(repoCheck.Fix); // каждый fail несёт «что сделать»
            Assert.Equal(1, Report.ExitCode(checks));
        }
        finally { Directory.Delete(tmp, recursive: true); }
    }

    [Fact]
    public void Shallow_clone_is_flagged_as_a_warning()
    {
        using var repo = new RepoBuilder();
        repo.WriteFile("a.txt", "a\n");
        repo.CommitAll("first");
        File.WriteAllText(Path.Combine(repo.GitDir, "shallow"), "");

        var history = Doctor.Run(repo.Root).Single(c => c.Name == "history");
        Assert.Equal(CheckStatus.Warn, history.Status);
        Assert.Contains("unshallow", history.Fix);
    }

    [Fact]
    public void Report_layout_keeps_columns_and_drops_color_when_asked()
    {
        Report.ColorEnabled = false;
        var text = Report.Render(new[]
        {
            new Check(CheckStatus.Ok, "winfsp", "1.12.22339"),
            new Check(CheckStatus.Fail, "overlay", "orphaned", "remove it", "docs.gitfs.dev/e/x"),
        });

        Assert.DoesNotContain('', text);              // ни одной ANSI-последовательности
        var lines = text.Split('\n');
        Assert.StartsWith("ok   winfsp                 1.12.22339", lines[0]);
        Assert.StartsWith("fail overlay                orphaned", lines[1]);
        Assert.Equal(lines[0].IndexOf("1.12.22339", StringComparison.Ordinal),
                     lines[1].IndexOf("orphaned", StringComparison.Ordinal)); // колонки совпали
        Assert.Contains("→ remove it", lines[2]);
        Assert.Contains("1 ok · 0 warnings · 1 failure", text);
    }

    [Fact]
    public void GitDir_resolution_handles_plain_dir_pointer_file_and_bare()
    {
        using var repo = new RepoBuilder();
        repo.WriteFile("a.txt", "a\n");
        repo.CommitAll("first");

        Assert.Equal(repo.GitDir, Doctor.ResolveGitDir(repo.Root));
        Assert.Equal(repo.GitDir, Doctor.ResolveGitDir(repo.GitDir)); // сам .git
        Assert.Null(Doctor.ResolveGitDir(Path.GetTempPath()));
    }
}
