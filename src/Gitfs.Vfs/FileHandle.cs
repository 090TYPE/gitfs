using Gitfs.Core;

namespace Gitfs.Vfs;

/// <summary>Открытый файл. Держит аренду снапшота (объект не исчезнет из-под
/// чтения при смене эпохи) и последовательный поток с позицией.
///
/// Политика позиционирования: git-объекты читаются потоком, seek назад
/// невозможен. Последовательное чтение (обычный случай для ФС) идёт по
/// быстрому пути; чтение назад переоткрывает поток, вперёд — домотка.
/// Это выполняет бюджет §16 на последовательное чтение, не ломая случайное.</summary>
public sealed class FileHandle : IDisposable
{
    private readonly SnapshotLease _lease;
    private readonly ObjectId _blobId;

    private Stream? _stream;
    private long _position;

    public NodeInfo Info { get; }
    /// <summary>Виртуальный путь узла — адаптеру он нужен, чтобы перечислить
    /// директорию по уже открытому хендлу.</summary>
    public string Path { get; }
    public bool IsDirectory => Info.Kind == NodeKind.Directory;

    internal FileHandle(SnapshotLease lease, string path, in NodeInfo info, in ObjectId blobId)
    {
        _lease = lease;
        Path = path;
        Info = info;
        _blobId = blobId;
    }

    /// <summary>Читает окно [offset, offset+buffer.Length) из объекта.
    /// Возвращает фактически прочитанное; 0 — за концом файла.</summary>
    internal int Read(long offset, Span<byte> buffer)
    {
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

    public void Dispose()
    {
        _stream?.Dispose();
        _stream = null;
        _lease.Dispose();
    }
}
