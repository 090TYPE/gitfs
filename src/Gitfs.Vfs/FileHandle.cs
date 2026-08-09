using Gitfs.Core;

namespace Gitfs.Vfs;

/// <summary>Открытый файл. Держит аренду снапшота (объект не исчезнет из-под
/// чтения при смене эпохи) и поток с позицией.
///
/// Источников три: git-объект (только чтение, поток последовательный), файл
/// overlay-песочницы (чтение и запись, обычный FileStream с seek) и
/// СИНТЕТИЧЕСКОЕ содержимое — байты, которых нет ни в репозитории, ни на
/// диске: `.gitfs/status.txt` и `.gitfs/log.txt` собираются в момент
/// открытия. Спека §14: «оба файла — часть смонтированного дерева, поэтому
/// доступны без отдельного инструмента».
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
    private readonly byte[]? _synthetic;
    private readonly bool _writable;

    private Stream? _stream;
    private long _position;

    public NodeInfo Info { get; private set; }
    /// <summary>Виртуальный путь узла — адаптеру он нужен, чтобы перечислить
    /// директорию по уже открытому хендлу.</summary>
    public string Path { get; }
    public bool IsDirectory => Info.Kind == NodeKind.Directory;
    public bool IsOverlay => _overlayPath is not null;

    internal FileHandle(SnapshotLease lease, string path, in NodeInfo info, in ObjectId blobId,
        string? overlayPath = null, byte[]? synthetic = null, bool writable = true)
    {
        _lease = lease;
        Path = path;
        Info = info;
        _blobId = blobId;
        _overlayPath = overlayPath;
        _synthetic = synthetic;
        _writable = writable;
    }

    /// <summary>Читает окно [offset, offset+buffer.Length) из объекта.
    /// Возвращает фактически прочитанное; 0 — за концом файла.</summary>
    internal int Read(long offset, Span<byte> buffer)
    {
        if (_overlayPath is not null) return ReadOverlay(offset, buffer);
        if (_synthetic is not null)
        {
            // Байты собраны при открытии и с тех пор не меняются: диагностика,
            // которая шевелится посреди чтения, читалась бы кусками от разных
            // моментов и не складывалась бы в осмысленную картину.
            if (offset >= _synthetic.Length) return 0;
            var take = (int)Math.Min(buffer.Length, _synthetic.Length - offset);
            _synthetic.AsSpan((int)offset, take).CopyTo(buffer);
            return take;
        }
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
        // Файл песочницы, открытый ЧЕРЕЗ .gitfs/overlay/, физический — но
        // смотровой: спека обещает видимость записанного, а не второй путь
        // записи в ту же песочницу мимо всех правил.
        if (_overlayPath is null || !_writable)
            throw new InvalidOperationException("handle is read-only");
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
        // Файл песочницы, открытый ЧЕРЕЗ .gitfs/overlay/, физический — но
        // смотровой: спека обещает видимость записанного, а не второй путь
        // записи в ту же песочницу мимо всех правил.
        if (_overlayPath is null || !_writable)
            throw new InvalidOperationException("handle is read-only");
        var file = OverlayStream();
        file.SetLength(length);
        file.Flush();
        Info = new NodeInfo(Info.Kind, Info.BlobId, length, DateTimeOffset.UtcNow, readOnly: false);
    }

    private FileStream OverlayStream()
    {
        if (_stream is FileStream existing) return existing;
        _stream?.Dispose();
        // Смотровой хендл открывается ТОЛЬКО на чтение и ничего не создаёт:
        // OpenOrCreate по пути внутри .gitfs/overlay/ завёл бы пустой файл там,
        // где пользователь всего лишь посмотрел, что он записал.
        var file = _writable
            ? new FileStream(_overlayPath!, FileMode.OpenOrCreate,
                FileAccess.ReadWrite, FileShare.ReadWrite)
            : new FileStream(_overlayPath!, FileMode.Open,
                FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
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
