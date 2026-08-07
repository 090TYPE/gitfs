using Gitfs.Vfs;

namespace Gitfs.Vfs.Tests;

public class NamePolicyTests
{
    private static readonly NamePolicy Win = NamePolicy.Windows;

    // ---------- правило 1: недопустимые символы → %XX ----------

    [Theory]
    [InlineData("a:b*c?.txt", "a%3Ab%2Ac%3F.txt")]
    [InlineData("100%.txt", "100%25.txt")]        // % экранируется ради обратимости
    [InlineData("<>|\"", "%3C%3E%7C%22")]
    [InlineData("bell.txt", "bell%07.txt")] // управляющий символ
    [InlineData("plain-name.txt", "plain-name.txt")]
    public void Invalid_characters_are_percent_encoded(string gitName, string expected)
    {
        Assert.Equal(expected, Win.EncodeName(gitName));
    }

    [Theory]
    [InlineData("name.", "name%2E")]
    [InlineData("name ", "name%20")]
    [InlineData("name..", "name%2E%2E")]
    [InlineData("name. ", "name%2E%20")]
    [InlineData("na.me", "na.me")]                // точка в середине легальна
    public void Trailing_dots_and_spaces_are_encoded(string gitName, string expected)
    {
        Assert.Equal(expected, Win.EncodeName(gitName));
    }

    // ---------- правило 2: зарезервированные имена → %RES ----------

    [Theory]
    [InlineData("CON", "CON%RES")]
    [InlineData("aux.c", "aux%RES.c")]
    [InlineData("Com3.TXT", "Com3%RES.TXT")]
    [InlineData("AUX.tar.gz", "AUX%RES.tar.gz")]  // база — до первой точки
    [InlineData("lpt10.txt", "lpt10.txt")]        // LPT только 1..9
    [InlineData("console.log", "console.log")]    // не точное совпадение базы
    public void Reserved_device_names_get_res_suffix(string gitName, string expected)
    {
        Assert.Equal(expected, Win.EncodeName(gitName));
    }

    // ---------- правило 3: коллизии регистра → ~N ----------

    [Fact]
    public void Case_collisions_get_ordinal_suffixes_in_git_order()
    {
        var listing = Win.EncodeListing(new[] { "README", "readme", "ReadMe", "other.txt" });
        Assert.Equal(
            new[] { ("README", "README"), ("readme~2", "readme"), ("ReadMe~3", "ReadMe"),
                    ("other.txt", "other.txt") },
            listing.Select(e => (e.Display, e.GitName)).ToArray());
    }

    [Fact]
    public void Encoding_happens_before_collision_check()
    {
        // после %XX-кодирования имена могут совпасть — суффикс обязан сработать
        var listing = Win.EncodeListing(new[] { "a%3A", "a:" }); // второе кодируется в a%3A
        Assert.Equal(new[] { "a%253A", "a%3A" },
            listing.Select(e => e.Display).ToArray()); // % в первом экранирован → коллизии нет
    }

    // ---------- политики ----------

    [Fact]
    public void Posix_policy_is_identity()
    {
        Assert.Equal("aux.c", NamePolicy.Posix.EncodeName("aux.c"));
        Assert.Equal("a:b", NamePolicy.Posix.EncodeName("a:b"));
        var listing = NamePolicy.Posix.EncodeListing(new[] { "README", "readme" });
        Assert.Equal(new[] { "README", "readme" }, listing.Select(e => e.Display).ToArray());
    }

    [Fact]
    public void MacOs_policy_folds_case_only()
    {
        Assert.Equal("aux.c", NamePolicy.MacOs.EncodeName("aux.c"));   // без %RES
        Assert.Equal("a:b", NamePolicy.MacOs.EncodeName("a:b"));       // без %XX
        var listing = NamePolicy.MacOs.EncodeListing(new[] { "README", "readme" });
        Assert.Equal(new[] { "README", "readme~2" }, listing.Select(e => e.Display).ToArray());
    }

    [Fact]
    public void Portable_equals_windows()
    {
        Assert.Same(NamePolicy.Windows, NamePolicy.For(NamePolicyKind.Portable));
    }
}
