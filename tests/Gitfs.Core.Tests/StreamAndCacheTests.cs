using Gitfs.Core;
using Gitfs.Core.Objects;
using Gitfs.Core.Tests.Fixtures;

namespace Gitfs.Core.Tests;

public class StreamAndCacheTests
{
    /// <summary>История упакована (с дельтами), свежий коммит — loose.</summary>
    private static RepoBuilder BuildMixedRepo()
    {
        var repo = new RepoBuilder();
        var text = "line\n";
        for (var i = 0; i < 20; i++)
        {
            text += $"line {i}\n";
            repo.WriteFile("data.txt", text);
            repo.CommitAll($"commit {i}");
        }
        repo.Repack();
        repo.WriteFile("fresh.txt", "loose object\n");
        repo.CommitAll("fresh");
        return repo;
    }

    private static byte[] Drain(Stream stream)
    {
        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        stream.Dispose();
        return ms.ToArray();
    }

    [Fact]
    public void OpenStream_matches_cat_file_for_loose_packed_and_delta_objects()
    {
        using var repo = BuildMixedRepo();
        using var reader = new ObjectReader(repo.GitDir);
        foreach (var (sha, gitType) in repo.AllObjects())
        {
            var expected = repo.RunBytes("cat-file", gitType, sha);
            var actual = Drain(reader.OpenStream(ObjectId.Parse(sha)));
            Assert.Equal(expected, actual);
        }
    }

    [Fact]
    public void OpenStream_throws_for_absent_object()
    {
        using var repo = BuildMixedRepo();
        using var reader = new ObjectReader(repo.GitDir);
        Assert.Throws<FileNotFoundException>(() =>
            reader.OpenStream(ObjectId.Parse("0123456789012345678901234567890123456789")));
    }

    [Fact]
    public void Second_read_of_delta_chain_hits_the_base_cache()
    {
        using var repo = BuildMixedRepo();
        var idx = Assert.Single(repo.IndexFiles());
        using var pack = PackFile.Open(Path.ChangeExtension(idx, ".pack"));
        var deltas = repo.VerifyPack(idx).Where(e => e.Depth > 0).ToList();
        Assert.NotEmpty(deltas); // фикстура обязана содержать дельты

        foreach (var e in deltas)
            Assert.True(pack.TryReadObject(ObjectId.Parse(e.Sha), 1 << 24, out _, out _));
        var hitsAfterFirstPass = pack.DeltaBaseCacheHits;

        foreach (var e in deltas)
            Assert.True(pack.TryReadObject(ObjectId.Parse(e.Sha), 1 << 24, out _, out _));
        // второй проход обязан попадать в кэш развёрнутых данных
        Assert.True(pack.DeltaBaseCacheHits > hitsAfterFirstPass,
            $"expected cache hits to grow, was {hitsAfterFirstPass} -> {pack.DeltaBaseCacheHits}");
    }

    [Fact]
    public void Repeated_headers_hit_the_size_cache_with_same_answers()
    {
        using var repo = BuildMixedRepo();
        var idx = Assert.Single(repo.IndexFiles());
        using var pack = PackFile.Open(Path.ChangeExtension(idx, ".pack"));
        var entries = repo.VerifyPack(idx);

        var first = new Dictionary<string, (GitObjectType, long)>();
        foreach (var e in entries)
        {
            Assert.True(pack.TryGetHeader(ObjectId.Parse(e.Sha), out var t, out var s));
            first[e.Sha] = (t, s);
        }
        var hitsBefore = pack.SizeCacheHits;
        foreach (var e in entries)
        {
            Assert.True(pack.TryGetHeader(ObjectId.Parse(e.Sha), out var t, out var s));
            Assert.Equal(first[e.Sha], (t, s)); // кэш отвечает тем же, чем считал обход
        }
        Assert.True(pack.SizeCacheHits >= hitsBefore + entries.Count);
    }

    [Fact]
    public void Parallel_reads_stay_byte_identical()
    {
        using var repo = BuildMixedRepo();
        using var reader = new ObjectReader(repo.GitDir);
        var objects = repo.AllObjects()
            .Select(o => (Id: ObjectId.Parse(o.Sha), Expected: repo.RunBytes("cat-file", o.Type, o.Sha)))
            .ToList();

        Parallel.For(0, 8, worker =>
        {
            foreach (var (id, expected) in objects)
            {
                Assert.Equal(expected, reader.ReadAll(id, 1 << 24));
                Assert.True(reader.TryGetHeader(id, out _, out var size));
                Assert.Equal(expected.Length, size);
            }
        });
    }
}
