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
