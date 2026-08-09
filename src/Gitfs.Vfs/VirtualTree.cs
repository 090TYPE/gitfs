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
        _views = _ordered.ToDictionary(v => v.Name, StringComparer.OrdinalIgnoreCase);
    }

    public NodeInfo? Resolve(RepoSnapshot snapshot, string path)
    {
        var segments = PathGrammar.Split(path);
        if (segments is null) return null;                       // «.»/«..» — на разборе
        if (segments.Length == 0)
        {
            // §3.4: времена — не косметика; корень — максимум дат корней вьюх
            var stamp = DateTimeOffset.UnixEpoch;
            foreach (var v in _ordered)
                if (v.Resolve(snapshot, Array.Empty<string>()) is { } info && info.Timestamp > stamp)
                    stamp = info.Timestamp;
            return NodeInfo.Directory(stamp);
        }
        if (!_views.TryGetValue(segments[0], out var view)) return null;
        return view.Resolve(snapshot, segments[1..]);
    }

    public IEnumerable<DirEntry>? List(RepoSnapshot snapshot, string path)
    {
        var segments = PathGrammar.Split(path);
        if (segments is null) return null;
        if (segments.Length == 0)
            // дата вьюхи в листинге корня обязана совпадать с её Resolve —
            // иначе readdir показывает 1970, а stat — правильную (ревью M2)
            return _ordered.Select(v => new DirEntry(v.Name,
                v.Resolve(snapshot, Array.Empty<string>())
                    ?? NodeInfo.Directory(DateTimeOffset.UnixEpoch)));
        if (!_views.TryGetValue(segments[0], out var view)) return null;
        return view.List(snapshot, segments[1..]);
    }

    /// <summary>Байты файла, которого нет ни в репозитории, ни на диске
    /// (`.gitfs/status.txt`, `.gitfs/log.txt`). null — обычный путь, читается
    /// как всегда.</summary>
    public byte[]? ReadSynthetic(RepoSnapshot snapshot, string path)
    {
        var segments = PathGrammar.Split(path);
        if (segments is null || segments.Length == 0) return null;
        if (!_views.TryGetValue(segments[0], out var view)) return null;
        return view is ISyntheticView synthetic ? synthetic.Read(snapshot, segments[1..]) : null;
    }

    /// <summary>Путь на диске для узла служебной вьюхи (`.gitfs/overlay/…`).
    /// null — узел не физический.</summary>
    public string? PhysicalPath(RepoSnapshot snapshot, string path)
    {
        var segments = PathGrammar.Split(path);
        if (segments is null || segments.Length == 0) return null;
        if (!_views.TryGetValue(segments[0], out var view)) return null;
        return view is ISyntheticView synthetic
            ? synthetic.PhysicalPath(snapshot, segments[1..]) : null;
    }

    /// <summary>Запрещена ли запись по ЭТОМУ ПУТИ: вся служебная вьюха и
    /// собранные нами маркеры. У диагностики нет режима «изменить».
    ///
    /// Спрашивать про тип вьюхи было нельзя. Синтетику отдаёт база всех вьюх
    /// (маркер сабмодуля), и признак «вьюха умеет синтетику» немедленно стал
    /// верным для каждого пути — записи перестали проходить вообще. Поймали
    /// это одиннадцать чужих тестов про песочницу, а не рассуждение.</summary>
    public bool IsWriteProtected(string path)
    {
        var segments = PathGrammar.Split(path);
        if (segments is null || segments.Length == 0) return false;
        return _views.TryGetValue(segments[0], out var view)
               && view is ISyntheticView synthetic
               && synthetic.IsWriteProtected(segments[1..]);
    }
}
