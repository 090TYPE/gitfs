using Gitfs.Core;

namespace Gitfs.Vfs.Views;

/// <summary>Общая механика вьюх: чтение деревьев и коммитов через кэши
/// снапшота, спуск по ОТОБРАЖАЕМЫМ именам (иначе не резолвятся readme~2 и
/// aux%RES.c), перевод записи дерева в узел.</summary>
public abstract class ViewBase : IView, ISyntheticView
{
    /// <summary>Спека §3: gitlink — «пустая директория с файлом-маркером
    /// .gitfs-submodule, внутри — OID и путь».
    ///
    /// Без него сабмодуль выглядел папкой, которая не открывается: щёлкнув по
    /// ней, человек получал пустоту и никакого объяснения. Папка с одним
    /// файлом объясняет себя сама.</summary>
    public const string SubmoduleMarker = ".gitfs-submodule";

    protected static byte[] SubmoduleText(in ObjectId commit, string path) =>
        System.Text.Encoding.UTF8.GetBytes(
            $"submodule{Environment.NewLine}path = {path}{Environment.NewLine}" +
            $"commit = {commit}{Environment.NewLine}" +
            $"gitfs does not clone submodules; open that repository separately" +
            Environment.NewLine);

    protected const long MaxTreeBytes = 64L << 20;
    protected const long MaxCommitBytes = 16L << 20;

    protected NamePolicy Names { get; }

    protected ViewBase(NamePolicy names) => Names = names;

    public abstract string Name { get; }
    public abstract NodeInfo? Resolve(RepoSnapshot snapshot, IReadOnlyList<string> segments);
    public abstract IEnumerable<DirEntry> List(RepoSnapshot snapshot, IReadOnlyList<string> segments);

    // ---------- кэшированные чтения ----------

    protected static TreeObject ReadTree(RepoSnapshot snapshot, in ObjectId id)
    {
        if (snapshot.TreeCache.TryGet(id, out var cached)) return cached;
        var tree = TreeObject.Parse(snapshot.Objects.ReadAll(id, MaxTreeBytes));
        snapshot.TreeCache.Set(id, tree);
        return tree;
    }

    protected IReadOnlyList<DisplayName> DisplayNames(RepoSnapshot snapshot, in ObjectId treeId,
        TreeObject tree)
    {
        var key = (treeId, Names.Tag);
        if (snapshot.ListingCache.TryGet(key, out var cached)) return cached;
        var display = Names.EncodeListing(tree.Entries.Select(e => e.Name));
        snapshot.ListingCache.Set(key, display);
        return display;
    }

    /// <summary>Коммит по OID; null — объекта нет, он не коммит или битый.
    /// Ни при каких обстоятельствах не бросает — один битый ref не должен
    /// ронять перечисление всей вьюхи (ревью M2).</summary>
    protected static CommitObject? TryCommit(RepoSnapshot snapshot, ObjectId id)
    {
        try
        {
            if (!snapshot.Objects.TryGetHeader(id, out var type, out _)) return null;
            if (type == GitObjectType.Tag)
            {
                (id, type) = TagObject.Peel(snapshot.Objects, id);
            }
            if (type != GitObjectType.Commit) return null;
            return CommitObject.Parse(id, snapshot.Objects.ReadAll(id, MaxCommitBytes));
        }
        // ловим ЛЮБОЙ отказ чтения: обещание докстринга — «не бросает никогда»,
        // а битый пак умеет кидать и IOException, и ArgumentException (ревью M4)
        catch (Exception e) when (e is not OutOfMemoryException and not StackOverflowException)
        {
            return null;
        }
    }

    /// <summary>Вершина ссылки через кэш снапшота.</summary>
    protected static CommitObject? TipOf(RepoSnapshot snapshot, string refName) =>
        snapshot.TipCache.GetOrAdd(refName, name =>
            snapshot.Refs.TryResolve(name, out var entry)
                ? TryCommit(snapshot, entry.Peeled ?? entry.Target)
                : null);

    /// <summary>Опорная точка вьюх (спека §3.4): дата коммита HEAD.</summary>
    protected static DateTimeOffset ViewTimestamp(RepoSnapshot snapshot)
    {
        if (snapshot.Refs.HeadTarget is { } head && TryCommit(snapshot, head) is { } commit)
            return commit.Committer.When;
        return DateTimeOffset.UnixEpoch;
    }

