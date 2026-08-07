using Gitfs.Core;
using Gitfs.Core.Objects;
using Gitfs.Core.Tests.Fixtures;

namespace Gitfs.Core.Tests;

public class LooseObjectReaderTests
{
    private static RepoBuilder BuildRepo()
    {
        var repo = new RepoBuilder();
        repo.WriteFile("README.md", "gitfs test fixture\n");
        repo.WriteFile("src/Program.cs", "static class Program { static int Main() => 0; }\n");
        repo.CommitAll("first");
        repo.WriteFile("src/Program.cs", "static class Program { static int Main() => 1; }\n");
        repo.CommitAll("second");
        repo.Tag("v1.0", annotated: true, message: "release");
        return repo;
    }

    [Fact]
    public void Header_matches_git_cat_file_for_every_object()
    {
        using var repo = BuildRepo();
        var reader = new LooseObjectReader(repo.GitDir);
        foreach (var (sha, gitType) in repo.AllObjects())
        {
            var id = ObjectId.Parse(sha);
            Assert.True(reader.TryGetHeader(id, out var type, out var size), $"missing {sha}");
            Assert.Equal(gitType, TypeName(type));
            Assert.Equal(long.Parse(repo.Run("cat-file", "-s", sha).Trim()), size);
        }
    }

    [Fact]
    public void Content_matches_git_cat_file_for_every_object()
    {
        using var repo = BuildRepo();
        var reader = new LooseObjectReader(repo.GitDir);
        foreach (var (sha, gitType) in repo.AllObjects())
        {
            var expected = repo.RunBytes("cat-file", gitType, sha);
            var actual = reader.ReadAll(ObjectId.Parse(sha), maxBytes: 1 << 20);
            Assert.Equal(expected, actual);
        }
    }

    [Fact]
    public void Annotated_tag_object_is_readable()
    {
        using var repo = BuildRepo();
        var tagSha = repo.Run("rev-parse", "v1.0").Trim();  // sha объекта тега
        var reader = new LooseObjectReader(repo.GitDir);
        Assert.True(reader.TryGetHeader(ObjectId.Parse(tagSha), out var type, out _));
        Assert.Equal(GitObjectType.Tag, type);
    }

    [Fact]
    public void Missing_object_reports_false_not_throw()
    {
        using var repo = BuildRepo();
        var reader = new LooseObjectReader(repo.GitDir);
        var absent = ObjectId.Parse("0123456789012345678901234567890123456789");
        Assert.False(reader.TryGetHeader(absent, out _, out _));
        Assert.Null(reader.TryOpenStream(absent, out _, out _));
    }

    [Fact]
    public void ReadAll_enforces_max_bytes()
    {
        using var repo = BuildRepo();
        var reader = new LooseObjectReader(repo.GitDir);
        var (sha, _) = repo.AllObjects().First(o => o.Type == "blob");
        Assert.Throws<InvalidDataException>(() => reader.ReadAll(ObjectId.Parse(sha), maxBytes: 1));
    }

    private static string TypeName(GitObjectType t) => t switch
    {
        GitObjectType.Commit => "commit",
        GitObjectType.Tree => "tree",
        GitObjectType.Blob => "blob",
        GitObjectType.Tag => "tag",
        _ => "?",
    };
}
