using Gitfs.Core;
using Gitfs.Vfs.Overlay;

namespace Gitfs.Vfs;

/// <summary>Реализация границы поверх снапшотов, виртуального дерева и
/// overlay-песочницы. Каждая операция берёт аренду снапшота на своё время
/// (спека §8) и транслирует исключения нижних слоёв в коды — наружу не летит
/// ничего.
///
/// Порядок слоёв при разрешении пути (спека §9): OverlayStore → VirtualTree.
/// Перекрытие проверяется первым, иначе запись была бы не видна на чтении.</summary>
public sealed class VfsMountTarget : IMountTarget
{
    private readonly SnapshotManager _snapshots;
    private readonly VirtualTree _tree;
    private readonly OverlayStore? _overlay;
    private readonly string _repositoryName;
    private readonly bool _readOnly;
    private readonly long _capacity;

    public VfsMountTarget(SnapshotManager snapshots, VirtualTree tree,
        string repositoryName, bool readOnly = true, OverlayStore? overlay = null)
    {
        _snapshots = snapshots;
        _tree = tree;
        _overlay = overlay;
        _repositoryName = repositoryName;
        _readOnly = readOnly;
        _capacity = MeasureObjects(snapshots.Current.GitDir);
    }

    public GitfsResult<NodeInfo> Lookup(string path) => Guard(() =>
    {
        if (_overlay is not null)
        {
            if (_overlay.IsHidden(path)) return GitfsResult<NodeInfo>.Fail(GitfsError.NotFound);
            if (_overlay.TryGetFilePath(path) is { } overlayFile && File.Exists(overlayFile))
            {
                var info = new FileInfo(overlayFile);
                return GitfsResult<NodeInfo>.Ok(new NodeInfo(NodeKind.File, default,
                    info.Length, info.LastWriteTimeUtc, readOnly: false));
            }
        }
        using var lease = _snapshots.Acquire();
        var node = _tree.Resolve(lease.Snapshot, path);
        return node is null
            ? GitfsResult<NodeInfo>.Fail(GitfsError.NotFound)
            : GitfsResult<NodeInfo>.Ok(node.Value);
    });

    public GitfsResult<IEnumerable<DirEntry>> List(string path) => Guard(() =>
    {
        var lookup = Lookup(path);
        if (!lookup.TryGet(out var node)) return GitfsResult<IEnumerable<DirEntry>>.Fail(lookup.Error);
        if (node.Kind != NodeKind.Directory)
            return GitfsResult<IEnumerable<DirEntry>>.Fail(GitfsError.NotADirectory);

        using var lease = _snapshots.Acquire();
        var listed = _tree.List(lease.Snapshot, path);
        if (listed is null) return GitfsResult<IEnumerable<DirEntry>>.Fail(GitfsError.NotFound);

        // материализуем под арендой: последовательность ленива, аренда кончится
        var entries = listed.ToList();
        if (_overlay is not null) entries = ApplyOverlay(path, entries);
        return GitfsResult<IEnumerable<DirEntry>>.Ok(entries);
    });

    /// <summary>Накладывает песочницу на листинг: надгробия прячут записи,
    /// перекрытые файлы получают свой размер, созданные — добавляются.</summary>
    private List<DirEntry> ApplyOverlay(string path, List<DirEntry> entries)
    {
        var overlay = _overlay!;
        var result = new List<DirEntry>(entries.Count);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var basePath = OverlayStore.Normalize(path);

        foreach (var entry in entries)
        {
            var childPath = basePath.Length == 0 ? entry.Name : basePath + "/" + entry.Name;
            if (overlay.IsHidden(childPath)) continue;
            if (overlay.TryGetFilePath(childPath) is { } file && File.Exists(file))
            {
                var info = new FileInfo(file);
                result.Add(new DirEntry(entry.Name, new NodeInfo(NodeKind.File, default,
                    info.Length, info.LastWriteTimeUtc, readOnly: false)));
            }
            else
            {
                result.Add(entry);
            }
            seen.Add(entry.Name);
        }

        foreach (var (name, entry) in overlay.ChildrenOf(path))
        {
            if (entry.Kind != OverlayKind.File || seen.Contains(name)) continue;
            var file = Path.Combine(overlay.Root, entry.StorageName);
            if (!File.Exists(file)) continue;
            var info = new FileInfo(file);
            result.Add(new DirEntry(name, new NodeInfo(NodeKind.File, default,
                info.Length, info.LastWriteTimeUtc, readOnly: false)));
        }
        return result;
    }

