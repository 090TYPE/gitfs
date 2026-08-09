namespace Gitfs.Vfs;

/// <summary>Роутинг путей по вьюхам (спека §9): первый сегмент выбирает вьюху,
/// остальное делегируется ей. Корень перечисляет вьюхи.</summary>
public sealed class VirtualTree
{
    private readonly Dictionary<string, IView> _views;
    private readonly List<IView> _ordered;
    private readonly string _repositoryName;

    /// <param name="repositoryName">Попадает в autorun.inf как метка тома.
    /// Значение по умолчанию есть, чтобы полторы сотни тестов, которым до
    /// оформления Проводника дела нет, не перечисляли его каждый раз.</param>
    public VirtualTree(IEnumerable<IView> views, string repositoryName = "repository")
    {
        _ordered = views.ToList();
        _views = _ordered.ToDictionary(v => v.Name, StringComparer.OrdinalIgnoreCase);
        _repositoryName = repositoryName;
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
        // autorun.inf в корне тома — иконка самого диска (только Windows)
        if (segments.Length == 1 && Same(segments[0], ShellFolders.AutorunInf)
            && ShellFolders.AutorunFor(_repositoryName) is { } autorun)
        {
            return new NodeInfo(NodeKind.File, default, autorun.LongLength,
                DateTimeOffset.Now, hidden: true, system: true);
        }
        if (!_views.TryGetValue(segments[0], out var view)) return null;

        // desktop.ini в корне вьюхи — оформление папки в Проводнике
        if (segments.Length == 2 && Same(segments[1], ShellFolders.DesktopIni)
            && ShellFolders.DesktopIniFor(view.Name) is { } ini)
        {
            return new NodeInfo(NodeKind.File, default, ini.LongLength,
                DateTimeOffset.Now, hidden: true, system: true);
        }

        var node = view.Resolve(snapshot, segments[1..]);
        // Корень вьюхи помечается системным: без этого атрибута Проводник в
        // desktop.ini даже не заглядывает.
        return segments.Length == 1 && node is { Kind: NodeKind.Directory }
               && ShellFolders.DesktopIniFor(view.Name) is not null
            ? node.Value.AsShellFolder()
            : node;
    }

    private static bool Same(string a, string b) =>
        string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    public IEnumerable<DirEntry>? List(RepoSnapshot snapshot, string path)
    {
        var segments = PathGrammar.Split(path);
        if (segments is null) return null;
        if (segments.Length == 0) return ListRoot(snapshot);
        if (!_views.TryGetValue(segments[0], out var view)) return null;

        var listed = view.List(snapshot, segments[1..]);
        if (segments.Length != 1) return listed;
        if (ShellFolders.DesktopIniFor(view.Name) is not { } ini) return listed;
        // desktop.ini лежит В корне вьюхи, значит и в листинге он оттуда же
        return listed.Append(new DirEntry(ShellFolders.DesktopIni,
            new NodeInfo(NodeKind.File, default, ini.LongLength, DateTimeOffset.Now,
                hidden: true, system: true)));
    }

    private IEnumerable<DirEntry> ListRoot(RepoSnapshot snapshot)
    {
        foreach (var v in _ordered)
        {
            // дата вьюхи в листинге корня обязана совпадать с её Resolve —
            // иначе readdir показывает 1970, а stat — правильную (ревью M2)
            var info = v.Resolve(snapshot, Array.Empty<string>())
                       ?? NodeInfo.Directory(DateTimeOffset.UnixEpoch);
            if (ShellFolders.DesktopIniFor(v.Name) is not null) info = info.AsShellFolder();
            yield return new DirEntry(v.Name, info);
        }
        if (ShellFolders.AutorunFor(_repositoryName) is { } autorun)
        {
            yield return new DirEntry(ShellFolders.AutorunInf,
                new NodeInfo(NodeKind.File, default, autorun.LongLength, DateTimeOffset.Now,
                    hidden: true, system: true));
        }
    }

    /// <summary>Байты файла, которого нет ни в репозитории, ни на диске
    /// (`.gitfs/status.txt`, `.gitfs/log.txt`). null — обычный путь, читается
    /// как всегда.</summary>
    public byte[]? ReadSynthetic(RepoSnapshot snapshot, string path)
    {
        var segments = PathGrammar.Split(path);
        if (segments is null || segments.Length == 0) return null;
        if (segments.Length == 1 && Same(segments[0], ShellFolders.AutorunInf))
            return ShellFolders.AutorunFor(_repositoryName);
        if (!_views.TryGetValue(segments[0], out var view)) return null;
        if (segments.Length == 2 && Same(segments[1], ShellFolders.DesktopIni))
            return ShellFolders.DesktopIniFor(view.Name);
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
