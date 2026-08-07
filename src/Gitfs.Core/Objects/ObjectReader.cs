namespace Gitfs.Core.Objects;

/// <summary>Составной доступ к объектам: сначала loose (новее), затем пакеты.
/// Набор пакетов фиксируется при создании — это будущая часть RepoSnapshot;
/// переоткрытие после git gc (спека §7) появится вместе со снапшотами.</summary>
public sealed class ObjectReader : IObjectReader, IDisposable
{
    /// <summary>Потолок материализации дельты в OpenStream; настоящие лимиты
    /// (--max-object-mb) применяет адаптер поверх (§15).</summary>
    private const long StreamMaxBytes = 1L << 30;

    private readonly LooseObjectReader _loose;
    private readonly PackFile[] _packs;

    public ObjectReader(string gitDir)
    {
        _loose = new LooseObjectReader(gitDir);
        var packDir = Path.Combine(gitDir, "objects", "pack");
        var packs = new List<PackFile>();
        if (Directory.Exists(packDir))
        {
            try
            {
                foreach (var packPath in Directory.GetFiles(packDir, "*.pack"))
                {
                    // .pack без .idx — штатное окно во время git repack/fetch
                    // (.pack пишется раньше индекса): пропускаем, не валим весь ридер
                    if (!File.Exists(Path.ChangeExtension(packPath, ".idx"))) continue;
                    packs.Add(PackFile.Open(packPath));
                }
            }
            catch
            {
                foreach (var p in packs) p.Dispose(); // не течём при падении на N-м паке
                throw;
            }
        }
        _packs = packs.ToArray();
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

    public Stream OpenStream(in ObjectId id)
    {
        if (_loose.TryOpenStream(id, out _, out var looseSize) is { } looseStream)
            return new LimitedReadStream(looseStream, looseSize);
        foreach (var pack in _packs)
            if (pack.TryOpenStream(id, StreamMaxBytes, out _, out _) is { } packStream)
                return packStream;
        throw new FileNotFoundException($"object not found: {id}");
    }

    public byte[] ReadAll(in ObjectId id, long maxBytes)
    {
        // одним вызовом, без Contains+ReadAll: git gc может убрать loose-файл
        // между двумя проверками, а объект уже лежит в паке строкой ниже
        if (_loose.TryReadAll(id, maxBytes) is { } loose) return loose;
        foreach (var pack in _packs)
            if (pack.TryReadObject(id, maxBytes, out _, out var data)) return data;
        throw new FileNotFoundException($"object not found: {id}");
    }

    public void Dispose()
    {
        foreach (var pack in _packs) pack.Dispose();
    }
}
