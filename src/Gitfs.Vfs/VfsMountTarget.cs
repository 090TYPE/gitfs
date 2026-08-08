using Gitfs.Core;

namespace Gitfs.Vfs;

/// <summary>Реализация границы поверх снапшотов и виртуального дерева.
/// Каждая операция берёт аренду снапшота на своё время (спека §8) и
/// транслирует исключения нижних слоёв в коды — наружу не летит ничего.</summary>
public sealed class VfsMountTarget : IMountTarget
{
    private readonly SnapshotManager _snapshots;
    private readonly VirtualTree _tree;
    private readonly string _repositoryName;
    private readonly bool _readOnly;
    private readonly long _capacity;

    public VfsMountTarget(SnapshotManager snapshots, VirtualTree tree,
        string repositoryName, bool readOnly = true)
    {
        _snapshots = snapshots;
        _tree = tree;
        _repositoryName = repositoryName;
        _readOnly = readOnly;
        _capacity = MeasureObjects(snapshots.Current.GitDir);
    }

    public GitfsResult<NodeInfo> Lookup(string path) => Guard(() =>
    {
        using var lease = _snapshots.Acquire();
        var node = _tree.Resolve(lease.Snapshot, path);
        return node is null
            ? GitfsResult<NodeInfo>.Fail(GitfsError.NotFound)
            : GitfsResult<NodeInfo>.Ok(node.Value);
    });

    public GitfsResult<IEnumerable<DirEntry>> List(string path) => Guard(() =>
    {
        using var lease = _snapshots.Acquire();
        var node = _tree.Resolve(lease.Snapshot, path);
        if (node is null) return GitfsResult<IEnumerable<DirEntry>>.Fail(GitfsError.NotFound);
        if (node.Value.Kind != NodeKind.Directory)
            return GitfsResult<IEnumerable<DirEntry>>.Fail(GitfsError.NotADirectory);
        var entries = _tree.List(lease.Snapshot, path);
        return entries is null
            ? GitfsResult<IEnumerable<DirEntry>>.Fail(GitfsError.NotFound)
            // материализуем под арендой: последовательность ленива, а аренда
            // закончится на выходе из метода
            : GitfsResult<IEnumerable<DirEntry>>.Ok(entries.ToList());
    });

    public GitfsResult<FileHandle> Open(string path, OpenMode mode)
    {
        if (mode == OpenMode.Write && _readOnly)
            return GitfsResult<FileHandle>.Fail(GitfsError.AccessDenied);
        return Guard(() =>
        {
            var lease = _snapshots.Acquire();
            try
            {
                var node = _tree.Resolve(lease.Snapshot, path);
                if (node is null)
                {
                    lease.Dispose();
                    return GitfsResult<FileHandle>.Fail(GitfsError.NotFound);
                }
                // аренда переходит во владение хендла и живёт до Close
                return GitfsResult<FileHandle>.Ok(
                    new FileHandle(lease, node.Value, node.Value.BlobId));
            }
            catch
            {
                lease.Dispose();
                throw;
            }
        });
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

    public GitfsResult<int> Write(FileHandle handle, long offset, ReadOnlySpan<byte> data) =>
        // overlay приходит в M5; до тех пор запись отвергается кодом, а не исключением
        GitfsResult<int>.Fail(_readOnly ? GitfsError.AccessDenied : GitfsError.NotSupported);

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
        new($"gitfs: {_repositoryName}", _capacity, 0);

    public void Dispose() { }

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
