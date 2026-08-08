using System.Globalization;
using Gitfs.Core;

namespace Gitfs.Vfs.Views;

/// <summary>dates/YYYY-MM-DD/… — состояние проекта на конец дня.
/// Перечисляются только дни, в которые был хотя бы один коммит; обход —
/// по первому родителю от опорной точки (спека §3.1).</summary>
public sealed class DatesView : ViewBase
{
    public const string DateFormat = "yyyy-MM-dd";

    private readonly int _limit;

    public DatesView(NamePolicy names, int limit = 2000) : base(names) => _limit = limit;

    public override string Name => "dates";

    public override NodeInfo? Resolve(RepoSnapshot snapshot, IReadOnlyList<string> segments)
    {
        if (segments.Count == 0) return NodeInfo.Directory(ViewTimestamp(snapshot));
        var commit = CommitOfDay(snapshot, segments[0]);
        return commit is null ? null : ResolveInCommit(snapshot, commit, segments, 1);
    }

    public override IEnumerable<DirEntry> List(RepoSnapshot snapshot, IReadOnlyList<string> segments)
    {
        if (segments.Count == 0)
        {
            foreach (var (day, commit) in DayIndex(snapshot).OrderBy(p => p.Key, StringComparer.Ordinal))
                yield return new DirEntry(day, NodeInfo.Directory(commit.Committer.When));
            yield break;
        }
        var target = CommitOfDay(snapshot, segments[0]);
        if (target is null) yield break;
        foreach (var e in ListInCommit(snapshot, target, segments, 1)) yield return e;
    }

    /// <summary>День → последний коммит этого дня (состояние на конец дня).
    /// Обход идёт от свежего к старому, поэтому первый встреченный коммит
    /// дня и есть последний по времени.
    ///
    /// День берётся в UTC: иначе коммиты одного момента, сделанные людьми в
    /// разных поясах, попадали бы в разные дни, и дерево зависело бы от того,
    /// у кого какой ноутбук (ревью M4). Кэшируется в снапшоте — без кэша
    /// каждый stat стоил полный обход истории.</summary>
    private IReadOnlyDictionary<string, CommitObject> DayIndex(RepoSnapshot snapshot)
    {
        const string key = "dates";
        if (snapshot.DateIndexCache.TryGet(key, out var cached)) return cached;

        var index = new Dictionary<string, CommitObject>(StringComparer.Ordinal);
        var head = HeadCommit(snapshot);
        if (head is not null)
        {
            var seen = 0;
            foreach (var commit in snapshot.Revs.FirstParent(head.Id))
            {
                if (seen++ >= _limit) break;
                var day = commit.Committer.When.UtcDateTime
                    .ToString(DateFormat, CultureInfo.InvariantCulture);
                index.TryAdd(day, commit);
            }
        }
        snapshot.DateIndexCache.Set(key, index);
        return index;
    }

    private CommitObject? CommitOfDay(RepoSnapshot snapshot, string day)
    {
        if (!DateOnly.TryParseExact(day, DateFormat, CultureInfo.InvariantCulture,
                DateTimeStyles.None, out _))
            return null;
        return DayIndex(snapshot).GetValueOrDefault(day);
    }
}