    /// <summary>Опорная точка вьюх history/ и dates/ — через кэш:
    /// ViewTimestamp зовётся из каждого Resolve корня, и без кэша коммит
    /// парсился заново на каждый вызов (ревью M4).
    ///
    /// Обычно это HEAD, но монтирование может назвать другую точку (Advanced
    /// в диалоге): «покажи историю такой, какой она была на v1.0». Имя
    /// разрешается сперва как ссылка, потом как SHA — тем же порядком, каким
    /// это делает сам git.</summary>
    protected static CommitObject? HeadCommit(RepoSnapshot snapshot)
    {
        var wanted = snapshot.Options.HistoryRef;
        if (string.IsNullOrWhiteSpace(wanted))
            return snapshot.Refs.HeadTarget is { } head
                ? snapshot.TipCache.GetOrAdd("HEAD", _ => TryCommit(snapshot, head))
                : null;

        return snapshot.TipCache.GetOrAdd(wanted, name =>
        {
            if (snapshot.Refs.TryResolve(name, out var entry)) return TryCommit(snapshot, entry.Target);
            foreach (var prefix in new[] { "refs/heads/", "refs/tags/", "refs/remotes/" })
                if (snapshot.Refs.TryResolve(prefix + name, out var qualified))
                    return TryCommit(snapshot, qualified.Target);
            // не ссылка — пробуем как объект: полный SHA или префикс
            return snapshot.Objects.FindByPrefix(name) is { } id ? TryCommit(snapshot, id) : null;
        });
    }

    // ---------- спуск по отображаемым именам ----------

    protected TreeEntry? ResolveDisplayPath(RepoSnapshot snapshot, ObjectId rootTree,
        IReadOnlyList<string> segments, int skip = 0)
    {
        // Разрешение пути — чистая функция от (дерево, путь, политика), а
        // снапшот иммутабелен: кэшируем, иначе обход истории пересчитывает
        // один и тот же спуск тысячи раз.
        var key = (rootTree, Names.Tag + "\0" + string.Join('\0', segments.Skip(skip)));
        if (snapshot.PathCache.TryGet(key, out var cached)) return cached.Entry;

        var resolved = ResolveDisplayPathUncached(snapshot, rootTree, segments, skip);
        snapshot.PathCache.Set(key, new CachedEntry(resolved));
        return resolved;
    }

    private TreeEntry? ResolveDisplayPathUncached(RepoSnapshot snapshot, ObjectId rootTree,
        IReadOnlyList<string> segments, int skip)
    {
        var current = new TreeEntry("", rootTree, GitFileMode.Directory);
        for (var i = skip; i < segments.Count; i++)
        {
            if (current.Mode != GitFileMode.Directory) return null;
            var tree = ReadTree(snapshot, current.Id);
            var display = DisplayNames(snapshot, current.Id, tree);
            TreeEntry? next = null;
            for (var k = 0; k < tree.Entries.Count; k++)
            {
                if (string.Equals(display[k].Display, segments[i], Names.Comparison))
                {
                    next = tree.Entries[k];
                    break;
                }
            }
            if (next is null) return null;
            current = next.Value;
        }
        return current;
    }

    /// <summary>Листинг директории дерева с отображаемыми именами.</summary>
    protected IEnumerable<DirEntry> ListTree(RepoSnapshot snapshot, ObjectId treeId,
        DateTimeOffset stamp)
    {
        var tree = ReadTree(snapshot, treeId);
        var display = DisplayNames(snapshot, treeId, tree);
        for (var i = 0; i < tree.Entries.Count; i++)
            yield return new DirEntry(display[i].Display, ToNodeInfo(snapshot, tree.Entries[i], stamp));
    }

    protected static NodeInfo ToNodeInfo(RepoSnapshot snapshot, in TreeEntry entry,
        DateTimeOffset stamp)
    {
        switch (entry.Mode)
        {
            case GitFileMode.Directory:
                return NodeInfo.Directory(stamp);
            case GitFileMode.Gitlink:
                return new NodeInfo(NodeKind.Submodule, entry.Id, 0, stamp);
            case GitFileMode.Symlink:
            {
                snapshot.Objects.TryGetHeader(entry.Id, out _, out var linkSize);
                return new NodeInfo(NodeKind.Symlink, entry.Id, linkSize, stamp);
            }
            default:
            {
                snapshot.Objects.TryGetHeader(entry.Id, out _, out var size);
                // 100755 доносится до границы: иначе на Linux ни один
                // скрипт из репозитория не запускается с тома
                return new NodeInfo(NodeKind.File, entry.Id, size, stamp,
                    executable: entry.Mode == GitFileMode.ExecutableFile);
            }
        }
    }

