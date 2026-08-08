using Gitfs.Core.Objects;

namespace Gitfs.Core.Walk;

/// <summary>Ленивый обход истории по первому родителю (спека §6.6):
/// history/ и dates/ строятся на нём, лимиты накладывают вьюхи.
///
/// Обход ОСТАНАВЛИВАЕТСЯ на границе доступного, а не падает: у shallow- и
/// partial-клонов граничный коммит ссылается на родителя, которого нет в
/// объектах. Ревью M4: без этого три вьюхи из пяти умирали на любом
/// `git clone --depth`.</summary>
public sealed class RevWalker
{
    private const long MaxCommitBytes = 16L << 20;

    private readonly ObjectReader _reader;

    public RevWalker(ObjectReader reader) => _reader = reader;

    /// <summary>Истина, если обход оборвался раньше корня истории — вызывающий
    /// может показать это пользователю (маркер .truncated).</summary>
    public bool TryWalkFirstParent(ObjectId from, int limit, out List<CommitObject> commits,
        out bool truncated)
    {
        commits = new List<CommitObject>();
        truncated = false;
        var current = from;
        while (commits.Count < limit)
        {
            var commit = TryRead(current);
            if (commit is null)
            {
                // граница shallow-клона или битый объект: то, что уже собрано,
                // остаётся валидным — сообщаем об обрыве
                truncated = commits.Count > 0 || !current.Equals(from);
                return commits.Count > 0;
            }
            commits.Add(commit);
            if (commit.Parents.Count == 0) return true;
            current = commit.Parents[0];
        }
        truncated = true;
        return true;
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

    private CommitObject? TryRead(ObjectId id)
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
