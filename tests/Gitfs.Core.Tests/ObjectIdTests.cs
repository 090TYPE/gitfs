using Gitfs.Core;

namespace Gitfs.Core.Tests;

public class ObjectIdTests
{
    private const string Hex = "a3f9c21d8e07b4415c6a9b02f13d5e6789abcdef";

    [Fact]
    public void Parse_roundtrips_to_lowercase_hex()
    {
        Assert.Equal(Hex, ObjectId.Parse(Hex).ToString());
        Assert.Equal(Hex, ObjectId.Parse(Hex.ToUpperInvariant()).ToString());
    }

    [Fact]
    public void Raw_roundtrip_preserves_bytes()
    {
        var raw = Convert.FromHexString(Hex);
        var id = new ObjectId(raw);
        Span<byte> back = stackalloc byte[ObjectId.RawLength];
        id.WriteRaw(back);
        Assert.True(back.SequenceEqual(raw));
        Assert.Equal(raw[0], id.FirstByte);
    }

    [Fact]
    public void Equality_and_comparison_follow_byte_order()
    {
        var a = ObjectId.Parse("00" + Hex[2..]);
        var b = ObjectId.Parse("ff" + Hex[2..]);
        Assert.True(a.Equals(ObjectId.Parse("00" + Hex[2..])));
        Assert.True(a.CompareTo(b) < 0);           // memcmp-порядок, как в .idx
        Assert.True(b.CompareTo(a) > 0);
        Assert.NotEqual(a.GetHashCode(), b.GetHashCode());
    }

    [Theory]
    [InlineData("")]
    [InlineData("a3f9")]
    [InlineData("g3f9c21d8e07b4415c6a9b02f13d5e6789abcdef")] // не hex
    public void TryParse_rejects_invalid(string input)
    {
        Assert.False(ObjectId.TryParse(input, out _));
    }
}
