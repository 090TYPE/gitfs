using Gitfs.Core;
using Gitfs.Core.Objects;
using Gitfs.Core.Tests.Fixtures;

namespace Gitfs.Core.Tests;

public class PackIndexTests
{
    private static RepoBuilder BuildPackedRepo()
    {
        var repo = new RepoBuilder();
        // 30 версий растущего файла — repack почти наверняка дельтифицирует
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
    public void Offsets_match_git_verify_pack_for_every_object()
    {
        using var repo = BuildPackedRepo();
        var idxPath = Assert.Single(repo.IndexFiles());
        var index = PackIndex.Load(idxPath);
        var expected = repo.VerifyPack(idxPath);

        Assert.Equal(expected.Count, index.Count);
        foreach (var e in expected)
        {
            Assert.True(index.TryFindOffset(ObjectId.Parse(e.Sha), out var offset), $"missing {e.Sha}");
            Assert.Equal(e.Offset, offset);
        }
    }

    [Fact]
    public void Fixture_actually_contains_deltas()
    {
        using var repo = BuildPackedRepo();
        var entries = repo.VerifyPack(repo.IndexFiles()[0]);
        Assert.Contains(entries, e => e.Depth > 0); // иначе дельта-путь не тестируется
    }

    [Fact]
    public void Absent_object_reports_false()
    {
        using var repo = BuildPackedRepo();
        var index = PackIndex.Load(repo.IndexFiles()[0]);
        Assert.False(index.TryFindOffset(
            ObjectId.Parse("0123456789012345678901234567890123456789"), out _));
    }

    [Fact]
    public void Synthetic_large_offset_uses_64bit_table()
    {
        // Ручной .idx: один объект со смещением 5 ГиБ через таблицу u64
        var sha = ObjectId.Parse("aa23456789012345678901234567890123456789");
        const long bigOffset = 5L * 1024 * 1024 * 1024;

        using var ms = new MemoryStream();
        void U32(uint v)
        {
            Span<byte> b = stackalloc byte[4];
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(b, v);
            ms.Write(b);
        }
        void U64(ulong v)
        {
            Span<byte> b = stackalloc byte[8];
            System.Buffers.Binary.BinaryPrimitives.WriteUInt64BigEndian(b, v);
            ms.Write(b);
        }

        ms.Write(new byte[] { 0xff, 0x74, 0x4f, 0x63 });  // \377tOc
        U32(2);                                            // версия
        for (var i = 0; i < 256; i++) U32(i >= 0xaa ? 1u : 0u); // fanout: 1 объект с first byte 0xaa
        var raw = new byte[20];
        sha.WriteRaw(raw);
        ms.Write(raw);                                     // отсортированные OID
        U32(0);                                            // CRC (не проверяем)
        U32(0x8000_0000);                                  // смещение: MSB ⇒ индекс 0 в u64-таблице
        U64((ulong)bigOffset);                             // большая таблица
        ms.Write(new byte[40]);                            // два sha1-трейлера (нули — не проверяем)

        var path = Path.Combine(Path.GetTempPath(), $"gitfs-idx-{Guid.NewGuid():N}.idx");
        File.WriteAllBytes(path, ms.ToArray());
        try
        {
            var index = PackIndex.Load(path);
            Assert.Equal(1, index.Count);
            Assert.True(index.TryFindOffset(sha, out var offset));
            Assert.Equal(bigOffset, offset);
        }
        finally { File.Delete(path); }
    }
}
