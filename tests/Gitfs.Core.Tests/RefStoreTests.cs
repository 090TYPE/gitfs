using Gitfs.Core;
using Gitfs.Core.Refs;
using Gitfs.Core.Tests.Fixtures;

namespace Gitfs.Core.Tests;

public class RefStoreTests
{
    private static RepoBuilder BuildRepo()
    {
        var repo = new RepoBuilder();
        repo.WriteFile("a.txt", "a\n");
        repo.CommitAll("first");
        repo.Branch("feature/login");        // слэш в имени — важный случай для VFS
        repo.Tag("v1.0", annotated: true, message: "release");
        repo.Tag("lightweight");
        repo.WriteFile("a.txt", "b\n");
        repo.CommitAll("second");
        return repo;
    }

    private static Dictionary<string, string> GitShowRef(RepoBuilder repo) =>
        repo.Run("show-ref")
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Split(' ', 2))
            .ToDictionary(p => p[1], p => p[0]);

    [Fact]
    public void Loose_refs_match_git_show_ref()
    {
        using var repo = BuildRepo();
        AssertMatchesGit(repo, RefStore.Load(repo.GitDir));
    }

    [Fact]
    public void Packed_refs_match_git_show_ref()
    {
        using var repo = BuildRepo();
        repo.Run("pack-refs", "--all");       // все ссылки уезжают в packed-refs
        AssertMatchesGit(repo, RefStore.Load(repo.GitDir));
    }

    [Fact]
    public void Loose_ref_overrides_packed()
    {
        using var repo = BuildRepo();
        repo.Run("pack-refs", "--all");
        repo.WriteFile("a.txt", "c\n");
        var newSha = repo.CommitAll("third"); // main снова loose, packed устарел
        var store = RefStore.Load(repo.GitDir);
        Assert.True(store.TryResolve("refs/heads/main", out var main));
        Assert.Equal(newSha, main.Target.ToString());
    }

    [Fact]
    public void Head_is_symbolic_ref_to_main()
    {
        using var repo = BuildRepo();
        var store = RefStore.Load(repo.GitDir);
        Assert.Equal("refs/heads/main", store.HeadSymref);
        Assert.Equal(repo.Run("rev-parse", "HEAD").Trim(), store.HeadTarget?.ToString());
    }

    [Fact]
    public void Annotated_tag_has_peeled_target_in_packed_refs()
    {
        using var repo = BuildRepo();
        repo.Run("pack-refs", "--all");
        var store = RefStore.Load(repo.GitDir);
        Assert.True(store.TryResolve("refs/tags/v1.0", out var tag));
        Assert.Equal(repo.Run("rev-parse", "v1.0").Trim(), tag.Target.ToString());
        Assert.Equal(repo.Run("rev-parse", "v1.0^{commit}").Trim(), tag.Peeled?.ToString());
    }

    private static void AssertMatchesGit(RepoBuilder repo, RefStore store)
    {
        foreach (var (name, sha) in GitShowRef(repo))
        {
            Assert.True(store.TryResolve(name, out var entry), $"missing {name}");
            Assert.Equal(sha, entry.Target.ToString());
        }
    }
}
