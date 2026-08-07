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

    [Fact]
    public void Large_offset_index_beyond_table_throws_not_reads_trailer()
    {
        // MSB у смещения взведён, а u64-таблица пуста: без стражи это чтение
        // sha1-трейлера как смещения — молча (находка ревью)
        var sha = ObjectId.Parse("aa23456789012345678901234567890123456789");
        using var ms = new MemoryStream();
        void U32(uint v)
        {
            Span<byte> b = stackalloc byte[4];
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(b, v);
            ms.Write(b);
        }
        ms.Write(new byte[] { 0xff, 0x74, 0x4f, 0x63 });
        U32(2);
        for (var i = 0; i < 256; i++) U32(i >= 0xaa ? 1u : 0u);
        var raw = new byte[20];
        sha.WriteRaw(raw);
        ms.Write(raw);
        U32(0);                 // CRC
        U32(0x8000_0000);       // ссылка в пустую u64-таблицу
        ms.Write(new byte[40]); // сразу трейлер — большой таблицы нет

        var path = Path.Combine(Path.GetTempPath(), $"gitfs-idx-{Guid.NewGuid():N}.idx");
        File.WriteAllBytes(path, ms.ToArray());
        try
        {
            var index = PackIndex.Load(path);
            Assert.Throws<InvalidDataException>(() => index.TryFindOffset(sha, out _));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Corrupt_idx_fails_with_clear_error()
    {
        using var repo = BuildPackedRepo();
        var good = File.ReadAllBytes(repo.IndexFiles()[0]);
        var path = Path.Combine(Path.GetTempPath(), $"gitfs-idx-{Guid.NewGuid():N}.idx");
        try
        {
            File.WriteAllBytes(path, good.AsSpan(0, 100).ToArray()); // обрезан
            Assert.Throws<InvalidDataException>(() => PackIndex.Load(path));

            var badMagic = (byte[])good.Clone();
            badMagic[0] ^= 0xff;
            File.WriteAllBytes(path, badMagic);
            Assert.Throws<InvalidDataException>(() => PackIndex.Load(path));

            var badVersion = (byte[])good.Clone();
            badVersion[7] = 3;
            File.WriteAllBytes(path, badVersion);
            Assert.Throws<InvalidDataException>(() => PackIndex.Load(path));

            // обрезка ниже заявленного Count — InvalidDataException, не ArgumentOutOfRange
            var truncated = good.AsSpan(0, good.Length - 60).ToArray();
            File.WriteAllBytes(path, truncated);
            Assert.Throws<InvalidDataException>(() => PackIndex.Load(path));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void ObjectIds_enumerates_exactly_the_pack_contents()
    {
        using var repo = BuildPackedRepo();
        var idx = repo.IndexFiles()[0];
        var index = PackIndex.Load(idx);
        var expected = repo.VerifyPack(idx).Select(e => e.Sha).ToHashSet();
        var actual = index.ObjectIds.Select(o => o.ToString()).ToHashSet();
        Assert.Equal(expected, actual);
    }
}
