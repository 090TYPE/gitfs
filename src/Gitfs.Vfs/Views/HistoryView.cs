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
/// OID блоба отличается от предыдущего. Удаление разрывает цепочку —
/// файл, воссозданный с прежним содержимым, даёт новую ревизию
/// (ревью M4: иначе история утверждала бы, что версия появилась раньше).</summary>
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

    public sealed record Revision(int Ordinal, ObjectId Blob, CommitObject Commit, long Size);

    public sealed record PathHistory(IReadOnlyList<Revision> Revisions, bool Truncated);

    public override NodeInfo? Resolve(RepoSnapshot snapshot, IReadOnlyList<string> segments)
    {
        if (segments.Count == 0) return NodeInfo.Directory(ViewTimestamp(snapshot));

        // §4: последний сегмент — файл версии тогда и только тогда, когда
        // соответствует шаблону И родительский путь существует в истории как
        // файл. Это допускает файл с именем 0001-abcdef.cs в самом
        // репозитории: его родитель — директория, а не файл.
        if (segments.Count >= 2)
        {
            var parent = segments.Take(segments.Count - 1).ToArray();
            var history = HistoryOf(snapshot, parent);
            if (history is not null)
            {
                var leaf = segments[^1];
                if (string.Equals(leaf, TruncatedMarker, Names.Comparison))
                    return history.Truncated
                        ? new NodeInfo(NodeKind.File, default, 0, ViewTimestamp(snapshot))
                        : null;
                var revision = MatchVersion(history, parent[^1], leaf, Names.Comparison);
                if (revision is not null)
                    return new NodeInfo(NodeKind.File, revision.Blob, revision.Size,
                        revision.Commit.Committer.When);
                // не версия — путь может быть настоящей записью дерева
                // (узел был и файлом, и директорией, §3.1)
            }
        }

        if (HistoryOf(snapshot, segments) is { } own)
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

        var emitted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (segments.Count > 0 && HistoryOf(snapshot, segments) is { } history)
        {
            // ЭТО ОНО: файл раскрылся папкой своих версий
            var extension = VersionExtension(segments[^1]);
            foreach (var revision in history.Revisions)
            {
                var name = $"{revision.Ordinal:0000}-{revision.Blob.ToString()[..7]}{extension}";
                emitted.Add(name);
                yield return new DirEntry(name,
                    new NodeInfo(NodeKind.File, revision.Blob, revision.Size,
                        revision.Commit.Committer.When));
            }
            if (history.Revisions.Count > 0)
            {
                var latest = LatestName + extension;
                emitted.Add(latest);
                yield return new DirEntry(latest,
                    new NodeInfo(NodeKind.File, history.Revisions[0].Blob,
                        history.Revisions[0].Size, history.Revisions[0].Commit.Committer.When));
            }
            if (history.Truncated)
            {
                // молчаливое усечение недопустимо (§3.1)
                emitted.Add(TruncatedMarker);
                yield return new DirEntry(TruncatedMarker,
                    new NodeInfo(NodeKind.File, default, 0, ViewTimestamp(snapshot)));
            }
            // §3.1: узел, который был и файлом, и директорией, показывает
            // и версии, и дочерние записи — падаем ниже, а не выходим
        }

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
        // директории остаются директориями, файлы показываются папками версий
        foreach (var e in ListTree(snapshot, treeId, head.Committer.When))
        {
            if (!emitted.Add(e.Name)) continue; // коллизия с именем версии
            yield return e.Info.Kind == NodeKind.File
                ? new DirEntry(e.Name, NodeInfo.Directory(e.Info.Timestamp))
                : e;
        }
    }

    // ---------- построение истории пути ----------

    /// <summary>История пути через кэш снапшота: снапшот иммутабелен, поэтому
    /// инвалидация не нужна. Без кэша адаптер платил полный обход истории
    /// на КАЖДЫЙ Lookup/Open/ReadDirectory (ревью M4: 2.4 с на 300 коммитах).</summary>
    private PathHistory? HistoryOf(RepoSnapshot snapshot, IReadOnlyList<string> displaySegments)
    {
        var key = Name + "/" + string.Join('/', displaySegments);
        if (snapshot.HistoryCache.TryGet(key, out var cached)) return cached;
        var built = BuildHistory(snapshot, displaySegments);
        snapshot.HistoryCache.Set(key, built!);
        return built;
    }

    /// <summary>null — путь не является файлом в опорной истории.</summary>
    private PathHistory? BuildHistory(RepoSnapshot snapshot, IReadOnlyList<string> displaySegments)
    {
        var head = HeadCommit(snapshot);
        if (head is null) return null;

        // путь должен существовать в опорной точке как настоящий файл:
        // симлинк и гитлинк историей версий не раскрываются (ревью M4)
        var current = ResolveDisplayPath(snapshot, head.Tree, displaySegments);
        if (current is null || !IsVersionableFile(current.Value.Mode)) return null;

        var revisions = new List<Revision>();
        ObjectId? previous = null;
        var scanned = 0;
        var truncated = false;
        var stoppedByLimit = false;
        ObjectId? previousTree = null;

        // Обход берёт только идентификатор и дерево: автор и сообщение здесь
        // не нужны, а через commit-graph это чтение индекса вместо распаковки.
        ObjectId? lastId = null;
        foreach (var (commitId, commitTree) in snapshot.Revs.FirstParentTrees(head.Id))
        {
            lastId = commitId;
            if (scanned++ >= _scanLimit) { truncated = true; stoppedByLimit = true; break; }

            // Дерево коммита не изменилось — значит и путь в нём тот же.
            // Подавляющее большинство коммитов не трогают конкретный файл,
            // и этот выход убирает спуск по дереву целиком.
            if (previousTree is { } prevTree && prevTree == commitTree) continue;
            previousTree = commitTree;

            var entry = ResolveDisplayPath(snapshot, commitTree, displaySegments);
            if (entry is null || !IsVersionableFile(entry.Value.Mode))
            {
                previous = null; // разрыв присутствия: дальше — новая жизнь файла
                continue;
            }
            if (previous is { } prev && prev == entry.Value.Id) continue; // не менялся

            previous = entry.Value.Id;
            // коммит разбирается только ради даты ревизии — а таких коммитов
            // единицы против тысяч просмотренных
            var commit = snapshot.Revs.TryRead(commitId);
            if (commit is null) continue;
            snapshot.Objects.TryGetHeader(entry.Value.Id, out _, out var size);
            revisions.Add(new Revision(revisions.Count + 1, entry.Value.Id, commit, size));
            if (revisions.Count >= _limit) { truncated = true; stoppedByLimit = true; break; }
        }

        // обход закончился не по нашему лимиту, а у коммита с родителем —
        // значит родитель недоступен (shallow/partial-клон), история оборвана
        if (!stoppedByLimit && lastId is { } tail
            && snapshot.Revs.TryRead(tail) is { Parents.Count: > 0 })
            truncated = true;

        return new PathHistory(revisions, truncated);
    }

    private static bool IsVersionableFile(GitFileMode mode) =>
        mode is GitFileMode.RegularFile or GitFileMode.ExecutableFile;

    /// <summary>Расширение версии — последнее расширение имени; для составных
    /// имён вроде archive.tar.gz оно и есть «.gz», как у самого файла.</summary>
    private static string VersionExtension(string fileName) => Path.GetExtension(fileName);

    /// <summary>Строгая грамматика §4: NNNN — ровно цифры, sha — hex длиной
    /// от 7, расширение обязано совпасть с расширением исходного имени.
    /// Ревью M4: раньше резолвились фантомы «0001-.txt», «+001-…», «latest.exe».</summary>
    internal static Revision? MatchVersion(PathHistory history, string fileName, string leaf,
        StringComparison comparison = StringComparison.Ordinal)
    {
        var extension = VersionExtension(fileName);
        if (!leaf.EndsWith(extension, comparison)) return null;
        var name = leaf[..^extension.Length];

        if (string.Equals(name, LatestName, comparison))
            return history.Revisions.Count > 0 ? history.Revisions[0] : null;

        var dash = name.IndexOf('-');
        if (dash < 4) return null;
        for (var i = 0; i < dash; i++)
            if (!char.IsAsciiDigit(name[i]))
                return null;
        if (!int.TryParse(name[..dash], System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out var ordinal))
            return null;

        var sha = name[(dash + 1)..];
        if (sha.Length < 7 || sha.Length > ObjectId.HexLength) return null;
        foreach (var c in sha)
            if (!Uri.IsHexDigit(c))
                return null;

        return history.Revisions.FirstOrDefault(r =>
            r.Ordinal == ordinal && r.Blob.ToString().StartsWith(sha, StringComparison.OrdinalIgnoreCase));
    }
}
