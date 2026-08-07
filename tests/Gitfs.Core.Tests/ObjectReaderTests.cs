using Gitfs.Core;
using Gitfs.Core.Objects;
using Gitfs.Core.Tests.Fixtures;

namespace Gitfs.Core.Tests;

public class ObjectReaderTests
{
    /// <summary>Смешанный репозиторий: история упакована, свежий коммит — loose.</summary>
    private static RepoBuilder BuildMixedRepo()
    {
        var repo = new RepoBuilder();
        var text = "";
        for (var i = 0; i < 10; i++)
        {
            text += $"line {i}\n";
            repo.WriteFile("data.txt", text);
            repo.CommitAll($"commit {i}");
        }
        repo.Repack();
        repo.WriteFile("fresh.txt", "not packed yet\n");
        repo.CommitAll("fresh commit"); // эти объекты — loose
        return repo;
    }

    [Fact]
    public void Reads_every_object_from_mixed_repo_byte_identical_to_git()
    {
        using var repo = BuildMixedRepo();
        using var reader = new ObjectReader(repo.GitDir);
        foreach (var (sha, gitType) in repo.AllObjects())
        {
            var id = ObjectId.Parse(sha);
            Assert.True(reader.TryGetHeader(id, out var type, out var size), $"missing header {sha}");
            Assert.Equal(gitType, TypeName(type));
            Assert.Equal(long.Parse(repo.Run("cat-file", "-s", sha).Trim()), size);
            Assert.Equal(repo.RunBytes("cat-file", gitType, sha), reader.ReadAll(id, 1 << 24));
        }
    }

    [Fact]
    public void Contains_is_true_for_packed_and_loose_false_for_absent()
    {
        using var repo = BuildMixedRepo();
        using var reader = new ObjectReader(repo.GitDir);
        foreach (var (sha, _) in repo.AllObjects())
            Assert.True(reader.Contains(ObjectId.Parse(sha)));
        Assert.False(reader.Contains(
            ObjectId.Parse("0123456789012345678901234567890123456789")));
    }

    private static string TypeName(GitObjectType t) => t switch
    {
        GitObjectType.Commit => "commit", GitObjectType.Tree => "tree",
        GitObjectType.Blob => "blob", GitObjectType.Tag => "tag", _ => "?",
    };
}
