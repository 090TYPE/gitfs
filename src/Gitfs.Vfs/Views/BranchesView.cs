using Gitfs.Core;

namespace Gitfs.Vfs.Views;

/// <summary>branches/&lt;ветка&gt;/&lt;путь&gt; — рабочее дерево каждой ветки.
/// Слэш в имени ветки → вложенные директории; граница имени — наибольшее
/// совпадение по списку веток снапшота (спека §4). Спуск по дереву
/// NamePolicy-осведомлённый: сегмент сопоставляется с отображаемым именем —
/// только так резолвятся readme~2 и aux%RES.c.</summary>
public sealed class BranchesView : IView
{
    private const string Prefix = "refs/heads/";
    private const long MaxTreeBytes = 64L << 20;
    private const long MaxCommitBytes = 16L << 20;

    private readonly NamePolicy _names;

    public BranchesView(NamePolicy names) => _names = names;

    public string Name => "branches";

    public NodeInfo? Resolve(RepoSnapshot snapshot, IReadOnlyList<string> segments)
    {
        var branches = BranchNames(snapshot);
        if (segments.Count == 0)
            return NodeInfo.Directory(ViewTimestamp(snapshot));

        var array = segments as string[] ?? segments.ToArray();
        var match = PathGrammar.MatchLongestRef(branches, array);
        if (match is null)
            return PathGrammar.IsRefPrefix(branches, array)
                ? NodeInfo.Directory(ViewTimestamp(snapshot))
                : null;

        var (refName, tail) = match.Value;
        var tip = TipCommit(snapshot, refName);
        if (tip is null) return null;

        if (tail.Count == 0)
            return NodeInfo.Directory(tip.Committer.When);

        var entry = ResolveDisplayPath(snapshot, tip.Tree, tail);
        return entry is null ? null : ToNodeInfo(snapshot, entry.Value, tip.Committer.When);
    }

    public IEnumerable<DirEntry> List(RepoSnapshot snapshot, IReadOnlyList<string> segments)
    {
        var branches = BranchNames(snapshot);
        var array = segments as string[] ?? segments.ToArray();

        if (array.Length == 0 || (PathGrammar.MatchLongestRef(branches, array) is null
                                  && PathGrammar.IsRefPrefix(branches, array)))
        {
            // корень вьюхи или промежуточный сегмент имени ветки:
            // перечисляем следующий сегмент каждой подходящей ветки
            var prefix = array.Length == 0 ? "" : string.Join('/', array) + "/";
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var stamp = ViewTimestamp(snapshot);
            foreach (var branch in branches.Order(StringComparer.Ordinal))
            {
                if (!branch.StartsWith(prefix, StringComparison.Ordinal)) continue;
                var next = branch[prefix.Length..];
                var slash = next.IndexOf('/');
                var head = slash < 0 ? next : next[..slash];
                if (!seen.Add(head)) continue;
                if (slash < 0)
                {
                    var branchTip = TipCommit(snapshot, branch);
                    yield return new DirEntry(head,
                        NodeInfo.Directory(branchTip?.Committer.When ?? stamp));
                }
                else
                {
                    yield return new DirEntry(head, NodeInfo.Directory(stamp));
                }
            }
            yield break;
        }

        var match = PathGrammar.MatchLongestRef(branches, array);
        if (match is null) yield break;
        var (refName, tail) = match.Value;
        var tip = TipCommit(snapshot, refName);
        if (tip is null) yield break;

        // директория внутри дерева ветки
        TreeEntry dirEntry;
        if (tail.Count == 0)
        {
            dirEntry = new TreeEntry("", tip.Tree, GitFileMode.Directory);
        }
        else
        {
            var resolved = ResolveDisplayPath(snapshot, tip.Tree, tail);
            if (resolved is null || resolved.Value.Mode != GitFileMode.Directory) yield break;
            dirEntry = resolved.Value;
        }

        var tree = TreeObject.Parse(snapshot.Objects.ReadAll(dirEntry.Id, MaxTreeBytes));
        var display = _names.EncodeListing(tree.Entries.Select(e => e.Name));
        for (var i = 0; i < tree.Entries.Count; i++)
            yield return new DirEntry(display[i].Display,
                ToNodeInfo(snapshot, tree.Entries[i], tip.Committer.When));
    }

