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

    [Fact]
    public void Target_over_limit_throws_before_allocation()
    {
        // защита от DoS: маленькая дельта заявляет гигантский результат
        var delta = Delta(new byte[] { 11 }, EncodeVarint(1_000_000));
        Assert.Throws<InvalidDataException>(() => DeltaCodec.Apply(BaseData, delta, maxTargetBytes: 100));
    }

    [Fact]
    public void Copy_with_multibyte_offset_and_size()
    {
        var baseData = new byte[70000];
        new Random(7).NextBytes(baseData);
        var delta = Delta(
            EncodeVarint(70000),
            EncodeVarint(299),
            // offset 300 → байты 0x01|0x02; size 299 → байты 0x10|0x20
            new byte[] { 0b1011_0011, 44, 1, 43, 1 });
        var result = DeltaCodec.Apply(baseData, delta);
        Assert.Equal(baseData.AsSpan(300, 299).ToArray(), result);
    }

    [Fact]
    public void Reserved_zero_command_throws()
    {
        var delta = Delta(new byte[] { 11 }, new byte[] { 8 }, new byte[] { 0 });
        Assert.Throws<InvalidDataException>(() => DeltaCodec.Apply(BaseData, delta));
    }

    [Fact]
    public void Copy_out_of_base_range_throws()
    {
        var delta = Delta(new byte[] { 11 }, new byte[] { 20 },
            new byte[] { 0b1001_0001, 6, 20 }); // offset 6 + size 20 > base 11
        Assert.Throws<InvalidDataException>(() => DeltaCodec.Apply(BaseData, delta));
    }

    [Fact]
    public void Source_size_mismatch_throws()
    {
        var delta = Delta(new byte[] { 10 }, new byte[] { 8 }); // base на самом деле 11
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
