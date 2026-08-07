using System.Buffers.Binary;

namespace Gitfs.Core;

/// <summary>SHA-1 идентификатор git-объекта: 20 байт inline, без аллокаций.
/// CompareTo упорядочивает как memcmp сырых байт — этого требует бинарный
/// поиск в .idx (план M1b).</summary>
public readonly struct ObjectId : IEquatable<ObjectId>, IComparable<ObjectId>
{
    public const int RawLength = 20;
    public const int HexLength = 40;

    private readonly ulong _a; // байты 0..7, big-endian
    private readonly ulong _b; // байты 8..15
    private readonly uint _c;  // байты 16..19

    public ObjectId(ReadOnlySpan<byte> raw)
    {
        if (raw.Length != RawLength)
            throw new ArgumentException($"expected {RawLength} bytes, got {raw.Length}", nameof(raw));
        _a = BinaryPrimitives.ReadUInt64BigEndian(raw);
        _b = BinaryPrimitives.ReadUInt64BigEndian(raw[8..]);
        _c = BinaryPrimitives.ReadUInt32BigEndian(raw[16..]);
    }

    public byte FirstByte => (byte)(_a >> 56);

    public void WriteRaw(Span<byte> destination)
    {
        BinaryPrimitives.WriteUInt64BigEndian(destination, _a);
        BinaryPrimitives.WriteUInt64BigEndian(destination[8..], _b);
        BinaryPrimitives.WriteUInt32BigEndian(destination[16..], _c);
    }

    public static ObjectId Parse(ReadOnlySpan<char> hex) =>
        TryParse(hex, out var id) ? id : throw new FormatException($"invalid object id: '{hex}'");

    public static bool TryParse(ReadOnlySpan<char> hex, out ObjectId id)
    {
        id = default;
        if (hex.Length != HexLength) return false;
        Span<byte> raw = stackalloc byte[RawLength];
        for (var i = 0; i < RawLength; i++)
        {
            var hi = Nibble(hex[i * 2]);
            var lo = Nibble(hex[i * 2 + 1]);
            if (hi < 0 || lo < 0) return false;
            raw[i] = (byte)((hi << 4) | lo);
        }
        id = new ObjectId(raw);
        return true;
    }

    private static int Nibble(char c) => c switch
    {
        >= '0' and <= '9' => c - '0',
        >= 'a' and <= 'f' => c - 'a' + 10,
        >= 'A' and <= 'F' => c - 'A' + 10,
        _ => -1,
    };

    public override string ToString()
    {
        Span<byte> raw = stackalloc byte[RawLength];
        WriteRaw(raw);
        return Convert.ToHexString(raw).ToLowerInvariant();
    }

    public bool Equals(ObjectId other) => _a == other._a && _b == other._b && _c == other._c;
    public override bool Equals(object? obj) => obj is ObjectId other && Equals(other);
    public override int GetHashCode() => HashCode.Combine(_a, _b, _c);

    public int CompareTo(ObjectId other)
    {
        var a = _a.CompareTo(other._a);
        if (a != 0) return a;
        var b = _b.CompareTo(other._b);
        return b != 0 ? b : _c.CompareTo(other._c);
    }

    public static bool operator ==(ObjectId left, ObjectId right) => left.Equals(right);
    public static bool operator !=(ObjectId left, ObjectId right) => !left.Equals(right);
}
