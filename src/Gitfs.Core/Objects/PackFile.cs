using System.IO.Compression;
using System.IO.MemoryMappedFiles;

namespace Gitfs.Core.Objects;

/// <summary>Пакет .pack поверх memory-mapped file: проекция одна, читать её
/// многими потоками безопасно; на операцию открывается свой view-stream.
/// Цепочки дельт разворачиваются итеративно (спека §6.4).</summary>
public sealed class PackFile : IDisposable
{
    private const int MaxDeltaChain = 1000;
    private const long DefaultDeltaBaseCacheBytes = 32L << 20;
    private const int SizeCacheEntries = 1 << 16;

    private readonly MemoryMappedFile _mmap;
    private readonly long _length;
    private readonly PackIndex _index;

    // DeltaBaseCache (§6.4/§7): развёрнутые данные по смещению в паке —
    // без него N объектов на общей цепочке глубины d стоят O(N·d) инфляций.
    private readonly LruCache<long, (GitObjectType Type, byte[] Data)> _resolved;
    // SizeCache (§7): GetAttr/Lookup зовутся на порядок чаще Read,
    // а тип дельта-объекта добывается только спуском по цепочке.
    private readonly LruCache<ObjectId, (GitObjectType Type, long Size)> _sizes;

    public long DeltaBaseCacheHits => _resolved.Hits;
    public long SizeCacheHits => _sizes.Hits;

    private PackFile(MemoryMappedFile mmap, long length, PackIndex index, long cacheBytes,
        long? maxObjectBytes)
    {
        _mmap = mmap;
        _length = length;
        _index = index;
        _resolved = new LruCache<long, (GitObjectType, byte[])>(cacheBytes, e => e.Item2.Length,
            maxObjectBytes);
        _sizes = new LruCache<ObjectId, (GitObjectType, long)>(SizeCacheEntries, _ => 1);
    }

    /// <param name="maxObjectBytes">Потолок для одного развёрнутого объекта в
    /// кэше дельт: «Max cached blob» из настроек монтирования. null — только
    /// общий бюджет.</param>
    public static PackFile Open(string packPath, long deltaBaseCacheBytes = DefaultDeltaBaseCacheBytes,
        long? maxObjectBytes = null)
    {
        var index = PackIndex.Load(Path.ChangeExtension(packPath, ".idx"));
        var length = new FileInfo(packPath).Length;
        if (length < 32) // 12 байт заголовка + 20 байт sha1-трейлера
            throw new InvalidDataException($"pack too short: {packPath}");
        var mmap = MemoryMappedFile.CreateFromFile(packPath, FileMode.Open, null, 0,
            MemoryMappedFileAccess.Read);
        return new PackFile(mmap, length, index, deltaBaseCacheBytes, maxObjectBytes);
    }

    public bool Contains(in ObjectId id) => _index.TryFindOffset(id, out _);

    /// <summary>Сколько объектов в этом пакете — из заголовка .idx.</summary>
    public int ObjectCount => _index.Count;

    /// <summary>Все OID пакета в порядке .idx (отсортированы).</summary>
    public IEnumerable<ObjectId> ObjectIds => _index.ObjectIds;

