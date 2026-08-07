# gitfs M1b: PackIndex + PackReader + DeltaCodec — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Чтение упакованных объектов: `.idx` v2 (включая 8-байтные смещения), `.pack` через memory-mapped files, обе разновидности дельт с итеративным разворотом, составной `ObjectReader` (loose → pack) — всё доказано дифференциальными тестами против `git cat-file` и `git verify-pack -v`.

**Architecture:** `PackIndex` парсит `.idx` в память (маленький относительно пакета) и ищет бинарным поиском в fanout-бакете. `PackFile` держит `MemoryMappedFile`; на операцию открывается `MemoryMappedViewStream`, заголовок объекта читается побайтово, тело инфлируется `ZLibStream` поверх того же потока. Цепочка дельт собирается в стек итеративно и применяется от базы. `ObjectReader` объединяет loose и все пакеты каталога `objects/pack`.

**Tech Stack:** без изменений — .NET 8, xunit, ноль NuGet в `Gitfs.Core`.

**Форматные факты, на которых всё строится** (проверяются тестами, не верой):
- `.idx` v2: магия `\377tOc`, версия 2, fanout 256×u32BE (кумулятивно), N×20 байт отсортированных OID, N×u32 CRC, N×u32 смещений (старший бит ⇒ индекс в таблице u64), таблица u64, два sha1-трейлера.
- Заголовок объекта в `.pack`: байт 0 — бит 7 продолжение, биты 6–4 тип (1 commit, 2 tree, 3 blob, 4 tag, 6 ofs-delta, 7 ref-delta), биты 3–0 младшие биты размера; далее по 7 бит на байт (little-endian группы).
- OFS_DELTA — «прибавляющий» varint: `n = (n+1)<<7 | bits` на каждом продолжении; смещение базы = смещение объекта − n.
- Тело дельты: два обычных 7-битных varint (размер источника, размер результата), затем команды: copy (бит 7 = 1, селекторы присутствующих байт offset/size; size 0 ⇒ 0x10000) и insert (бит 7 = 0, длина 1–127 литеральных байт).
- Размер в заголовке пакета у дельты — размер **самой дельты**; итоговый размер — второй varint её тела (ловушка из спеки §6.4).

---

### Task 1: DeltaCodec

**Files:**
- Create: `src/Gitfs.Core/Objects/DeltaCodec.cs`
- Test: `tests/Gitfs.Core.Tests/DeltaCodecTests.cs`

- [x] **Step 1: Написать падающие тесты (ручные вектора формата)**

```csharp
using Gitfs.Core.Objects;

namespace Gitfs.Core.Tests;

public class DeltaCodecTests
{
    // Вектор: base = "hello world", delta: insert "HI " + copy(offset 6, size 5) => "HI world"
    private static readonly byte[] BaseData = "hello world"u8.ToArray();

    private static byte[] Delta(params byte[][] parts)
    {
        var all = new List<byte>();
        foreach (var p in parts) all.AddRange(p);
        return all.ToArray();
    }

    [Fact]
    public void Insert_then_copy()
    {
        var delta = Delta(
            new byte[] { 11 },                    // source size = 11
            new byte[] { 8 },                     // target size = 8
            new byte[] { 3, (byte)'H', (byte)'I', (byte)' ' },  // insert 3
            new byte[] { 0b1001_0001, 6, 5 });    // copy: offset1=6, size1=5
        var result = DeltaCodec.Apply(BaseData, delta);
        Assert.Equal("HI world"u8.ToArray(), result);
    }

    [Fact]
    public void Copy_with_zero_size_means_65536()
    {
        var baseData = new byte[70000];
        new Random(42).NextBytes(baseData);
        var delta = Delta(
            EncodeVarint(70000),
            EncodeVarint(65536),
            new byte[] { 0b1000_0000 });          // copy: без offset и size ⇒ 0, size 0 ⇒ 0x10000
        var result = DeltaCodec.Apply(baseData, delta);
        Assert.Equal(baseData.AsSpan(0, 65536).ToArray(), result);
    }

    [Fact]
    public void Multibyte_varint_sizes_roundtrip()
    {
        var (src, tgt, len) = DeltaCodec.ReadSizes(Delta(EncodeVarint(300), EncodeVarint(70000)));
        Assert.Equal(300, src);
        Assert.Equal(70000, tgt);
        Assert.Equal(2 + 3, len); // 300 → 2 байта, 70000 → 3 байта
    }

    [Fact]
    public void Truncated_delta_throws()
    {
        var delta = Delta(new byte[] { 11 }, new byte[] { 8 }, new byte[] { 5, (byte)'a' }); // insert 5, но 1 байт
        Assert.Throws<InvalidDataException>(() => DeltaCodec.Apply(BaseData, delta));
    }

    [Fact]
    public void Target_size_mismatch_throws()
    {
        var delta = Delta(new byte[] { 11 }, new byte[] { 9 },
            new byte[] { 3, (byte)'H', (byte)'I', (byte)' ' },
            new byte[] { 0b1001_0001, 6, 5 });    // фактически 8, заявлено 9
        Assert.Throws<InvalidDataException>(() => DeltaCodec.Apply(BaseData, delta));
    }

    private static byte[] EncodeVarint(long value)
    {
        var bytes = new List<byte>();
        do
        {
            var b = (byte)(value & 0x7f);
            value >>= 7;
            if (value != 0) b |= 0x80;
            bytes.Add(b);
        } while (value != 0);
        return bytes.ToArray();
    }
}
```

