using Gitfs.Core;
using Gitfs.Core.Objects;
using Gitfs.Core.Tests.Fixtures;
using Gitfs.Core.Walk;

namespace Gitfs.Core.Tests;

public class WalkersAndTagTests
{
    private static RepoBuilder BuildHistory()
    {
        var repo = new RepoBuilder();
        repo.WriteFile("README.md", "hello\n");
        repo.WriteFile("src/Program.cs", "class P {}\n");
        repo.WriteFile("src/nested/deep.txt", "deep\n");
        repo.CommitAll("root");
        repo.Branch("side");
        repo.WriteFile("src/Program.cs", "class P { int X; }\n");
        repo.CommitAll("main 2");
        repo.Checkout("side");
        repo.WriteFile("side.txt", "side\n");
        repo.CommitAll("side 1");
        repo.Checkout("main");
        repo.Merge("side");
        repo.Tag("v1.0", annotated: true, message: "release");
        return repo;
    }

    // ---------- TagObject ----------

    [Fact]
    public void Tag_fields_match_git_and_peel_reaches_commit()
    {
        using var repo = BuildHistory();
        using var reader = new ObjectReader(repo.GitDir);
        var tagId = ObjectId.Parse(repo.Run("rev-parse", "v1.0").Trim());
        var tag = TagObject.Parse(tagId, reader.ReadAll(tagId, 1 << 20));

        Assert.Equal(repo.Run("rev-parse", "v1.0^{commit}").Trim(), tag.Target.ToString());
        Assert.Equal(GitObjectType.Commit, tag.TargetType);
        Assert.Equal("v1.0", tag.Name);
        Assert.StartsWith("release", tag.Message);

        var (peeled, type) = TagObject.Peel(reader, tagId);
        Assert.Equal(tag.Target, peeled);
        Assert.Equal(GitObjectType.Commit, type);
    }

    [Fact]
    public void Peel_of_non_tag_returns_itself()
    {
        using var repo = BuildHistory();
        using var reader = new ObjectReader(repo.GitDir);
        var head = ObjectId.Parse(repo.Run("rev-parse", "HEAD").Trim());
        var (peeled, type) = TagObject.Peel(reader, head);
        Assert.Equal(head, peeled);
        Assert.Equal(GitObjectType.Commit, type);
    }

    // ---------- TreeWalker ----------

    [Fact]
    public void Resolves_nested_paths_to_the_same_ids_as_rev_parse()
    {
        using var repo = BuildHistory();
        using var reader = new ObjectReader(repo.GitDir);
        var walker = new TreeWalker(reader);
        var root = ObjectId.Parse(repo.Run("rev-parse", "HEAD^{tree}").Trim());

        var file = walker.TryResolve(root, new[] { "src", "Program.cs" });
        Assert.Equal(repo.Run("rev-parse", "HEAD:src/Program.cs").Trim(), file!.Value.Id.ToString());
        Assert.Equal(GitFileMode.RegularFile, file.Value.Mode);

        var dir = walker.TryResolve(root, new[] { "src" });
        Assert.Equal(repo.Run("rev-parse", "HEAD:src").Trim(), dir!.Value.Id.ToString());
        Assert.Equal(GitFileMode.Directory, dir.Value.Mode);

        var deep = walker.TryResolve(root, new[] { "src", "nested", "deep.txt" });
        Assert.Equal(repo.Run("rev-parse", "HEAD:src/nested/deep.txt").Trim(), deep!.Value.Id.ToString());
    }

    [Fact]
    public void Missing_path_and_path_through_blob_return_null()
    {
        using var repo = BuildHistory();
        using var reader = new ObjectReader(repo.GitDir);
        var walker = new TreeWalker(reader);
        var root = ObjectId.Parse(repo.Run("rev-parse", "HEAD^{tree}").Trim());

        Assert.Null(walker.TryResolve(root, new[] { "no", "such", "path" }));
        Assert.Null(walker.TryResolve(root, new[] { "README.md", "x" })); // сквозь файл
    }

    [Fact]
    public void Empty_path_is_the_root_directory()
    {
        using var repo = BuildHistory();
        using var reader = new ObjectReader(repo.GitDir);
        var walker = new TreeWalker(reader);
        var root = ObjectId.Parse(repo.Run("rev-parse", "HEAD^{tree}").Trim());

        var entry = walker.TryResolve(root, ReadOnlySpan<string>.Empty);
        Assert.Equal(root, entry!.Value.Id);
        Assert.Equal(GitFileMode.Directory, entry.Value.Mode);
    }

    // ---------- RevWalker ----------

    [Fact]
    public void First_parent_chain_matches_git_rev_list()
    {
        using var repo = BuildHistory();
        using var reader = new ObjectReader(repo.GitDir);
        var walker = new RevWalker(reader);
        var head = ObjectId.Parse(repo.Run("rev-parse", "HEAD").Trim());

        var expected = repo.Run("rev-list", "--first-parent", "HEAD")
            .Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var actual = walker.FirstParent(head).Select(c => c.Id.ToString()).ToArray();
        Assert.Equal(expected, actual);
        // merge пройден по первому родителю: коммит side-ветки в выдачу не попал
        Assert.DoesNotContain(repo.Run("rev-parse", "HEAD^2").Trim(), actual);
    }

    [Fact]
    public void Walk_can_start_mid_history_and_is_lazy()
    {
        using var repo = BuildHistory();
        using var reader = new ObjectReader(repo.GitDir);
        var walker = new RevWalker(reader);
        var mid = ObjectId.Parse(repo.Run("rev-parse", "HEAD^").Trim());

        var expected = repo.Run("rev-list", "--first-parent", "HEAD^")
            .Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(expected, walker.FirstParent(mid).Select(c => c.Id.ToString()).ToArray());

        // ленивость: первый элемент доступен без материализации всей истории
        Assert.Equal(mid, walker.FirstParent(mid).First().Id);
    }
}
