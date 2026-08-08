using Gitfs.Core.Accel;
using Gitfs.Core.Objects;

namespace Gitfs.Core.Walk;

/// <summary>Ленивый обход истории по первому родителю (спека §6.6):
/// history/ и dates/ строятся на нём, лимиты накладывают вьюхи.
///
/// Обход ОСТАНАВЛИВАЕТСЯ на границе доступного, а не падает: у shallow- и
/// partial-клонов граничный коммит ссылается на родителя, которого нет в
/// объектах (ревью M4: без этого три вьюхи из пяти умирали на любом
/// `git clone --depth`).
///
/// При наличии commit-graph (§6.7) шаги по истории читают 36 байт индекса
/// вместо распаковки объекта — на 10 000 коммитов это разница между
/// секундами и миллисекундами.</summary>
public sealed class RevWalker
{
    private const long MaxCommitBytes = 16L << 20;

    private readonly ObjectReader _reader;
    private readonly CommitGraph? _graph;

    public RevWalker(ObjectReader reader, CommitGraph? graph = null)
    {
        _reader = reader;
        _graph = graph;
    }

    public IEnumerable<CommitObject> FirstParent(ObjectId from)
    {
        var current = from;
        while (true)
        {
            var commit = TryRead(current);
            if (commit is null) yield break;
            yield return commit;
            if (commit.Parents.Count == 0) yield break;
            current = commit.Parents[0];
        }
    }

    /// <summary>Шаги по истории без разбора коммитов: отдаёт только то, что
    /// нужно обходу — сам коммит, его дерево и первого родителя. Вьюхи,
    /// которым не нужны автор и сообщение, идут этим путём.</summary>
    public IEnumerable<(ObjectId Id, ObjectId Tree)> FirstParentTrees(ObjectId from)
    {
        var current = from;
        while (true)
        {
            if (_graph is not null && _graph.TryGetCommit(current, out var tree, out var parent))
            {
                yield return (current, tree);
                if (parent is null) yield break;
                current = parent.Value;
                continue;
            }
            var commit = TryRead(current);
            if (commit is null) yield break;
            yield return (commit.Id, commit.Tree);
            if (commit.Parents.Count == 0) yield break;
            current = commit.Parents[0];
        }
    }

    public CommitObject? TryRead(ObjectId id)
    {
        try
        {
            if (!_reader.TryGetHeader(id, out var type, out _) || type != GitObjectType.Commit)
                return null;
            return CommitObject.Parse(id, _reader.ReadAll(id, MaxCommitBytes));
        }
        catch (Exception e) when (e is FileNotFoundException or InvalidDataException)
        {
            return null;
        }
    }
}