- [x] **Step 2: Красная фаза** — `dotnet test --filter DeltaCodecTests` → ошибки компиляции.

- [x] **Step 3: Реализация**

```csharp
namespace Gitfs.Core.Objects;

/// <summary>Разворот git-дельты (тело OBJ_OFS_DELTA / OBJ_REF_DELTA):
/// два 7-битных varint размеров, затем команды copy/insert.
/// Спека §6.4: размер результата — только здесь, не в заголовке пакета.</summary>
public static class DeltaCodec
{
    public static (long SourceSize, long TargetSize, int HeaderLength) ReadSizes(ReadOnlySpan<byte> delta)
    {
        var pos = 0;
        var source = ReadVarint(delta, ref pos);
        var target = ReadVarint(delta, ref pos);
        return (source, target, pos);
    }

    public static byte[] Apply(ReadOnlySpan<byte> baseData, ReadOnlySpan<byte> delta)
    {
        var (sourceSize, targetSize, pos) = ReadSizes(delta);
        if (sourceSize != baseData.Length)
            throw new InvalidDataException($"delta expects base of {sourceSize} bytes, got {baseData.Length}");

        var result = new byte[targetSize];
        var written = 0;
        while (pos < delta.Length)
        {
            var cmd = delta[pos++];
            if ((cmd & 0x80) != 0)
            {
                long offset = 0, size = 0;
                if ((cmd & 0x01) != 0) offset |= (long)Next(delta, ref pos);
                if ((cmd & 0x02) != 0) offset |= (long)Next(delta, ref pos) << 8;
                if ((cmd & 0x04) != 0) offset |= (long)Next(delta, ref pos) << 16;
                if ((cmd & 0x08) != 0) offset |= (long)Next(delta, ref pos) << 24;
                if ((cmd & 0x10) != 0) size |= (long)Next(delta, ref pos);
                if ((cmd & 0x20) != 0) size |= (long)Next(delta, ref pos) << 8;
                if ((cmd & 0x40) != 0) size |= (long)Next(delta, ref pos) << 16;
                if (size == 0) size = 0x10000;
                if (offset + size > baseData.Length || written + size > result.Length)
                    throw new InvalidDataException("delta copy out of range");
                baseData.Slice((int)offset, (int)size).CopyTo(result.AsSpan(written));
                written += (int)size;
            }
            else
            {
                if (cmd == 0) throw new InvalidDataException("delta: reserved zero command");
                if (pos + cmd > delta.Length || written + cmd > result.Length)
                    throw new InvalidDataException("delta insert out of range");
                delta.Slice(pos, cmd).CopyTo(result.AsSpan(written));
                pos += cmd;
                written += cmd;
            }
        }
        if (written != result.Length)
            throw new InvalidDataException($"delta produced {written} bytes, target declared {result.Length}");
        return result;
    }

    private static byte Next(ReadOnlySpan<byte> delta, ref int pos) =>
        pos < delta.Length ? delta[pos++] : throw new InvalidDataException("truncated delta");

    private static long ReadVarint(ReadOnlySpan<byte> data, ref int pos)
    {
        long value = 0;
        var shift = 0;
        while (true)
        {
            var b = Next(data, ref pos);
            value |= (long)(b & 0x7f) << shift;
            if ((b & 0x80) == 0) return value;
            shift += 7;
            if (shift > 56) throw new InvalidDataException("varint too long");
        }
    }
}
```

