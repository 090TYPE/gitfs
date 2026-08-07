namespace Gitfs.Vfs;

/// <summary>Роутинг путей по вьюхам (спека §9): первый сегмент выбирает вьюху,
/// остальное делегируется ей. Корень перечисляет вьюхи.</summary>
public sealed class VirtualTree
{
    private readonly Dictionary<string, IView> _views;
    private readonly List<IView> _ordered;

    public VirtualTree(IEnumerable<IView> views)
    {
        _ordered = views.ToList();
        _views = _ordered.ToDictionary(v => v.Name, StringComparer.Ordinal);
    }

    public NodeInfo? Resolve(RepoSnapshot snapshot, string path)
    {
        var segments = PathGrammar.Split(path);
        if (segments is null) return null;                       // «.»/«..» — на разборе
        if (segments.Length == 0) return NodeInfo.Directory(DateTimeOffset.UnixEpoch);
        if (!_views.TryGetValue(segments[0], out var view)) return null;
        return view.Resolve(snapshot, segments[1..]);
    }

    public IEnumerable<DirEntry>? List(RepoSnapshot snapshot, string path)
    {
        var segments = PathGrammar.Split(path);
        if (segments is null) return null;
        if (segments.Length == 0)
            return _ordered.Select(v =>
                new DirEntry(v.Name, NodeInfo.Directory(DateTimeOffset.UnixEpoch)));
        if (!_views.TryGetValue(segments[0], out var view)) return null;
        return view.List(snapshot, segments[1..]);
    }
}
