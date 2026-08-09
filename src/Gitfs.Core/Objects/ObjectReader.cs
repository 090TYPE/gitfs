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
    private readonly string _gitDir;

    /// <param name="maxCachedObjectBytes">Потолок для одного объекта в кэше
    /// развёрнутых дельт — «Max cached blob» из настроек монтирования.
    /// null — только общий бюджет пакета.</param>
    public ObjectReader(string gitDir, long? maxCachedObjectBytes = null)
    {
        _gitDir = gitDir;
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
                    packs.Add(PackFile.Open(packPath, maxObjectBytes: maxCachedObjectBytes));
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

    /// <summary>Единственный объект с данным hex-префиксом; null — не найден
    /// ИЛИ найдено больше одного (спека §4: неоднозначный префикс — не выбор
    /// первого попавшегося). Перф-долг: линейное сканирование по пакам и
    /// каталогам loose; ускорить бинарным поиском по отсортированной таблице
    /// .idx — к M7 вместе с бенчмарками.</summary>
    public ObjectId? FindByPrefix(string hexPrefix)
    {
        if (hexPrefix.Length is < 2 or > ObjectId.HexLength) return null;
        foreach (var c in hexPrefix)
            if (!Uri.IsHexDigit(c))
                return null;

        // Дедупликация обязательна: один объект штатно лежит и в loose, и в паке
        // (repack без -d, или fetch поверх), а два вхождения одного OID — НЕ
        // неоднозначность. Ревью M4: без множества такой объект «терялся».
        var matches = new HashSet<ObjectId>();
        bool Accept(in ObjectId candidate)
        {
            if (!StartsWith(candidate, hexPrefix)) return true;
            matches.Add(candidate);
            return matches.Count < 2; // два РАЗНЫХ объекта — ответа нет
        }

        var looseBucket = Path.Combine(_gitDir, "objects", hexPrefix[..2]);
        if (Directory.Exists(looseBucket))
        {
            foreach (var file in Directory.EnumerateFiles(looseBucket))
            {
                var hex = hexPrefix[..2] + Path.GetFileName(file);
                if (!ObjectId.TryParse(hex, out var id)) continue;
                if (!Accept(id)) return null;
            }
        }

        foreach (var pack in _packs)
            foreach (var id in pack.ObjectIds)
                if (!Accept(id))
                    return null;

        return matches.Count == 1 ? matches.First() : null;
    }

    /// <summary>Сравнение без аллокации 40-символьной строки на объект.</summary>
    private static bool StartsWith(in ObjectId id, string hexPrefix)
    {
        Span<byte> raw = stackalloc byte[ObjectId.RawLength];
        id.WriteRaw(raw);
        for (var i = 0; i < hexPrefix.Length; i++)
        {
            var nibble = (i & 1) == 0 ? raw[i / 2] >> 4 : raw[i / 2] & 0xf;
            if (nibble != HexValue(hexPrefix[i])) return false;
        }
        return true;
    }

    private static int HexValue(char c) => c switch
    {
        >= '0' and <= '9' => c - '0',
        >= 'a' and <= 'f' => c - 'a' + 10,
        >= 'A' and <= 'F' => c - 'A' + 10,
        _ => -1,
    };

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