    // ---------- внутренности ----------

    private static IReadOnlySet<string> BranchNames(RepoSnapshot snapshot)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        foreach (var name in snapshot.Refs.All.Keys)
            if (name.StartsWith(Prefix, StringComparison.Ordinal))
                set.Add(name[Prefix.Length..]);
        return set;
    }

    /// <summary>null — ветка бита: висячий ref, не-коммит, слишком большой
    /// объект. Один битый ref не должен ронять листинг всей вьюхи (ревью M2).</summary>
    private static CommitObject? TipCommit(RepoSnapshot snapshot, string branch)
    {
        if (!snapshot.Refs.TryResolve(Prefix + branch, out var entry)) return null;
        var id = entry.Target;
        if (!snapshot.Objects.TryGetHeader(id, out var type, out _)) return null;
        if (type == GitObjectType.Tag)
        {
            // plumbing позволяет ветку на тег — peel до коммита, как rev-parse
            try { (id, type) = TagObject.Peel(snapshot.Objects, id); }
            catch (Exception e) when (e is InvalidDataException or FileNotFoundException) { return null; }
        }
        if (type != GitObjectType.Commit) return null;
        try
        {
            return CommitObject.Parse(id, snapshot.Objects.ReadAll(id, MaxCommitBytes));
        }
        catch (Exception e) when (e is InvalidDataException or FileNotFoundException)
        {
            return null;
        }
    }

    private DateTimeOffset ViewTimestamp(RepoSnapshot snapshot)
    {
        // дата коммита опорной точки (§3.4); нет HEAD — начало эпохи Unix
        if (snapshot.Refs.HeadTarget is { } head
            && snapshot.Objects.TryGetHeader(head, out var t, out _)
            && t == GitObjectType.Commit)
        {
            return CommitObject.Parse(head, snapshot.Objects.ReadAll(head, MaxCommitBytes))
                .Committer.When;
        }
        return DateTimeOffset.UnixEpoch;
    }

    /// <summary>Спуск по отображаемым именам: на каждом уровне листинг
    /// кодируется NamePolicy и сегмент ищется среди Display-имён.</summary>
    private TreeEntry? ResolveDisplayPath(RepoSnapshot snapshot, ObjectId rootTree,
        IReadOnlyList<string> segments)
    {
        var current = new TreeEntry("", rootTree, GitFileMode.Directory);
        foreach (var segment in segments)
        {
            if (current.Mode != GitFileMode.Directory) return null;
            var tree = TreeObject.Parse(snapshot.Objects.ReadAll(current.Id, MaxTreeBytes));
            var display = _names.EncodeListing(tree.Entries.Select(e => e.Name));
            TreeEntry? next = null;
            for (var i = 0; i < tree.Entries.Count; i++)
            {
                if (string.Equals(display[i].Display, segment, StringComparison.Ordinal))
                {
                    next = tree.Entries[i];
                    break;
                }
            }
            if (next is null) return null;
            current = next.Value;
        }
        return current;
    }

    private static NodeInfo ToNodeInfo(RepoSnapshot snapshot, in TreeEntry entry,
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
                snapshot.Objects.TryGetHeader(entry.Id, out _, out var size);
                return new NodeInfo(NodeKind.Symlink, entry.Id, size, stamp);
            }
            default:
            {
                snapshot.Objects.TryGetHeader(entry.Id, out _, out var size);
                return new NodeInfo(NodeKind.File, entry.Id, size, stamp);
            }
        }
    }
}
