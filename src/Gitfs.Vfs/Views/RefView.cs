using Gitfs.Core;

namespace Gitfs.Vfs.Views;

/// <summary>Общая вьюха для ссылок: branches/ и tags/. Слэш в имени ссылки
/// становится вложенными директориями; граница имени — наибольшее совпадение
/// по списку ссылок снапшота (спека §4).</summary>
public abstract class RefView : ViewBase
{
    protected RefView(NamePolicy names) : base(names) { }

    protected abstract string RefPrefix { get; }

    protected HashSet<string> RefNames(RepoSnapshot snapshot)
    {
        var set = new HashSet<string>(Names.Comparer);
        foreach (var name in snapshot.Refs.All.Keys)
            if (name.StartsWith(RefPrefix, StringComparison.Ordinal))
                set.Add(name[RefPrefix.Length..]);
        return set;
    }

    /// <summary>Первые skip сегментов адресуют ссылку — значит и коммит.
    /// Нужно базе, чтобы найти gitlink под путём и отдать маркер сабмодуля.</summary>
    protected override CommitObject? CommitAt(RepoSnapshot snapshot,
        IReadOnlyList<string> segments, int skip)
    {
        if (skip == 0) return null;
        return TipOf(snapshot, RefPrefix + string.Join('/', segments.Take(skip)));
    }

    public override NodeInfo? Resolve(RepoSnapshot snapshot, IReadOnlyList<string> segments)
    {
        if (segments.Count == 0) return NodeInfo.Directory(ViewTimestamp(snapshot));

        var names = RefNames(snapshot);
        var array = segments as string[] ?? segments.ToArray();
        var match = PathGrammar.MatchLongestRef(names, array);
        if (match is null)
            return PathGrammar.IsRefPrefix(names, array, Names.Comparison)
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
            if (array.Length != 0 && !PathGrammar.IsRefPrefix(names, array, Names.Comparison)) yield break;
            // корень вьюхи или промежуточный сегмент имени ссылки
            var prefix = array.Length == 0 ? "" : string.Join('/', array) + "/";
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var stamp = ViewTimestamp(snapshot);
            foreach (var refName in names.Order(StringComparer.Ordinal))
            {
                if (!refName.StartsWith(prefix, Names.Comparison)) continue;
                var rest = refName[prefix.Length..];
                var slash = rest.IndexOf('/');
                var head = slash < 0 ? rest : rest[..slash];
                if (seen.Contains(head)) continue;
                DateTimeOffset when;
                if (slash < 0)
                {
                    // битая ссылка не резолвится — не показываем и в листинге,
                    // иначе List и Resolve расходятся на одном имени (ревью M4)
                    var tipCommit = TipOf(snapshot, RefPrefix + refName);
                    if (tipCommit is null) continue;
                    when = tipCommit.Committer.When;
                }
                else
                {
                    when = stamp;
                }
                seen.Add(head);
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
