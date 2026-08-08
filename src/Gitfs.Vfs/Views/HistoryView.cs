using Gitfs.Core;

namespace Gitfs.Vfs.Views;

/// <summary>history/&lt;путь&gt;/ — ФАЙЛ СТАНОВИТСЯ ПАПКОЙ со всеми своими
/// версиями. Фирменная особенность продукта (спека §3.1).
///
/// Имена версий: NNNN-&lt;short-sha&gt;&lt;расширение&gt;. Номер спереди —
/// сортировка по имени в Проводнике совпадает с хронологией (0001 —
/// самая свежая); short-sha копируется в git show; расширение сохранено,
/// поэтому работают подсветка синтаксиса и файловые ассоциации.
/// Плюс latest.&lt;ext&gt; — версия из опорной точки.
///
/// В папку попадают только коммиты, где файл ДЕЙСТВИТЕЛЬНО менялся:
/// OID блоба отличается от предыдущего рассмотренного.</summary>
public sealed class HistoryView : ViewBase
{
    public const string LatestName = "latest";
    public const string TruncatedMarker = ".truncated";

    private readonly int _limit;
    private readonly int _scanLimit;

    public HistoryView(NamePolicy names, int limit = 500, int scanLimit = 20000)
        : base(names)
    {
        _limit = limit;
        _scanLimit = scanLimit;
    }

    public override string Name => "history";

    private sealed record Revision(int Ordinal, ObjectId Blob, CommitObject Commit, long Size);

    private sealed record PathHistory(IReadOnlyList<Revision> Revisions, bool Truncated);

    public override NodeInfo? Resolve(RepoSnapshot snapshot, IReadOnlyList<string> segments)
    {
        if (segments.Count == 0) return NodeInfo.Directory(ViewTimestamp(snapshot));

        // §4: последний сегмент — файл версии тогда и только тогда, когда
        // соответствует шаблону И родительский путь существует в истории как
        // файл. Это допускает файл с именем вида 0001-abcdef.cs в самом
        // репозитории: его родитель — директория, а не файл.
        if (segments.Count >= 2)
        {
            var parent = segments.Take(segments.Count - 1).ToArray();
            var history = TryBuildHistory(snapshot, parent);
            if (history is not null)
            {
                var leaf = segments[^1];
                if (leaf == TruncatedMarker)
                    return history.Truncated
                        ? new NodeInfo(NodeKind.File, default, 0, ViewTimestamp(snapshot))
                        : null;
                var revision = MatchVersion(history, leaf);
                return revision is null
                    ? null
                    : new NodeInfo(NodeKind.File, revision.Blob, revision.Size,
                        revision.Commit.Committer.When);
            }
        }

        // сам путь: файл в истории — директория версий; директория — обычная
        if (TryBuildHistory(snapshot, segments) is { } own)
            return NodeInfo.Directory(own.Revisions.Count > 0
                ? own.Revisions[0].Commit.Committer.When
                : ViewTimestamp(snapshot));

        var head = HeadCommit(snapshot);
        if (head is null) return null;
        var entry = ResolveDisplayPath(snapshot, head.Tree, segments);
        return entry is { Mode: GitFileMode.Directory }
            ? NodeInfo.Directory(head.Committer.When)
            : null;
    }

    public override IEnumerable<DirEntry> List(RepoSnapshot snapshot, IReadOnlyList<string> segments)
    {
        var head = HeadCommit(snapshot);
        if (head is null) yield break;

        if (segments.Count > 0 && TryBuildHistory(snapshot, segments) is { } history)
        {
            // ЭТО ОНО: файл раскрылся папкой своих версий
            var extension = Path.GetExtension(segments[^1]);
            foreach (var revision in history.Revisions)
                yield return new DirEntry(
                    $"{revision.Ordinal:0000}-{revision.Blob.ToString()[..7]}{extension}",
                    new NodeInfo(NodeKind.File, revision.Blob, revision.Size,
                        revision.Commit.Committer.When));
            if (history.Revisions.Count > 0)
                yield return new DirEntry(LatestName + extension,
                    new NodeInfo(NodeKind.File, history.Revisions[0].Blob,
                        history.Revisions[0].Size, history.Revisions[0].Commit.Committer.When));
            if (history.Truncated)
                // молчаливое усечение недопустимо (§3.1)
                yield return new DirEntry(TruncatedMarker,
                    new NodeInfo(NodeKind.File, default, 0, ViewTimestamp(snapshot)));
            yield break;
        }

        // обычная директория дерева опорной точки
        ObjectId treeId;
        if (segments.Count == 0)
        {
            treeId = head.Tree;
        }
        else
        {
            var entry = ResolveDisplayPath(snapshot, head.Tree, segments);
            if (entry is not { Mode: GitFileMode.Directory }) yield break;
            treeId = entry.Value.Id;
        }
        // директории остаются директориями, а файлы показываются как папки версий
        foreach (var e in ListTree(snapshot, treeId, head.Committer.When))
            yield return e.Info.Kind == NodeKind.File
                ? new DirEntry(e.Name, NodeInfo.Directory(e.Info.Timestamp))
                : e;
    }

    // ---------- построение истории пути ----------

    /// <summary>null — путь не является файлом в опорной истории.</summary>
    private PathHistory? TryBuildHistory(RepoSnapshot snapshot, IReadOnlyList<string> displaySegments)
    {
        var head = HeadCommit(snapshot);
        if (head is null) return null;

        // путь должен существовать в опорной точке как файл
        var current = ResolveDisplayPath(snapshot, head.Tree, displaySegments);
        if (current is null || current.Value.Mode == GitFileMode.Directory) return null;

        var revisions = new List<Revision>();
        ObjectId? previous = null;
        var scanned = 0;
        var truncated = false;

        foreach (var commit in snapshot.Revs.FirstParent(head.Id))
        {
            if (scanned++ >= _scanLimit) { truncated = true; break; }
            var entry = ResolveDisplayPath(snapshot, commit.Tree, displaySegments);
            if (entry is null) continue;               // файла тогда ещё не было
            if (entry.Value.Mode == GitFileMode.Directory) continue;
            if (previous is { } prev && prev == entry.Value.Id) continue; // не менялся

            previous = entry.Value.Id;
            snapshot.Objects.TryGetHeader(entry.Value.Id, out _, out var size);
            revisions.Add(new Revision(revisions.Count + 1, entry.Value.Id, commit, size));
            if (revisions.Count >= _limit) { truncated = true; break; }
        }
        return new PathHistory(revisions, truncated);
    }

    private static Revision? MatchVersion(PathHistory history, string leaf)
    {
        var name = Path.GetFileNameWithoutExtension(leaf);
        if (string.Equals(name, LatestName, StringComparison.Ordinal))
            return history.Revisions.Count > 0 ? history.Revisions[0] : null;

        var dash = name.IndexOf('-');
        if (dash != 4 || !int.TryParse(name[..dash], out var ordinal)) return null;
        var sha = name[(dash + 1)..];
        return history.Revisions.FirstOrDefault(r =>
            r.Ordinal == ordinal && r.Blob.ToString().StartsWith(sha, StringComparison.Ordinal));
    }
}