- [x] **Step 4: Зелёная фаза** — 5 тестов PASS.
- [x] **Step 5: Commit** — `feat(core): DeltaCodec — copy/insert instructions, format-vector tested`

---

### Task 2: Фикстура с пакетами + PackIndex

**Files:**
- Modify: `tests/Gitfs.Core.Tests/Fixtures/RepoBuilder.cs` (добавить Repack и VerifyPack)
- Create: `src/Gitfs.Core/Objects/PackIndex.cs`
- Test: `tests/Gitfs.Core.Tests/PackIndexTests.cs`

- [x] **Step 1: Расширить RepoBuilder**

```csharp
    /// <summary>Упаковывает все объекты в один pack (loose исчезают).</summary>
    public void Repack() => Run("repack", "-a", "-d");

    public string[] IndexFiles() =>
        Directory.GetFiles(Path.Combine(GitDir, "objects", "pack"), "*.idx");

    public sealed record PackEntry(string Sha, string Type, long Size, long Offset, int Depth);

    /// <summary>Разбор `git verify-pack -v`: эталон смещений и глубин дельт.</summary>
    public IReadOnlyList<PackEntry> VerifyPack(string idxPath)
    {
        var entries = new List<PackEntry>();
        foreach (var line in Run("verify-pack", "-v", idxPath).Split('\n'))
        {
            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 5 || parts[0].Length != 40) continue;
            if (!parts[1].StartsWith("commit") && !parts[1].StartsWith("tree")
                && !parts[1].StartsWith("blob") && !parts[1].StartsWith("tag")) continue;
            entries.Add(new PackEntry(parts[0], parts[1], long.Parse(parts[2]),
                long.Parse(parts[4]), parts.Length >= 7 ? int.Parse(parts[5]) : 0));
        }
        return entries;
    }
```

- [x] **Step 2: Написать падающие тесты PackIndex**

```csharp
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
        void U32(uint v) { Span<byte> b = stackalloc byte[4];
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(b, v); ms.Write(b); }
        void U64(ulong v) { Span<byte> b = stackalloc byte[8];
            System.Buffers.Binary.BinaryPrimitives.WriteUInt64BigEndian(b, v); ms.Write(b); }

        ms.Write(new byte[] { 0xff, 0x74, 0x4f, 0x63 });  // \377tOc
        U32(2);                                            // версия
        for (var i = 0; i < 256; i++) U32(i >= 0xaa ? 1u : 0u); // fanout: 1 объект с first byte 0xaa
        Span<byte> raw = stackalloc byte[20];
        sha.WriteRaw(raw); ms.Write(raw);                  // отсортированные OID
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
```

- [x] **Step 3: Красная фаза** — ошибки компиляции.

- [x] **Step 4: Реализация PackIndex**

