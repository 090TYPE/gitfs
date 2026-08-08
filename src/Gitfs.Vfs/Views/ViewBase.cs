using Gitfs.Core;

namespace Gitfs.Vfs.Views;

/// <summary>Общая механика вьюх: чтение деревьев и коммитов через кэши
/// снапшота, спуск по ОТОБРАЖАЕМЫМ именам (иначе не резолвятся readme~2 и
/// aux%RES.c), перевод записи дерева в узел.</summary>
public abstract class ViewBase : IView
{
    protected const long MaxTreeBytes = 64L << 20;
    protected const long MaxCommitBytes = 16L << 20;

    protected NamePolicy Names { get; }

    protected ViewBase(NamePolicy names) => Names = names;

    public abstract string Name { get; }
    public abstract NodeInfo? Resolve(RepoSnapshot snapshot, IReadOnlyList<string> segments);
    public abstract IEnumerable<DirEntry> List(RepoSnapshot snapshot, IReadOnlyList<string> segments);

    // ---------- кэшированные чтения ----------

    protected static TreeObject ReadTree(RepoSnapshot snapshot, in ObjectId id)
    {
        if (snapshot.TreeCache.TryGet(id, out var cached)) return cached;
        var tree = TreeObject.Parse(snapshot.Objects.ReadAll(id, MaxTreeBytes));
        snapshot.TreeCache.Set(id, tree);
        return tree;
    }

    protected IReadOnlyList<DisplayName> DisplayNames(RepoSnapshot snapshot, in ObjectId treeId,
        TreeObject tree)
    {
        var key = (treeId, Names.Tag);
        if (snapshot.ListingCache.TryGet(key, out var cached)) return cached;
        var display = Names.EncodeListing(tree.Entries.Select(e => e.Name));
        snapshot.ListingCache.Set(key, display);
        return display;
    }

    /// <summary>Коммит по OID; null — объекта нет, он не коммит или битый.
    /// Ни при каких обстоятельствах не бросает — один битый ref не должен
    /// ронять перечисление всей вьюхи (ревью M2).</summary>
    protected static CommitObject? TryCommit(RepoSnapshot snapshot, ObjectId id)
    {
        try
        {
            if (!snapshot.Objects.TryGetHeader(id, out var type, out _)) return null;
            if (type == GitObjectType.Tag)
            {
                (id, type) = TagObject.Peel(snapshot.Objects, id);
            }
            if (type != GitObjectType.Commit) return null;
            return CommitObject.Parse(id, snapshot.Objects.ReadAll(id, MaxCommitBytes));
        }
        // ловим ЛЮБОЙ отказ чтения: обещание докстринга — «не бросает никогда»,
        // а битый пак умеет кидать и IOException, и ArgumentException (ревью M4)
        catch (Exception e) when (e is not OutOfMemoryException and not StackOverflowException)
        {
            return null;
        }
    }

    /// <summary>Вершина ссылки через кэш снапшота.</summary>
    protected static CommitObject? TipOf(RepoSnapshot snapshot, string refName) =>
        snapshot.TipCache.GetOrAdd(refName, name =>
            snapshot.Refs.TryResolve(name, out var entry)
                ? TryCommit(snapshot, entry.Peeled ?? entry.Target)
                : null);

    /// <summary>Опорная точка вьюх (спека §3.4): дата коммита HEAD.</summary>
    protected static DateTimeOffset ViewTimestamp(RepoSnapshot snapshot)
    {
        if (snapshot.Refs.HeadTarget is { } head && TryCommit(snapshot, head) is { } commit)
            return commit.Committer.When;
        return DateTimeOffset.UnixEpoch;
    }

    /// <summary>HEAD через кэш: ViewTimestamp зовётся из каждого Resolve корня,
    /// и без кэша коммит парсился заново на каждый вызов (ревью M4).</summary>
    protected static CommitObject? HeadCommit(RepoSnapshot snapshot) =>
        snapshot.Refs.HeadTarget is { } head
            ? snapshot.TipCache.GetOrAdd("HEAD", _ => TryCommit(snapshot, head))
            : null;

    // ---------- спуск по отображаемым именам ----------

    protected TreeEntry? ResolveDisplayPath(RepoSnapshot snapshot, ObjectId rootTree,
        IReadOnlyList<string> segments, int skip = 0)
    {
        var current = new TreeEntry("", rootTree, GitFileMode.Directory);
        for (var i = skip; i < segments.Count; i++)
        {
            if (current.Mode != GitFileMode.Directory) return null;
            var tree = ReadTree(snapshot, current.Id);
            var display = DisplayNames(snapshot, current.Id, tree);
            TreeEntry? next = null;
            for (var k = 0; k < tree.Entries.Count; k++)
            {
                if (string.Equals(display[k].Display, segments[i], Names.Comparison))
                {
                    next = tree.Entries[k];
                    break;
                }
            }
            if (next is null) return null;
            current = next.Value;
        }
        return current;
    }

    /// <summary>Листинг директории дерева с отображаемыми именами.</summary>
    protected IEnumerable<DirEntry> ListTree(RepoSnapshot snapshot, ObjectId treeId,
        DateTimeOffset stamp)
    {
        var tree = ReadTree(snapshot, treeId);
        var display = DisplayNames(snapshot, treeId, tree);
        for (var i = 0; i < tree.Entries.Count; i++)
            yield return new DirEntry(display[i].Display, ToNodeInfo(snapshot, tree.Entries[i], stamp));
    }

    protected static NodeInfo ToNodeInfo(RepoSnapshot snapshot, in TreeEntry entry,
        DateTimeOffset stamp)
    {
        switch (entry.Mode)
        {
            case GitFileMode.Directory:
                return NodeInfo.Directory(stamp);
            case GitFileMode.Gitlink:
                return new NodeInfo(NodeKind.Submodule, entry.Id, 0, stamp);
            case GitFileMode.Symlink:
            {
                snapshot.Objects.TryGetHeader(entry.Id, out _, out var linkSize);
                return new NodeInfo(NodeKind.Symlink, entry.Id, linkSize, stamp);
            }
            default:
            {
                snapshot.Objects.TryGetHeader(entry.Id, out _, out var size);
                return new NodeInfo(NodeKind.File, entry.Id, size, stamp);
            }
        }
    }

    /// <summary>Дерево коммита или узел внутри него — общий хвост
    /// для вьюх, у которых первый сегмент выбирает коммит.</summary>
    protected NodeInfo? ResolveInCommit(RepoSnapshot snapshot, CommitObject commit,
        IReadOnlyList<string> segments, int skip)
    {
        if (segments.Count == skip) return NodeInfo.Directory(commit.Committer.When);
        var entry = ResolveDisplayPath(snapshot, commit.Tree, segments, skip);
        return entry is null ? null : ToNodeInfo(snapshot, entry.Value, commit.Committer.When);
    }

    protected IEnumerable<DirEntry> ListInCommit(RepoSnapshot snapshot, CommitObject commit,
        IReadOnlyList<string> segments, int skip)
    {
        ObjectId treeId;
        if (segments.Count == skip)
        {
            treeId = commit.Tree;
        }
        else
        {
            var entry = ResolveDisplayPath(snapshot, commit.Tree, segments, skip);
            if (entry is null || entry.Value.Mode != GitFileMode.Directory) yield break;
            treeId = entry.Value.Id;
        }
        foreach (var e in ListTree(snapshot, treeId, commit.Committer.When)) yield return e;
    }
}
