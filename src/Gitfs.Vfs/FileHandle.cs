using Gitfs.Core;

namespace Gitfs.Vfs;

/// <summary>Открытый файл. Держит аренду снапшота (объект не исчезнет из-под
/// чтения при смене эпохи) и поток с позицией.
///
/// Источников два: git-объект (только чтение, поток последовательный) и файл
/// overlay-песочницы (чтение и запись, обычный FileStream с seek).
///
/// Политика позиционирования для git-объектов: seek назад невозможен, поэтому
/// последовательное чтение идёт быстрым путём, чтение назад переоткрывает
/// поток, вперёд — домотка. Это выполняет бюджет §16 на последовательное
/// чтение, не ломая случайное.</summary>
public sealed class FileHandle : IDisposable
{
    private readonly SnapshotLease _lease;
    private readonly ObjectId _blobId;
    private readonly string? _overlayPath;

    private Stream? _stream;
    private long _position;

    public NodeInfo Info { get; private set; }
    /// <summary>Виртуальный путь узла — адаптеру он нужен, чтобы перечислить
    /// директорию по уже открытому хендлу.</summary>
    public string Path { get; }
    public bool IsDirectory => Info.Kind == NodeKind.Directory;
    public bool IsOverlay => _overlayPath is not null;

    internal FileHandle(SnapshotLease lease, string path, in NodeInfo info, in ObjectId blobId,
        string? overlayPath = null)
    {
        _lease = lease;
        Path = path;
        Info = info;
        _blobId = blobId;
        _overlayPath = overlayPath;
    }

    /// <summary>Читает окно [offset, offset+buffer.Length) из объекта.
    /// Возвращает фактически прочитанное; 0 — за концом файла.</summary>
    internal int Read(long offset, Span<byte> buffer)
    {
        if (_overlayPath is not null) return ReadOverlay(offset, buffer);
        if (offset >= Info.Size) return 0;
        if (_stream is null || offset < _position)
        {
            _stream?.Dispose();
            _stream = _lease.Snapshot.Objects.OpenStream(_blobId);
            _position = 0;
        }
        while (_position < offset)
        {
            var skip = (int)Math.Min(offset - _position, 64 * 1024);
            var scratch = new byte[skip];
            var n = _stream.Read(scratch, 0, skip);
            if (n == 0) return 0;
            _position += n;
        }

        var want = (int)Math.Min(buffer.Length, Info.Size - offset);
        var total = 0;
        while (total < want)
        {
            var n = _stream.Read(buffer[total..want]);
            if (n == 0) break;
            total += n;
            _position += n;
        }
        return total;
    }

    private int ReadOverlay(long offset, Span<byte> buffer)
    {
        var file = OverlayStream();
        file.Position = offset;
        return file.Read(buffer);
    }

    internal int Write(long offset, ReadOnlySpan<byte> data)
    {
        if (_overlayPath is null) throw new InvalidOperationException("handle is read-only");
        var file = OverlayStream();
        file.Position = offset;
        file.Write(data);
        file.Flush();
        Info = new NodeInfo(Info.Kind, Info.BlobId, file.Length, DateTimeOffset.UtcNow,
            readOnly: false);
        return data.Length;
    }

    /// <summary>Усечение или расширение файла песочницы. Нужно настоящей ФС:
    /// перезапись существующего файла (CREATE_ALWAYS / SetFileSize) обязана
    /// отбросить хвост прежнего содержимого, иначе от старой версии остаётся
    /// мусор в конце.</summary>
    internal void SetLength(long length)
    {
        if (_overlayPath is null) throw new InvalidOperationException("handle is read-only");
        var file = OverlayStream();
        file.SetLength(length);
        file.Flush();
        Info = new NodeInfo(Info.Kind, Info.BlobId, length, DateTimeOffset.UtcNow, readOnly: false);
    }

    private FileStream OverlayStream()
    {
        if (_stream is FileStream existing) return existing;
        _stream?.Dispose();
        var file = new FileStream(_overlayPath!, FileMode.OpenOrCreate,
            FileAccess.ReadWrite, FileShare.ReadWrite);
        _stream = file;
        return file;
    }

    public void Dispose()
    {
        _stream?.Dispose();
        _stream = null;
        _lease.Dispose();
    }
}