```csharp
using System.Buffers.Binary;

namespace Gitfs.Core.Objects;

/// <summary>Индекс пакета .idx версии 2. Файл читается в память целиком
/// (индекс мал относительно пакета); поиск — бинарный в границах
/// fanout-бакета первого байта OID.</summary>
public sealed class PackIndex
{
    private const uint Magic = 0xff744f63; // "\377tOc"

    private readonly byte[] _data;
    private readonly int _oidTableStart;
    private readonly int _offsetTableStart;
    private readonly int _largeTableStart;

    public int Count { get; }

    private PackIndex(byte[] data)
    {
        _data = data;
        if (data.Length < 8 + 256 * 4 + 40)
            throw new InvalidDataException("idx too short");
        if (BinaryPrimitives.ReadUInt32BigEndian(data) != Magic)
            throw new InvalidDataException("not a v2 pack index (bad magic)");
        if (BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(4)) != 2)
            throw new InvalidDataException("unsupported pack index version");

        Count = (int)BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(8 + 255 * 4));
        _oidTableStart = 8 + 256 * 4;
        var crcTableStart = _oidTableStart + Count * 20;
        _offsetTableStart = crcTableStart + Count * 4;
        _largeTableStart = _offsetTableStart + Count * 4;
    }

    public static PackIndex Load(string idxPath) => new(File.ReadAllBytes(idxPath));

    public bool TryFindOffset(in ObjectId id, out long offset)
    {
        offset = 0;
        var slot = FindSlot(id);
        if (slot < 0) return false;
        var raw = BinaryPrimitives.ReadUInt32BigEndian(_data.AsSpan(_offsetTableStart + slot * 4));
        if ((raw & 0x8000_0000) == 0)
        {
            offset = raw;
            return true;
        }
        var largeIndex = (int)(raw & 0x7fff_ffff);
        offset = (long)BinaryPrimitives.ReadUInt64BigEndian(
            _data.AsSpan(_largeTableStart + largeIndex * 8));
        return true;
    }

    public IEnumerable<ObjectId> ObjectIds
    {
        get
        {
            for (var i = 0; i < Count; i++)
                yield return new ObjectId(_data.AsSpan(_oidTableStart + i * 20, 20));
        }
    }

    private int FindSlot(in ObjectId id)
    {
        var bucket = id.FirstByte;
        var lo = bucket == 0 ? 0 : (int)BinaryPrimitives.ReadUInt32BigEndian(_data.AsSpan(8 + (bucket - 1) * 4));
        var hi = (int)BinaryPrimitives.ReadUInt32BigEndian(_data.AsSpan(8 + bucket * 4));
        while (lo < hi)
        {
            var mid = (lo + hi) / 2;
            var midId = new ObjectId(_data.AsSpan(_oidTableStart + mid * 20, 20));
            var cmp = id.CompareTo(midId);
            if (cmp == 0) return mid;
            if (cmp < 0) hi = mid; else lo = mid + 1;
        }
        return -1;
    }
}
```

- [x] **Step 5: Зелёная фаза** — 4 теста PASS (включая синтетический large-offset).
- [x] **Step 6: Commit** — `feat(core): PackIndex v2 — fanout binary search, 64-bit offsets`

---

### Task 3: PackFile — чтение объектов и разворот цепочек

**Files:**
- Create: `src/Gitfs.Core/Objects/PackFile.cs`
- Test: `tests/Gitfs.Core.Tests/PackFileTests.cs`

- [x] **Step 1: Написать падающие дифференциальные тесты**

```csharp
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

    private static string TypeName(GitObjectType t) => t switch
    {
        GitObjectType.Commit => "commit", GitObjectType.Tree => "tree",
        GitObjectType.Blob => "blob", GitObjectType.Tag => "tag", _ => "?",
    };
}
```

- [x] **Step 2: Красная фаза.**

- [x] **Step 3: Реализация PackFile**

