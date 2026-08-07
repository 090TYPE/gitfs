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

    [Fact]
    public void Falls_through_to_second_pack_when_first_misses()
    {
        using var repo = new RepoBuilder();
        repo.WriteFile("a.txt", "batch A\n");
        repo.CommitAll("batch A");
        repo.Run("repack", "-d");            // пак №1: только объекты партии A
        repo.WriteFile("b.txt", "batch B\n");
        repo.CommitAll("batch B");
        repo.Run("repack", "-d");            // пак №2: только объекты партии B
        Assert.Equal(2, repo.IndexFiles().Length);

        using var reader = new ObjectReader(repo.GitDir);
        // объекты партий живут в разных паках: часть запросов обязана
        // промахнуться в первом паке и провалиться во второй
        foreach (var (sha, gitType) in repo.AllObjects())
        {
            var id = ObjectId.Parse(sha);
            Assert.True(reader.TryGetHeader(id, out _, out _), $"missing {sha}");
            Assert.Equal(repo.RunBytes("cat-file", gitType, sha), reader.ReadAll(id, 1 << 24));
        }
    }

    [Fact]
    public void Stray_pack_without_idx_is_skipped_not_fatal()
    {
        using var repo = BuildMixedRepo();
        // окно во время git repack/fetch: .pack уже есть, .idx ещё нет
        File.WriteAllBytes(
            Path.Combine(repo.GitDir, "objects", "pack", "pack-stray.pack"), new byte[64]);
        using var reader = new ObjectReader(repo.GitDir);
        Assert.True(reader.Contains(ObjectId.Parse(repo.Run("rev-parse", "HEAD").Trim())));
    }

    [Fact]
    public void Absent_id_fails_header_and_readall_cleanly()
    {
        using var repo = BuildMixedRepo();
        using var reader = new ObjectReader(repo.GitDir);
        var absent = ObjectId.Parse("0123456789012345678901234567890123456789");
        Assert.False(reader.TryGetHeader(absent, out _, out _));
        Assert.Throws<FileNotFoundException>(() => reader.ReadAll(absent, 1 << 24));
    }

    private static string TypeName(GitObjectType t) => t switch
    {
        GitObjectType.Commit => "commit", GitObjectType.Tree => "tree",
        GitObjectType.Blob => "blob", GitObjectType.Tag => "tag", _ => "?",
    };
}
