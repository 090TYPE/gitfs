using Gitfs.Core;
using Gitfs.Core.Accel;
using Gitfs.Core.Objects;
using Gitfs.Core.Tests.Fixtures;
using Gitfs.Core.Walk;

namespace Gitfs.Core.Tests;

/// <summary>Ускоритель обязан давать РОВНО те же ответы, что чтение объектов —
/// иначе он не ускоритель, а второй источник истины.</summary>
public class CommitGraphTests
{
    private static RepoBuilder BuildRepo(int commits = 12, bool withGraph = true)
    {
        var repo = new RepoBuilder();
        for (var i = 0; i < commits; i++)
        {
            repo.WriteFile("data.txt", string.Concat(Enumerable.Repeat("x\n", i + 1)));
            repo.CommitAll($"commit {i}");
        }
        if (withGraph) repo.Run("commit-graph", "write", "--reachable");
        return repo;
    }

    [Fact]
    public void Missing_graph_is_not_an_error()
    {
        using var repo = BuildRepo(withGraph: false);
        Assert.Null(CommitGraph.TryLoad(repo.GitDir));
    }

    [Fact]
    public void Graph_agrees_with_object_reads_on_every_commit()
    {
        using var repo = BuildRepo();
        var graph = CommitGraph.TryLoad(repo.GitDir);
        Assert.NotNull(graph);
        using var reader = new ObjectReader(repo.GitDir);

        foreach (var line in repo.Run("rev-list", "HEAD")
                     .Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var id = ObjectId.Parse(line.Trim());
            Assert.True(graph!.TryGetCommit(id, out var tree, out var parent));

            var commit = CommitObject.Parse(id, reader.ReadAll(id, 1 << 20));
            Assert.Equal(commit.Tree, tree);
            if (commit.Parents.Count == 0) Assert.Null(parent);
            else Assert.Equal(commit.Parents[0], parent);
        }
    }

    [Fact]
    public void Unknown_commit_is_reported_missing()
    {
        using var repo = BuildRepo();
        var graph = CommitGraph.TryLoad(repo.GitDir)!;
        Assert.False(graph.TryGetCommit(
            ObjectId.Parse("0123456789012345678901234567890123456789"), out _, out _));
    }

    [Fact]
    public void Walk_with_and_without_the_graph_produce_identical_history()
    {
        using var repo = BuildRepo();
        using var reader = new ObjectReader(repo.GitDir);
        var head = ObjectId.Parse(repo.Run("rev-parse", "HEAD").Trim());

        var plain = new RevWalker(reader).FirstParentTrees(head).ToList();
        var accelerated = new RevWalker(reader, CommitGraph.TryLoad(repo.GitDir))
            .FirstParentTrees(head).ToList();

        Assert.Equal(plain, accelerated);
        Assert.Equal(
            repo.Run("rev-list", "--first-parent", "HEAD")
                .Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(s => s.Trim()),
            accelerated.Select(c => c.Id.ToString()));
    }

    [Fact]
    public void Commit_created_after_the_graph_still_walks()
    {
        using var repo = BuildRepo();
        // граф записан ДО этого коммита — обход обязан доехать через объекты
        repo.WriteFile("fresh.txt", "after the graph\n");
        var fresh = repo.CommitAll("after graph");

        using var reader = new ObjectReader(repo.GitDir);
        var walker = new RevWalker(reader, CommitGraph.TryLoad(repo.GitDir));
        var walked = walker.FirstParentTrees(ObjectId.Parse(fresh)).Select(c => c.Id.ToString()).ToList();

        Assert.Equal(
            repo.Run("rev-list", "--first-parent", "HEAD")
                .Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(s => s.Trim()),
            walked);
    }

    [Fact]
    public void Corrupt_graph_is_ignored_rather_than_fatal()
    {
        using var repo = BuildRepo();
        var path = Path.Combine(repo.GitDir, "objects", "info", "commit-graph");
        var bytes = File.ReadAllBytes(path);
        File.SetAttributes(path, FileAttributes.Normal);

        bytes[0] ^= 0xff;                       // испорченная сигнатура
        File.WriteAllBytes(path, bytes);
        Assert.Null(CommitGraph.TryLoad(repo.GitDir));

        File.WriteAllBytes(path, bytes.AsSpan(0, 6).ToArray()); // обрезан
        Assert.Null(CommitGraph.TryLoad(repo.GitDir));
    }
}