```csharp
using System.IO.Compression;
using System.IO.MemoryMappedFiles;

namespace Gitfs.Core.Objects;

/// <summary>Пакет .pack поверх memory-mapped file: проекция одна,読 многими
/// потоками безопасно; на операцию открывается свой view-stream.
/// Цепочки дельт разворачиваются итеративно (спека §6.4).</summary>
public sealed class PackFile : IDisposable
{
    private const int MaxDeltaChain = 1000;

    private readonly MemoryMappedFile _mmap;
    private readonly long _length;
    private readonly PackIndex _index;

    private PackFile(MemoryMappedFile mmap, long length, PackIndex index)
    {
        _mmap = mmap;
        _length = length;
        _index = index;
    }

    public static PackFile Open(string packPath)
    {
        var index = PackIndex.Load(Path.ChangeExtension(packPath, ".idx"));
        var length = new FileInfo(packPath).Length;
        var mmap = MemoryMappedFile.CreateFromFile(packPath, FileMode.Open, null, 0,
            MemoryMappedFileAccess.Read);
        return new PackFile(mmap, length, index);
    }

    public bool Contains(in ObjectId id) => _index.TryFindOffset(id, out _);

    public bool TryGetHeader(in ObjectId id, out GitObjectType type, out long size)
    {
        type = default; size = 0;
        if (!_index.TryFindOffset(id, out var offset)) return false;

        // Тип — у базы цепочки; размер результата дельты — второй varint её тела.
        var chain = 0;
        while (true)
        {
            using var view = OpenView(offset);
            var (raw, headerSize, dataStart) = ReadObjectHeader(view, offset);
            if (raw is RawType.OfsDelta or RawType.RefDelta)
            {
                if (++chain > MaxDeltaChain) throw new InvalidDataException("delta chain too long");
                var baseRef = ReadDeltaBase(view, raw, offset, dataStart, out var deltaDataStart);
                if (chain == 1)
                {
                    // размеры лежат в начале инфлированной дельты
                    using var sizeView = OpenView(deltaDataStart);
                    using var z = new ZLibStream(sizeView, CompressionMode.Decompress);
                    Span<byte> prefix = stackalloc byte[32];
                    var n = ReadUpTo(z, prefix);
                    var (_, targetSize, _) = DeltaCodec.ReadSizes(prefix[..n]);
                    size = targetSize;
                }
                offset = ResolveBaseOffset(baseRef, offset);
                continue;
            }
            type = (GitObjectType)raw;
            if (chain == 0) size = headerSize;
            return true;
        }
    }

    public bool TryReadObject(in ObjectId id, long maxBytes, out GitObjectType type, out byte[] data)
    {
        type = default; data = [];
        if (!_index.TryFindOffset(id, out var offset)) return false;

        // Вниз по цепочке: собираем дельты в стек, находим базу; затем вверх — применяем.
        var deltas = new Stack<byte[]>();
        while (true)
        {
            using var view = OpenView(offset);
            var (raw, size, dataStart) = ReadObjectHeader(view, offset);
            if (raw is RawType.OfsDelta or RawType.RefDelta)
            {
                if (deltas.Count >= MaxDeltaChain) throw new InvalidDataException("delta chain too long");
                var baseRef = ReadDeltaBase(view, raw, offset, dataStart, out var deltaDataStart);
                deltas.Push(Inflate(deltaDataStart, size, maxBytes));
                offset = ResolveBaseOffset(baseRef, offset);
                continue;
            }
            type = (GitObjectType)raw;
            data = Inflate(dataStart, size, maxBytes);
            break;
        }
        while (deltas.Count > 0)
        {
            data = DeltaCodec.Apply(data, deltas.Pop());
            if (data.Length > maxBytes)
                throw new InvalidDataException($"object {id} exceeds {maxBytes} bytes");
        }
        return true;
    }

    // --- низкоуровневое ---

    private enum RawType { Commit = 1, Tree = 2, Blob = 3, Tag = 4, OfsDelta = 6, RefDelta = 7 }

    private readonly record struct BaseRef(long OfsNegative, ObjectId? RefId);

    private MemoryMappedViewStream OpenView(long offset) =>
        _mmap.CreateViewStream(offset, _length - offset, MemoryMappedFileAccess.Read);

    /// <summary>Читает заголовок объекта; возвращает тип, размер (для дельты —
    /// размер дельты) и абсолютное смещение начала данных.</summary>
    private static (RawType Type, long Size, long DataStart) ReadObjectHeader(Stream view, long offset)
    {
        var pos = 0L;
        int b = ReadByteOrThrow(view); pos++;
        var type = (RawType)((b >> 4) & 0x7);
        long size = b & 0xf;
        var shift = 4;
        while ((b & 0x80) != 0)
        {
            b = ReadByteOrThrow(view); pos++;
            size |= (long)(b & 0x7f) << shift;
            shift += 7;
        }
        return (type, size, offset + pos);
    }

    /// <summary>Для дельты — читает ссылку на базу сразу после заголовка;
    /// возвращает её и абсолютное смещение начала zlib-данных дельты.</summary>
    private static BaseRef ReadDeltaBase(Stream view, RawType type, long objOffset, long dataStart,
        out long deltaDataStart)
    {
        // view сейчас спозиционирован сразу за заголовком (после ReadObjectHeader)
        var consumed = 0L;
        if (type == RawType.OfsDelta)
        {
            int b = ReadByteOrThrow(view); consumed++;
            long n = b & 0x7f;
            while ((b & 0x80) != 0)
            {
                b = ReadByteOrThrow(view); consumed++;
                n = ((n + 1) << 7) | (uint)(b & 0x7f);
            }
            deltaDataStart = dataStart + consumed;
            return new BaseRef(n, null);
        }
        Span<byte> raw = stackalloc byte[20];
        for (var i = 0; i < 20; i++) raw[i] = (byte)ReadByteOrThrow(view);
        deltaDataStart = dataStart + 20;
        return new BaseRef(0, new ObjectId(raw));
    }

    private long ResolveBaseOffset(in BaseRef baseRef, long objOffset)
    {
        if (baseRef.RefId is { } refId)
            return _index.TryFindOffset(refId, out var byId)
                ? byId
                : throw new InvalidDataException($"ref-delta base {refId} not in this pack");
        var target = objOffset - baseRef.OfsNegative;
        return target >= 12 && target < objOffset
            ? target
            : throw new InvalidDataException("ofs-delta base offset out of range");
    }

    private byte[] Inflate(long dataStart, long declaredSize, long maxBytes)
    {
        if (declaredSize > maxBytes)
            throw new InvalidDataException($"packed entry of {declaredSize} bytes exceeds {maxBytes}");
        using var view = OpenView(dataStart);
        using var z = new ZLibStream(view, CompressionMode.Decompress);
        var data = new byte[declaredSize];
        var read = 0;
        while (read < data.Length)
        {
            var n = z.Read(data, read, data.Length - read);
            if (n == 0) throw new InvalidDataException("truncated packed object");
            read += n;
        }
        return data;
    }

    private static int ReadByteOrThrow(Stream s)
    {
        var b = s.ReadByte();
        return b >= 0 ? b : throw new InvalidDataException("unexpected end of pack");
    }

    private static int ReadUpTo(Stream s, Span<byte> buffer)
    {
        var total = 0;
        while (total < buffer.Length)
        {
            var n = s.Read(buffer[total..]);
            if (n == 0) break;
            total += n;
        }
        return total;
    }

    public void Dispose() => _mmap.Dispose();
}
```

