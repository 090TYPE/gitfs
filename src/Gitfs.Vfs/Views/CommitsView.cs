using Gitfs.Core;

namespace Gitfs.Vfs.Views;

/// <summary>commits/&lt;sha&gt;/… — снимок дерева любого коммита.
///
/// Принцип §3.2, снимающий проблему объёма: ЛИСТИНГ и РЕЗОЛВ — разные
/// операции. Перечисляются последние N коммитов от опорной точки, но путь
/// открывается для ЛЮБОГО валидного OID, даже если его не было в листинге.
/// Модель заимствована у /proc: видно не всё, доступно всё.</summary>
public sealed class CommitsView : ViewBase
{
    public const int MinPrefixLength = 7;

    private readonly int _limit;

    public CommitsView(NamePolicy names, int limit = 200) : base(names) => _limit = limit;

    public override string Name => "commits";

    /// <summary>Коммит адресует ровно первый сегмент.</summary>
    protected override CommitObject? CommitAt(RepoSnapshot snapshot,
        IReadOnlyList<string> segments, int skip) =>
        skip == 1 ? FindCommit(snapshot, segments[0]) : null;

    public override NodeInfo? Resolve(RepoSnapshot snapshot, IReadOnlyList<string> segments)
    {
        if (segments.Count == 0) return NodeInfo.Directory(ViewTimestamp(snapshot));
        var commit = FindCommit(snapshot, segments[0]);
        return commit is null ? null : ResolveInCommit(snapshot, commit, segments, 1);
    }

    public override IEnumerable<DirEntry> List(RepoSnapshot snapshot, IReadOnlyList<string> segments)
    {
        if (segments.Count == 0)
        {
            foreach (var commit in RecentCommits(snapshot))
                yield return new DirEntry(ShortName(commit.Id),
                    NodeInfo.Directory(commit.Committer.When));
            yield break;
        }
        var target = FindCommit(snapshot, segments[0]);
        if (target is null) yield break;
        foreach (var e in ListInCommit(snapshot, target, segments, 1)) yield return e;
    }

    private IEnumerable<CommitObject> RecentCommits(RepoSnapshot snapshot)
    {
        var head = HeadCommit(snapshot);
        if (head is null) yield break;
        var count = 0;
        foreach (var commit in snapshot.Revs.FirstParent(head.Id))
        {
            if (count++ >= _limit) yield break;
            yield return commit;
        }
    }

    private static string ShortName(in ObjectId id) => id.ToString()[..7];

    /// <summary>Полный OID — прямой резолв; префикс от 7 символов ищется
    /// среди объектов паков и loose. Неоднозначный префикс — null
    /// (спека §4: молча выбирать первый нельзя).</summary>
    private static CommitObject? FindCommit(RepoSnapshot snapshot, string spec)
    {
        if (ObjectId.TryParse(spec, out var exact)) return TryCommit(snapshot, exact);
        if (spec.Length < MinPrefixLength || spec.Length >= ObjectId.HexLength) return null;
        foreach (var c in spec)
            if (!Uri.IsHexDigit(c)) return null;

        var match = snapshot.Objects.FindByPrefix(spec.ToLowerInvariant());
        return match is null ? null : TryCommit(snapshot, match.Value);
    }
}
