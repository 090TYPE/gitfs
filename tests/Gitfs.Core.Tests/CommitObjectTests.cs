using System.Text;
using Gitfs.Core;
using Gitfs.Core.Objects;
using Gitfs.Core.Tests.Fixtures;

namespace Gitfs.Core.Tests;

public class CommitObjectTests
{
    private static RepoBuilder BuildHistory()
    {
        var repo = new RepoBuilder();
        repo.WriteFile("a.txt", "a\n");
        repo.CommitAll("root commit");
        repo.Branch("side");
        repo.WriteFile("a.txt", "main change\n");
        repo.CommitAll("main line\n\nwith a body\nand another line\n");
        repo.Checkout("side");
        repo.WriteFile("b.txt", "side\n");
        repo.CommitAll("side change");
        repo.Checkout("main");
        repo.Merge("side");
        return repo;
    }

    private static CommitObject ParseAt(RepoBuilder repo, ObjectReader reader, string rev)
    {
        var id = ObjectId.Parse(repo.Run("rev-parse", rev).Trim());
        return CommitObject.Parse(id, reader.ReadAll(id, 1 << 20));
    }

    private static string[] LogFields(RepoBuilder repo, string rev) =>
        repo.Run("log", "-1", "--format=%T%n%P%n%an%n%ae%n%at%n%cn%n%ce%n%ct", rev)
            .TrimEnd('\n').Split('\n');

    [Theory]
    [InlineData("HEAD")]        // merge: 2 родителя
    [InlineData("HEAD^")]       // обычный: 1 родитель
    [InlineData("HEAD^^")]      // корневой: 0 родителей
    public void Fields_match_git_log(string rev)
    {
        using var repo = BuildHistory();
        using var reader = new ObjectReader(repo.GitDir);
        var commit = ParseAt(repo, reader, rev);
        var f = LogFields(repo, rev);

        Assert.Equal(f[0], commit.Tree.ToString());
        Assert.Equal(f[1], string.Join(' ', commit.Parents.Select(p => p.ToString())));
        Assert.Equal(f[2], commit.Author.Name);
        Assert.Equal(f[3], commit.Author.Email);
        Assert.Equal(long.Parse(f[4]), commit.Author.When.ToUnixTimeSeconds());
        Assert.Equal(f[5], commit.Committer.Name);
        Assert.Equal(f[6], commit.Committer.Email);
        Assert.Equal(long.Parse(f[7]), commit.Committer.When.ToUnixTimeSeconds());
    }

    [Fact]
    public void Merge_lists_first_parent_first()
    {
        using var repo = BuildHistory();
        using var reader = new ObjectReader(repo.GitDir);
        var merge = ParseAt(repo, reader, "HEAD");
        Assert.Equal(2, merge.Parents.Count);
        // first-parent — линия main (HEAD^), второй — влитая side (HEAD^2)
        Assert.Equal(repo.Run("rev-parse", "HEAD^").Trim(), merge.Parents[0].ToString());
        Assert.Equal(repo.Run("rev-parse", "HEAD^2").Trim(), merge.Parents[1].ToString());
    }

    [Fact]
    public void Multiline_message_is_byte_exact()
    {
        using var repo = BuildHistory();
        using var reader = new ObjectReader(repo.GitDir);
        var commit = ParseAt(repo, reader, "HEAD^");
        Assert.Equal(repo.Run("log", "-1", "--format=%B", "HEAD^"), commit.Message + "\n");
    }

    [Fact]
    public void Gpgsig_and_unknown_headers_are_skipped_not_fatal()
    {
        using var repo = BuildHistory();
        using var reader = new ObjectReader(repo.GitDir);

        // крафт: берём сырой HEAD^, вставляем gpgsig с continuation-строками
        var raw = Encoding.UTF8.GetString(repo.RunBytes("cat-file", "commit", "HEAD^"));
        var committerEnd = raw.IndexOf('\n', raw.IndexOf("\ncommitter ", StringComparison.Ordinal) + 1);
        var crafted = raw[..(committerEnd + 1)]
            + "gpgsig -----BEGIN PGP SIGNATURE-----\n"
            + " iQEzBAABCAAdFakeSignatureLineOne\n"
            + " FakeSignatureLineTwo==\n"
            + " -----END PGP SIGNATURE-----\n"
            + raw[(committerEnd + 1)..];
        var sha = repo.RunWithInput(Encoding.UTF8.GetBytes(crafted),
            "hash-object", "-t", "commit", "-w", "--stdin").Trim();

        var reference = ParseAt(repo, reader, "HEAD^");
        var id = ObjectId.Parse(sha);
        var parsed = CommitObject.Parse(id, reader.ReadAll(id, 1 << 20));

        Assert.Equal(reference.Tree, parsed.Tree);
        Assert.Equal(reference.Parents, parsed.Parents);
        Assert.Equal(reference.Author.Email, parsed.Author.Email);
        Assert.Equal(reference.Message, parsed.Message);
    }
}
