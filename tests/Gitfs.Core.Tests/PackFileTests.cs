using Gitfs.Core;
using Gitfs.Core.Objects;
using Gitfs.Core.Tests.Fixtures;

namespace Gitfs.Core.Tests;

public class PackFileTests
{
    private static RepoBuilder BuildPackedRepo()
    {
        var repo = new RepoBuilder();
        var text = "line\n";
        for (var i = 0; i < 30; i++)
        {
            text += $"line {i}\n";
            repo.WriteFile("data.txt", text);
            repo.WriteFile($"extra/file{i % 3}.txt", $"content {i}\n");
            repo.CommitAll($"commit {i}");
        }
        repo.Repack();
        return repo;
    }

    [Fact]
    public void Every_packed_object_reads_byte_identical_to_git()
    {
        using var repo = BuildPackedRepo();
        var idx = Assert.Single(repo.IndexFiles());
        using var pack = PackFile.Open(Path.ChangeExtension(idx, ".pack"));
        foreach (var (sha, gitType) in repo.AllObjects())
        {
            var id = ObjectId.Parse(sha);
            Assert.True(pack.TryReadObject(id, 1 << 24, out var type, out var data), $"missing {sha}");
            Assert.Equal(gitType, TypeName(type));
            Assert.Equal(repo.RunBytes("cat-file", gitType, sha), data);
        }
    }

    [Fact]
    public void Header_reports_type_and_size_for_delta_objects_without_full_read()
    {
        using var repo = BuildPackedRepo();
        var idx = Assert.Single(repo.IndexFiles());
        using var pack = PackFile.Open(Path.ChangeExtension(idx, ".pack"));
        // Ловушка §6.4: у дельты в заголовке пакета — размер дельты, не результата
        foreach (var e in repo.VerifyPack(idx).Where(e => e.Depth > 0))
        {
            Assert.True(pack.TryGetHeader(ObjectId.Parse(e.Sha), out var type, out var size));
            Assert.Equal(e.Type, TypeName(type));
            Assert.Equal(long.Parse(repo.Run("cat-file", "-s", e.Sha).Trim()), size);
        }
    }

    [Fact]
    public void Absent_object_reports_false()
    {
        using var repo = BuildPackedRepo();
        using var pack = PackFile.Open(
            Path.ChangeExtension(repo.IndexFiles()[0], ".pack"));
        Assert.False(pack.TryReadObject(
            ObjectId.Parse("0123456789012345678901234567890123456789"), 1 << 24, out _, out _));
    }

    [Fact]
    public void MaxBytes_is_enforced_at_the_boundary()
    {
        using var repo = BuildPackedRepo();
        var idx = Assert.Single(repo.IndexFiles());
        using var pack = PackFile.Open(Path.ChangeExtension(idx, ".pack"));
        var e = repo.VerifyPack(idx).First(x => x.Depth == 0 && x.Size > 1);
        var id = ObjectId.Parse(e.Sha);
        Assert.Throws<InvalidDataException>(() => pack.TryReadObject(id, e.Size - 1, out _, out _));
        Assert.True(pack.TryReadObject(id, e.Size, out _, out _)); // граница включительно
    }

    private static string TypeName(GitObjectType t) => t switch
    {
        GitObjectType.Commit => "commit", GitObjectType.Tree => "tree",
        GitObjectType.Blob => "blob", GitObjectType.Tag => "tag", _ => "?",
    };
}
