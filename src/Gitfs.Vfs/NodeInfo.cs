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

    public NodeInfo(NodeKind kind, ObjectId blobId, long size, DateTimeOffset timestamp, bool readOnly = true)
    {
        Kind = kind;
        BlobId = blobId;
        Size = size;
        Timestamp = timestamp;
        ReadOnly = readOnly;
    }

    public static NodeInfo Directory(DateTimeOffset timestamp) =>
        new(NodeKind.Directory, default, 0, timestamp);
}

public readonly record struct DirEntry(string Name, NodeInfo Info);
