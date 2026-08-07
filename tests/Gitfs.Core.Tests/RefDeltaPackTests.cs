using Gitfs.Core;
using Gitfs.Core.Objects;
using Gitfs.Core.Tests.Fixtures;

namespace Gitfs.Core.Tests;

/// <summary>Критическая находка ревью: git repack по умолчанию пишет только
/// OFS_DELTA (repack.useDeltaBaseOffset=true), и ветка REF_DELTA была мертва
/// в тестах — при том что реальные паки из fetch/push её содержат.
/// Форсируем ref-дельты выключением конфига.</summary>
public class RefDeltaPackTests
{
    private static RepoBuilder BuildRefDeltaRepo()
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
        // -c обязан стоять до подкоманды
        repo.Run("-c", "repack.usedeltabaseoffset=false", "repack", "-a", "-d");
        return repo;
    }

    [Fact]
    public void Fixture_actually_contains_ref_deltas()
    {
        using var repo = BuildRefDeltaRepo();
        var idx = Assert.Single(repo.IndexFiles());
        var pack = File.ReadAllBytes(Path.ChangeExtension(idx, ".pack"));
        // verify-pack не различает ofs/ref — смотрим тип в первом байте заголовка
        var hasRefDelta = repo.VerifyPack(idx)
            .Any(e => ((pack[(int)e.Offset] >> 4) & 0x7) == 7);
        Assert.True(hasRefDelta, "repack with usedeltabaseoffset=false must emit REF_DELTA");
    }

    [Fact]
    public void Every_object_reads_byte_identical_through_ref_deltas()
    {
        using var repo = BuildRefDeltaRepo();
        var idx = Assert.Single(repo.IndexFiles());
        using var pack = PackFile.Open(Path.ChangeExtension(idx, ".pack"));
        foreach (var (sha, gitType) in repo.AllObjects())
        {
            var id = ObjectId.Parse(sha);
            Assert.True(pack.TryReadObject(id, 1 << 24, out _, out var data), $"missing {sha}");
            Assert.Equal(repo.RunBytes("cat-file", gitType, sha), data);
        }
    }

    [Fact]
    public void Header_walks_ref_delta_chain_for_type_and_size()
    {
        using var repo = BuildRefDeltaRepo();
        var idx = Assert.Single(repo.IndexFiles());
        using var pack = PackFile.Open(Path.ChangeExtension(idx, ".pack"));
        foreach (var e in repo.VerifyPack(idx).Where(e => e.Depth > 0))
        {
            Assert.True(pack.TryGetHeader(ObjectId.Parse(e.Sha), out _, out var size));
            Assert.Equal(long.Parse(repo.Run("cat-file", "-s", e.Sha).Trim()), size);
        }
    }
}