    public bool TryGetHeader(in ObjectId id, out GitObjectType type, out long size)
    {
        if (_sizes.TryGet(id, out var cached))
        {
            (type, size) = cached;
            return true;
        }
        type = default;
        size = 0;
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
                var baseRef = ReadDeltaBase(view, raw, dataStart, out var deltaDataStart);
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
            _sizes.Set(id, (type, size));
            return true;
        }
    }

    public bool TryReadObject(in ObjectId id, long maxBytes, out GitObjectType type, out byte[] data)
    {
        type = default;
        data = [];
        if (!_index.TryFindOffset(id, out var offset)) return false;

        // Вниз по цепочке: собираем дельты (со смещениями их объектов) в стек,
        // находим базу — или её кэшированный разворот; затем вверх — применяем,
        // кэшируя каждый промежуточный результат: он и есть объект на смещении
        // своей дельты.
        var deltas = new Stack<(long Offset, byte[] Delta)>();
        while (true)
        {
            if (_resolved.TryGet(offset, out var cachedBase))
            {
                (type, data) = cachedBase;
                break;
            }
            using var view = OpenView(offset);
            var (raw, size, dataStart) = ReadObjectHeader(view, offset);
            if (raw is RawType.OfsDelta or RawType.RefDelta)
            {
                if (deltas.Count >= MaxDeltaChain) throw new InvalidDataException("delta chain too long");
                var baseRef = ReadDeltaBase(view, raw, dataStart, out var deltaDataStart);
                deltas.Push((offset, Inflate(deltaDataStart, size, maxBytes)));
                offset = ResolveBaseOffset(baseRef, offset);
                continue;
            }
            type = (GitObjectType)raw;
            data = Inflate(dataStart, size, maxBytes);
            _resolved.Set(offset, (type, data));
            break;
        }
        while (deltas.Count > 0)
        {
            var (deltaOffset, delta) = deltas.Pop();
            data = DeltaCodec.Apply(data, delta, maxBytes); // потолок ДО аллокации
            _resolved.Set(deltaOffset, (type, data));
        }
        _sizes.Set(id, (type, data.LongLength));
        return true;
    }

    /// <summary>Потоковое чтение (§6.1). Не-дельта — честный zlib-поток поверх
    /// mmap-view (ничего не материализуется). Дельта — материализация через
    /// TryReadObject + MemoryStream: потоковый разворот дельт вне v1
    /// (долг M1b, записан). null — объекта нет в паке.</summary>
    public Stream? TryOpenStream(in ObjectId id, long maxBytes, out GitObjectType type, out long size)
    {
        type = default;
        size = 0;
        if (!_index.TryFindOffset(id, out var offset)) return null;

        using (var view = OpenView(offset))
        {
            var (raw, headerSize, dataStart) = ReadObjectHeader(view, offset);
            if (raw is not (RawType.OfsDelta or RawType.RefDelta))
            {
                type = (GitObjectType)raw;
                size = headerSize;
                if (size > maxBytes)
                    throw new InvalidDataException($"object {id} is {size} bytes, over the {maxBytes} limit");
                var dataView = OpenView(dataStart);
                return new LimitedReadStream(
                    new System.IO.Compression.ZLibStream(dataView,
                        System.IO.Compression.CompressionMode.Decompress), size);
            }
        }
        if (!TryReadObject(id, maxBytes, out type, out var data)) return null;
        size = data.LongLength;
        return new MemoryStream(data, writable: false);
    }

    // --- низкоуровневое ---

    private enum RawType { Commit = 1, Tree = 2, Blob = 3, Tag = 4, OfsDelta = 6, RefDelta = 7 }

    private readonly record struct BaseRef(long OfsNegative, ObjectId? RefId);

    private MemoryMappedViewStream OpenView(long offset)
    {
        // Смещение приходит из .idx и не заслуживает доверия. Особенно коварен
        // offset == длине файла: mmap выравнен по страницам, view нулевого
        // размера молча отдал бы нулевой хвост страницы — «успешный» разбор мусора.
        if (offset < 12 || offset >= _length - 20)
            throw new InvalidDataException($"pack offset {offset} out of range");
        return _mmap.CreateViewStream(offset, _length - offset, MemoryMappedFileAccess.Read);
    }

    /// <summary>Читает заголовок объекта; возвращает тип, размер (для дельты —
    /// размер дельты) и абсолютное смещение начала данных.</summary>
    private static (RawType Type, long Size, long DataStart) ReadObjectHeader(Stream view, long offset)
    {
        var pos = 0L;
        var b = ReadByteOrThrow(view);
        pos++;
        var type = (RawType)((b >> 4) & 0x7);
        if (type is not (RawType.Commit or RawType.Tree or RawType.Blob or RawType.Tag
            or RawType.OfsDelta or RawType.RefDelta))
            throw new InvalidDataException($"bad packed object type {(int)type}");
        long size = b & 0xf;
        var shift = 4;
        while ((b & 0x80) != 0)
        {
            b = ReadByteOrThrow(view);
            pos++;
            // C# маскирует сдвиг по 63 — без стражи сверхдлинный varint даёт
            // ТИХО неверный (в т.ч. отрицательный) размер, git здесь падает.
            var bits = (long)(b & 0x7f);
            if (shift >= 63 || bits << shift >> shift != bits)
                throw new InvalidDataException("object size varint overflow");
            size |= bits << shift;
            shift += 7;
        }
        return (type, size, offset + pos);
    }

    /// <summary>Для дельты — читает ссылку на базу сразу после заголовка
    /// (view уже спозиционирован там); возвращает её и абсолютное смещение
    /// начала zlib-данных дельты.</summary>
    private static BaseRef ReadDeltaBase(Stream view, RawType type, long dataStart,
        out long deltaDataStart)
    {
        var consumed = 0L;
        if (type == RawType.OfsDelta)
        {
            var b = ReadByteOrThrow(view);
            consumed++;
            long n = b & 0x7f;
            while ((b & 0x80) != 0)
            {
                // страж переполнения «прибавляющего» varint — аналог MSB(n,7) в git
                if ((n & unchecked((long)0xFE00_0000_0000_0000UL)) != 0)
                    throw new InvalidDataException("ofs-delta varint overflow");
                b = ReadByteOrThrow(view);
                consumed++;
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
        if (declaredSize < 0 || declaredSize > maxBytes)
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
        if (z.ReadByte() != -1)
            throw new InvalidDataException("packed object longer than declared size");
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