- [x] **Step 4: Зелёная фаза** — 3 теста PASS.
- [x] **Step 5: Commit** — `feat(core): PackFile — mmap reads, iterative delta chains`

---

### Task 4: Составной ObjectReader (loose → pack)

**Files:**
- Create: `src/Gitfs.Core/Objects/ObjectReader.cs`
- Test: `tests/Gitfs.Core.Tests/ObjectReaderTests.cs`

- [x] **Step 1: Написать падающие тесты**

```csharp
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
```

- [x] **Step 2: Красная фаза.**

- [x] **Step 3: Реализация**

```csharp
namespace Gitfs.Core.Objects;

/// <summary>Составной доступ к объектам: сначала loose (новее), затем пакеты.
/// Набор пакетов фиксируется при создании — это будущая часть RepoSnapshot;
/// переоткрытие после git gc (спека §7) появится вместе со снапшотами.</summary>
public sealed class ObjectReader : IDisposable
{
    private readonly LooseObjectReader _loose;
    private readonly PackFile[] _packs;

    public ObjectReader(string gitDir)
    {
        _loose = new LooseObjectReader(gitDir);
        var packDir = Path.Combine(gitDir, "objects", "pack");
        _packs = Directory.Exists(packDir)
            ? Directory.GetFiles(packDir, "*.pack").Select(PackFile.Open).ToArray()
            : [];
    }

    public bool Contains(in ObjectId id)
    {
        if (_loose.Contains(id)) return true;
        foreach (var pack in _packs)
            if (pack.Contains(id)) return true;
        return false;
    }

    public bool TryGetHeader(in ObjectId id, out GitObjectType type, out long size)
    {
        if (_loose.TryGetHeader(id, out type, out size)) return true;
        foreach (var pack in _packs)
            if (pack.TryGetHeader(id, out type, out size)) return true;
        return false;
    }

    public byte[] ReadAll(in ObjectId id, long maxBytes)
    {
        if (_loose.Contains(id)) return _loose.ReadAll(id, maxBytes);
        foreach (var pack in _packs)
            if (pack.TryReadObject(id, maxBytes, out _, out var data)) return data;
        throw new FileNotFoundException($"object not found: {id}");
    }

    public void Dispose()
    {
        foreach (var pack in _packs) pack.Dispose();
    }
}
```

