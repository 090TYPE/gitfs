using Gitfs.Core.Tests.Fixtures;

namespace Gitfs.Core.Tests;

public class RepoBuilderTests
{
    [Fact]
    public void Builds_repo_with_deterministic_commits_and_lists_objects()
    {
        using var repo = new RepoBuilder();
        repo.WriteFile("README.md", "hello\n");
        var c1 = repo.CommitAll("first");
        repo.WriteFile("src/Program.cs", "class P {}\n");
        var c2 = repo.CommitAll("second");

        Assert.NotEqual(c1, c2);
        Assert.Equal(c2, repo.Run("rev-parse", "HEAD").Trim());

        var objects = repo.AllObjects().ToList();
        // 2 коммита, 3 дерева (root×2 + src), 2 блоба
        Assert.Equal(2, objects.Count(o => o.Type == "commit"));
        Assert.Equal(2, objects.Count(o => o.Type == "blob"));
        Assert.True(objects.Count(o => o.Type == "tree") >= 3);
    }
}
