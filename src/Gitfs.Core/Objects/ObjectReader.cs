namespace Gitfs.Core.Objects;

/// <summary>Составной доступ к объектам: сначала loose (новее), затем пакеты.
/// Набор пакетов фиксируется при создании — это будущая часть RepoSnapshot;
/// переоткрытие после git gc (спека §7) появится вместе со снапшотами.</summary>
public sealed class ObjectReader : IDisposable
{
    private readonly LooseObjectReader _loose;
    private readonly PackFile[] _packs;

    public ObjectReader(string gitDir)
    {
        _loose = new LooseObjectReader(gitDir);
        var packDir = Path.Combine(gitDir, "objects", "pack");
        _packs = Directory.Exists(packDir)
            ? Directory.GetFiles(packDir, "*.pack").Select(PackFile.Open).ToArray()
            : [];
    }

    public bool Contains(in ObjectId id)
    {
        if (_loose.Contains(id)) return true;
        foreach (var pack in _packs)
            if (pack.Contains(id)) return true;
        return false;
    }

    public bool TryGetHeader(in ObjectId id, out GitObjectType type, out long size)
    {
        if (_loose.TryGetHeader(id, out type, out size)) return true;
        foreach (var pack in _packs)
            if (pack.TryGetHeader(id, out type, out size)) return true;
        return false;
    }

    public byte[] ReadAll(in ObjectId id, long maxBytes)
    {
        if (_loose.Contains(id)) return _loose.ReadAll(id, maxBytes);
        foreach (var pack in _packs)
            if (pack.TryReadObject(id, maxBytes, out _, out var data)) return data;
        throw new FileNotFoundException($"object not found: {id}");
    }

    public void Dispose()
    {
        foreach (var pack in _packs) pack.Dispose();
    }
}
