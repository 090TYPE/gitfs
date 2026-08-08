using Gitfs.Core;

namespace Gitfs.Vfs;

public enum NodeKind
{
    Directory = 1,
    File = 2,
    Symlink = 3,
    Submodule = 4,
}

/// <summary>Атрибуты виртуального узла (спека §9). Времена — дата коммита,
/// к которому относится узел (§3.4): сортировка по дате в Проводнике
/// даёт хронологию.</summary>
public readonly struct NodeInfo
{
    public NodeKind Kind { get; }
    public ObjectId BlobId { get; }
    public long Size { get; }
    public DateTimeOffset Timestamp { get; }
    public bool ReadOnly { get; }

    /// <summary>git различает 100644 и 100755, и на Linux эта разница
    /// наблюдаема: без неё каждый скрипт в репозитории приезжает на том
    /// как -rw-r--r--, а `./configure` получает EACCES от ядра (том
    /// монтируется с default_permissions). Границе нужно это донести —
    /// у NodeKind нет места для такого признака.</summary>
    public bool Executable { get; }

    public NodeInfo(NodeKind kind, ObjectId blobId, long size, DateTimeOffset timestamp,
        bool readOnly = true, bool executable = false)
    {
        Kind = kind;
        BlobId = blobId;
        Size = size;
        Timestamp = timestamp;
        ReadOnly = readOnly;
        Executable = executable;
    }

    public static NodeInfo Directory(DateTimeOffset timestamp) =>
        new(NodeKind.Directory, default, 0, timestamp);
}

public readonly record struct DirEntry(string Name, NodeInfo Info);

/// <summary>Обёртка для кэша: nullable-структура как значение LruCache
/// требует ссылочного типа, чтобы «путь отсутствует» тоже кэшировалось.</summary>
public sealed record CachedEntry(TreeEntry? Entry);