    public GitfsResult<FileHandle> Open(string path, OpenMode mode)
    {
        if (mode == OpenMode.Write && (_readOnly || _overlay is null))
            return GitfsResult<FileHandle>.Fail(GitfsError.AccessDenied);
        return Guard(() =>
        {
            var lease = _snapshots.Acquire();
            try
            {
                if (_overlay is not null && _overlay.IsHidden(path))
                {
                    lease.Dispose();
                    return GitfsResult<FileHandle>.Fail(GitfsError.NotFound);
                }

                var existing = _overlay?.TryGetFilePath(path);
                var node = _tree.Resolve(lease.Snapshot, path);
                if (node is null && existing is null)
                {
                    lease.Dispose();
                    // запись по несуществующему пути создаёт файл в песочнице
                    if (mode != OpenMode.Write) return GitfsResult<FileHandle>.Fail(GitfsError.NotFound);
                    return OpenFresh(path);
                }

                if (mode == OpenMode.Write)
                {
                    var blobId = node?.BlobId ?? default;
                    var seed = existing is null && node is { Kind: NodeKind.File }
                        ? () => lease.Snapshot.Objects.OpenStream(blobId)
                        : (Func<Stream>?)null;
                    var overlayPath = _overlay!.PrepareForWrite(path, seed);
                    var info = new FileInfo(overlayPath);
                    return GitfsResult<FileHandle>.Ok(new FileHandle(lease, path,
                        new NodeInfo(NodeKind.File, default, info.Length,
                            info.LastWriteTimeUtc, readOnly: false),
                        default, overlayPath));
                }

                if (existing is not null && File.Exists(existing))
                {
                    var info = new FileInfo(existing);
                    return GitfsResult<FileHandle>.Ok(new FileHandle(lease, path,
                        new NodeInfo(NodeKind.File, default, info.Length,
                            info.LastWriteTimeUtc, readOnly: false),
                        default, existing));
                }

                // аренда переходит во владение хендла и живёт до Close
                return GitfsResult<FileHandle>.Ok(
                    new FileHandle(lease, path, node!.Value, node.Value.BlobId));
            }
            catch
            {
                lease.Dispose();
                throw;
            }
        });
    }

    private GitfsResult<FileHandle> OpenFresh(string path)
    {
        var lease = _snapshots.Acquire();
        try
        {
            var overlayPath = _overlay!.PrepareForWrite(path, null);
            return GitfsResult<FileHandle>.Ok(new FileHandle(lease, path,
                new NodeInfo(NodeKind.File, default, 0, DateTimeOffset.UtcNow, readOnly: false),
                default, overlayPath));
        }
        catch
        {
            lease.Dispose();
            throw;
        }
    }

    public GitfsResult<int> Read(FileHandle handle, long offset, Span<byte> buffer)
    {
        if (handle.IsDirectory) return GitfsResult<int>.Fail(GitfsError.NotADirectory);
        if (offset < 0) return GitfsResult<int>.Fail(GitfsError.IoError);
        try
        {
            return GitfsResult<int>.Ok(handle.Read(offset, buffer));
        }
        catch (Exception e)
        {
            return GitfsResult<int>.Fail(Classify(e));
        }
    }

    public GitfsResult<int> Write(FileHandle handle, long offset, ReadOnlySpan<byte> data)
    {
        if (!handle.IsOverlay) return GitfsResult<int>.Fail(GitfsError.AccessDenied);
        if (offset < 0) return GitfsResult<int>.Fail(GitfsError.IoError);
        try
        {
            return GitfsResult<int>.Ok(handle.Write(offset, data));
        }
        catch (Exception e)
        {
            return GitfsResult<int>.Fail(Classify(e));
        }
    }

    public GitfsResult<Unit> SetLength(FileHandle handle, long length)
    {
        if (!handle.IsOverlay) return GitfsResult<Unit>.Fail(GitfsError.AccessDenied);
        if (length < 0) return GitfsResult<Unit>.Fail(GitfsError.IoError);
        try
        {
            handle.SetLength(length);
            return GitfsResult<Unit>.Ok(Unit.Value);
        }
        catch (Exception e)
        {
            return GitfsResult<Unit>.Fail(Classify(e));
        }
    }

    /// <summary>Удаление — надгробие в песочнице: узел исчезает из выдачи,
    /// объект в репозитории остаётся нетронутым (спека §10).</summary>
    public GitfsResult<Unit> Delete(string path)
    {
        if (_readOnly || _overlay is null) return GitfsResult<Unit>.Fail(GitfsError.AccessDenied);
        try
        {
            if (Lookup(path).Error == GitfsError.NotFound)
                return GitfsResult<Unit>.Fail(GitfsError.NotFound);
            _overlay.Hide(path);
            return GitfsResult<Unit>.Ok(Unit.Value);
        }
        catch (Exception e)
        {
            return GitfsResult<Unit>.Fail(Classify(e));
        }
    }

    public GitfsResult<Unit> Close(FileHandle handle)
    {
        try
        {
            handle.Dispose();
            return GitfsResult<Unit>.Ok(Unit.Value);
        }
        catch (Exception e)
        {
            return GitfsResult<Unit>.Fail(Classify(e));
        }
    }

    public VolumeInfo GetVolumeInfo() =>
        new($"gitfs: {_repositoryName}", _capacity + (_overlay?.TotalBytes ?? 0), 0);

    public void Dispose() => _overlay?.Dispose();

    // ---------- внутренности ----------

    private static GitfsResult<T> Guard<T>(Func<GitfsResult<T>> operation)
    {
        try
        {
            return operation();
        }
        catch (Exception e)
        {
            return GitfsResult<T>.Fail(Classify(e));
        }
    }

    private static GitfsError Classify(Exception e) => e switch
    {
        FileNotFoundException or DirectoryNotFoundException => GitfsError.NotFound,
        InvalidDataException => GitfsError.CorruptObject,
        UnauthorizedAccessException => GitfsError.AccessDenied,
        NotSupportedException => GitfsError.NotSupported,
        OutOfMemoryException => GitfsError.TooLarge,
        _ => GitfsError.IoError,
    };

    private static long MeasureObjects(string gitDir)
    {
        var objects = Path.Combine(gitDir, "objects");
        if (!Directory.Exists(objects)) return 0;
        try
        {
            long total = 0;
            foreach (var file in Directory.EnumerateFiles(objects, "*", SearchOption.AllDirectories))
                total += new FileInfo(file).Length;
            return total;
        }
        catch (IOException) { return 0; }
        catch (UnauthorizedAccessException) { return 0; }
    }
}
