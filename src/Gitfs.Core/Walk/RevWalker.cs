using Gitfs.Core.Objects;

namespace Gitfs.Core.Walk;

/// <summary>Ленивый обход истории по первому родителю (спека §6.6):
/// history/ и dates/ строятся на нём, лимиты накладывают вьюхи.</summary>
public sealed class RevWalker
{
    private const long MaxCommitBytes = 16L << 20;

    private readonly ObjectReader _reader;

    public RevWalker(ObjectReader reader) => _reader = reader;

    public IEnumerable<CommitObject> FirstParent(ObjectId from)
    {
        var current = from;
        while (true)
        {
            var commit = CommitObject.Parse(current, _reader.ReadAll(current, MaxCommitBytes));
            yield return commit;
            if (commit.Parents.Count == 0) yield break;
            current = commit.Parents[0];
        }
    }
}
