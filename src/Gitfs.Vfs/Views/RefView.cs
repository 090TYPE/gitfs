using Gitfs.Core;

namespace Gitfs.Vfs.Views;

/// <summary>Общая вьюха для ссылок: branches/ и tags/. Слэш в имени ссылки
/// становится вложенными директориями; граница имени — наибольшее совпадение
/// по списку ссылок снапшота (спека §4).</summary>
public abstract class RefView : ViewBase
{
    protected RefView(NamePolicy names) : base(names) { }

    protected abstract string RefPrefix { get; }

    protected IReadOnlySet<string> RefNames(RepoSnapshot snapshot)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        foreach (var name in snapshot.Refs.All.Keys)
            if (name.StartsWith(RefPrefix, StringComparison.Ordinal))
                set.Add(name[RefPrefix.Length..]);
        return set;
    }

    public override NodeInfo? Resolve(RepoSnapshot snapshot, IReadOnlyList<string> segments)
    {
        if (segments.Count == 0) return NodeInfo.Directory(ViewTimestamp(snapshot));

        var names = RefNames(snapshot);
        var array = segments as string[] ?? segments.ToArray();
        var match = PathGrammar.MatchLongestRef(names, array);
        if (match is null)
            return PathGrammar.IsRefPrefix(names, array)
                ? NodeInfo.Directory(ViewTimestamp(snapshot))
                : null;

        var (refName, tail) = match.Value;
        var tip = TipOf(snapshot, RefPrefix + refName);
        if (tip is null) return null;
        return ResolveInCommit(snapshot, tip, array, array.Length - tail.Count);
    }

    public override IEnumerable<DirEntry> List(RepoSnapshot snapshot, IReadOnlyList<string> segments)
    {
        var names = RefNames(snapshot);
        var array = segments as string[] ?? segments.ToArray();
        var match = PathGrammar.MatchLongestRef(names, array);

        if (match is null)
        {
            if (array.Length != 0 && !PathGrammar.IsRefPrefix(names, array)) yield break;
            // корень вьюхи или промежуточный сегмент имени ссылки
            var prefix = array.Length == 0 ? "" : string.Join('/', array) + "/";
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var stamp = ViewTimestamp(snapshot);
            foreach (var refName in names.Order(StringComparer.Ordinal))
            {
                if (!refName.StartsWith(prefix, StringComparison.Ordinal)) continue;
                var rest = refName[prefix.Length..];
                var slash = rest.IndexOf('/');
                var head = slash < 0 ? rest : rest[..slash];
                if (!seen.Add(head)) continue;
                var when = slash < 0
                    ? TipOf(snapshot, RefPrefix + refName)?.Committer.When ?? stamp
                    : stamp;
                yield return new DirEntry(head, NodeInfo.Directory(when));
            }
            yield break;
        }

        var (name, tail) = match.Value;
        var tip = TipOf(snapshot, RefPrefix + name);
        if (tip is null) yield break;
        foreach (var e in ListInCommit(snapshot, tip, array, array.Length - tail.Count))
            yield return e;
    }
}

/// <summary>branches/&lt;ветка&gt;/… — рабочее дерево каждой ветки.</summary>
public sealed class BranchesView : RefView
{
    public BranchesView(NamePolicy names) : base(names) { }
    public override string Name => "branches";
    protected override string RefPrefix => "refs/heads/";
}

/// <summary>tags/&lt;тег&gt;/… — выпущенные версии. Аннотированные теги
/// разыменовываются до коммита: показывается дерево, а не объект тега (§6.5).</summary>
public sealed class TagsView : RefView
{
    public TagsView(NamePolicy names) : base(names) { }
    public override string Name => "tags";
    protected override string RefPrefix => "refs/tags/";
}
