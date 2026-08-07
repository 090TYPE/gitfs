using Gitfs.Vfs;

namespace Gitfs.Vfs.Tests;

public class PathGrammarTests
{
    [Theory]
    [InlineData("/branches/main/src/", new[] { "branches", "main", "src" })]
    [InlineData("branches\\main\\a.txt", new[] { "branches", "main", "a.txt" })]
    [InlineData("", new string[0])]
    [InlineData("/", new string[0])]
    public void Split_normalizes_both_separators(string path, string[] expected)
    {
        Assert.Equal(expected, PathGrammar.Split(path));
    }

    [Theory]
    [InlineData("a/../b")]
    [InlineData("a/./b")]
    [InlineData("..")]
    public void Dot_segments_are_rejected_at_parse_time(string path)
    {
        Assert.Null(PathGrammar.Split(path));
    }

    private static readonly IReadOnlySet<string> Refs =
        new HashSet<string>(StringComparer.Ordinal) { "main", "feature/login" };

    [Fact]
    public void Longest_ref_match_wins()
    {
        var m = PathGrammar.MatchLongestRef(Refs, new[] { "feature", "login", "src", "f.cs" });
        Assert.Equal("feature/login", m!.Value.RefName);
        Assert.Equal(new[] { "src", "f.cs" }, m.Value.Rest);
    }

    [Fact]
    public void Exact_ref_leaves_empty_rest()
    {
        var m = PathGrammar.MatchLongestRef(Refs, new[] { "main" });
        Assert.Equal("main", m!.Value.RefName);
        Assert.Empty(m.Value.Rest);
    }

    [Fact]
    public void Prefix_is_not_a_match_but_is_a_prefix()
    {
        Assert.Null(PathGrammar.MatchLongestRef(Refs, new[] { "feature" }));
        Assert.True(PathGrammar.IsRefPrefix(Refs, new[] { "feature" }));
        Assert.False(PathGrammar.IsRefPrefix(Refs, new[] { "nosuch" }));
    }
}