Также добавить в `LooseObjectReader` метод `Contains`:

```csharp
    public bool Contains(in ObjectId id) => File.Exists(PathFor(id));
```

- [x] **Step 4: Зелёная фаза** — 2 теста PASS.
- [x] **Step 5: Commit** — `feat(core): composite ObjectReader — loose first, then packs`

---

### Task 5: Полный прогон

- [x] **Step 1:** `dotnet test gitfs.slnx` — все тесты PASS (17 старых + 14 новых = 31).
- [x] **Step 2:** `git add docs/superpowers/plans/2026-08-07-gitfs-m1b-packs.md && git commit -m "docs: M1b implementation plan (packfiles + deltas)"`

---

## Вне этого плана (следующие)

M1c: разбор commit/tree (`CommitObject`, `TreeObject`, `TreeEntry`, режимы),
`TreeWalker.Resolve(root, path)`, `RevWalker.FirstParent`. Затем M2: `RepoSnapshot`,
`PathGrammar`, `NamePolicy`, вьюха `branches` — дерево без файловой системы.

## Зафиксированный технический долг (из адверсариального ревью)

Записано явно, чтобы отступления от спеки не были молчаливыми:

1. **`DeltaBaseCache` отсутствует** (§6.4, §7). Каждый `TryReadObject` заново
   инфлирует цепочку от mmap: N объектов на общей цепочке глубины d стоят
   O(N·d) инфляций. **Дедлайн — до M4**: `history/` и `dates/` — первые
   квадратичные потребители, бюджеты §16 (листинг < 50 мс) без кэша не
   выполняются. Открытая развилка слоя: §18 кладёт CacheSet в Gitfs.Vfs, но
   кэш баз работает внутри цикла разворота PackFile — решить при реализации
   (практично: LRU внутри пак-слоя с ключом OID/offset базы, позже
   подключаемый к общему `--cache-mb`).
2. **`SizeCache` отсутствует** (§6.4, §7). `TryGetHeader` дельта-объекта
   каждый раз проходит цепочку до базы ради типа. `GetAttr`/`Lookup`
   вызываются на порядок чаще `Read` — нужен словарь OID → (тип, размер)
   с мелким фиксированным бюджетом. Вместе с п.1, до M4.
3. **`IObjectReader.OpenStream` не реализован для паков** (§6.1). Потоковое
   чтение есть только у loose; packed-объекты материализуются в byte[].
   Нужен к M3 (IMountTarget.Read по окнам), критичен к M5 (большие файлы
   из старых коммитов). Непротиворечивый v1: не-дельта — ZLibStream поверх
   view, дельта — материализация + MemoryStream; настоящий потоковый
   разворот дельт — за пределами v1.
4. **Принятые пробелы покрытия**: большие (>2 ГиБ) смещения проверены только
   на уровне PackIndex синтетикой (end-to-end через разрежённый пак — по
   желанию, `[Trait("Category","Slow")]`); байт 0x08 смещения copy-команды
   (offset ≥ 16 МиБ) требует базы > 16 МБ — не покрыт сознательно;
   MaxDeltaChain и «truncated packed object» требуют крафтовых паков.
5. **Нейминг выровнен**: спека говорила `PackReader`, код — `PackFile`;
   спека приведена к коду (правка §5/§18 от 2026-08-07).