    /// <summary>Дерево коммита или узел внутри него — общий хвост
    /// для вьюх, у которых первый сегмент выбирает коммит.</summary>
    protected NodeInfo? ResolveInCommit(RepoSnapshot snapshot, CommitObject commit,
        IReadOnlyList<string> segments, int skip)
    {
        if (segments.Count == skip) return NodeInfo.Directory(commit.Committer.When);
        var entry = ResolveDisplayPath(snapshot, commit.Tree, segments, skip);
        if (entry is not null) return ToNodeInfo(snapshot, entry.Value, commit.Committer.When);

        // Маркер сабмодуля: узла с таким именем в дереве нет, он наш.
        if (segments.Count > skip + 1
            && string.Equals(segments[^1], SubmoduleMarker, Names.Comparison)
            && ResolveDisplayPath(snapshot, commit.Tree, segments.Take(segments.Count - 1).ToArray(), skip)
                is { Mode: GitFileMode.Gitlink } gitlink)
        {
            var text = SubmoduleText(gitlink.Id, string.Join('/', segments.Skip(skip).SkipLast(1)));
            return new NodeInfo(NodeKind.File, default, text.LongLength, commit.Committer.When);
        }
        return null;
    }

    protected IEnumerable<DirEntry> ListInCommit(RepoSnapshot snapshot, CommitObject commit,
        IReadOnlyList<string> segments, int skip)
    {
        ObjectId treeId;
        if (segments.Count == skip)
        {
            treeId = commit.Tree;
        }
        else
        {
            var entry = ResolveDisplayPath(snapshot, commit.Tree, segments, skip);
            // Сабмодуль — «пустая директория с файлом-маркером» (спека §3).
            if (entry is { Mode: GitFileMode.Gitlink } gitlink)
            {
                var text = SubmoduleText(gitlink.Id, string.Join('/', segments.Skip(skip)));
                yield return new DirEntry(SubmoduleMarker, new NodeInfo(NodeKind.File, default,
                    text.LongLength, commit.Committer.When));
                yield break;
            }
            if (entry is null || entry.Value.Mode != GitFileMode.Directory) yield break;
            treeId = entry.Value.Id;
        }
        foreach (var e in ListTree(snapshot, treeId, commit.Committer.When)) yield return e;
    }

    /// <summary>Синтетическое содержимое вьюхи. База отдаёт маркер сабмодуля —
    /// он появляется во ВСЕХ вьюхах, где встречается gitlink, поэтому и живёт
    /// здесь, а не в каждой по отдельности. Вьюхи со своей синтетикой
    /// (.gitfs/) переопределяют метод.</summary>
    public virtual byte[]? Read(RepoSnapshot snapshot, IReadOnlyList<string> segments)
    {
        if (segments.Count < 2) return null;
        if (!string.Equals(segments[^1], SubmoduleMarker, Names.Comparison)) return null;

        var head = SubmoduleCommitFor(snapshot, segments.Take(segments.Count - 1).ToArray());
        return head is null ? null
            : SubmoduleText(head.Value.Id, string.Join('/', head.Value.Path));
    }

    public virtual string? PhysicalPath(RepoSnapshot snapshot, IReadOnlyList<string> segments) =>
        null;

    /// <summary>Записывать можно всё, кроме маркера сабмодуля: он собран нами
    /// и содержимого в репозитории не имеет. Остальное дерево вьюхи
    /// по-прежнему принимает запись в песочницу.</summary>
    public virtual bool IsWriteProtected(IReadOnlyList<string> segments) =>
        segments.Count > 0
        && string.Equals(segments[^1], SubmoduleMarker, Names.Comparison);

    /// <summary>Ищет gitlink по пути внутри вьюхи. Каждая вьюха адресует
    /// коммит своим первым сегментом, поэтому перебираем разумные варианты:
    /// точки входа мало, а спускаться по чужой грамматике из базы нельзя.</summary>
    private (ObjectId Id, string Path)? SubmoduleCommitFor(RepoSnapshot snapshot,
        IReadOnlyList<string> segments)
    {
        for (var skip = 0; skip < segments.Count; skip++)
        {
            var commit = CommitAt(snapshot, segments, skip);
            if (commit is null) continue;
            if (ResolveDisplayPath(snapshot, commit.Tree, segments, skip)
                is { Mode: GitFileMode.Gitlink } gitlink)
            {
                return (gitlink.Id, string.Join('/', segments.Skip(skip)));
            }
        }
        return null;
    }

    /// <summary>Коммит, к которому относится путь, если первые skip сегментов
    /// его адресуют. Базовая версия знает только опорную точку; вьюхи с
    /// собственной адресацией (branches/, commits/) уточняют.</summary>
    protected virtual CommitObject? CommitAt(RepoSnapshot snapshot,
        IReadOnlyList<string> segments, int skip) =>
        skip == 0 ? HeadCommit(snapshot) : null;
}
