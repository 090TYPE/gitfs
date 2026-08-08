using System.Buffers.Binary;

namespace Gitfs.Core.Accel;

/// <summary>Чтение файла commit-graph (спека §6.7). Даёт дерево и родителей
/// коммита БЕЗ распаковки объекта: обход истории превращается из тысяч
/// zlib-инфляций в чтение 36 байт на коммит.
///
/// Формат: сигнатура "CGPH", версия, версия хеша, число чанков; таблица
/// чанков (4 байта идентификатора + 8 байт смещения); чанки OIDF (fanout
/// на 256 входов), OIDL (отсортированные OID) и CDAT (дерево + два
/// родителя + поколение и дата).
///
/// Опционален: при отсутствии или несовместимости всё работает через
/// обычное чтение объектов.</summary>
public sealed class CommitGraph
{
    private const uint Signature = 0x43475048; // "CGPH"
    private const uint ChunkOidFanout = 0x4f494446; // "OIDF"
    private const uint ChunkOidLookup = 0x4f49444c; // "OIDL"
    private const uint ChunkCommitData = 0x43444154; // "CDAT"

    private const int CommitDataSize = 36; // 20 tree + 4 + 4 parents + 8 generation/date
    private const uint NoParent = 0x7000_0000;
    private const uint ExtraEdges = 0x8000_0000;

    private readonly byte[] _data;
    private readonly int _fanout;
    private readonly int _lookup;
    private readonly int _commitData;

    public int Count { get; }

    private CommitGraph(byte[] data, int fanout, int lookup, int commitData, int count)
    {
        _data = data;
        _fanout = fanout;
        _lookup = lookup;
        _commitData = commitData;
        Count = count;
    }

    /// <summary>null — графа нет или он в неподдерживаемом формате.
    /// Это не ошибка: ускоритель необязателен.</summary>
    public static CommitGraph? TryLoad(string gitDir)
    {
        var path = Path.Combine(gitDir, "objects", "info", "commit-graph");
        if (!File.Exists(path)) return null;
        try
        {
            var data = File.ReadAllBytes(path);
            if (data.Length < 8) return null;
            if (BinaryPrimitives.ReadUInt32BigEndian(data) != Signature) return null;
            if (data[4] != 1) return null;                 // версия файла
            if (data[5] != 1) return null;                 // sha-1; sha-256 вне v1
            var chunkCount = data[6];

            int fanout = 0, lookup = 0, commitData = 0;
            var tableStart = 8;
            for (var i = 0; i < chunkCount; i++)
            {
                var entry = tableStart + i * 12;
                if (entry + 12 > data.Length) return null;
                var id = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(entry));
                var offset = (int)BinaryPrimitives.ReadUInt64BigEndian(data.AsSpan(entry + 4));
                if (offset < 0 || offset >= data.Length) return null;
                if (id == ChunkOidFanout) fanout = offset;
                else if (id == ChunkOidLookup) lookup = offset;
                else if (id == ChunkCommitData) commitData = offset;
            }
            if (fanout == 0 || lookup == 0 || commitData == 0) return null;

            var count = (int)BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(fanout + 255 * 4));
            if (count <= 0) return null;
            if (lookup + (long)count * ObjectId.RawLength > data.Length) return null;
            if (commitData + (long)count * CommitDataSize > data.Length) return null;

            return new CommitGraph(data, fanout, lookup, commitData, count);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Дерево и первый родитель коммита. false — коммита нет в графе
    /// (например, он появился после последней записи графа).</summary>
    public bool TryGetCommit(in ObjectId id, out ObjectId tree, out ObjectId? firstParent)
    {
        tree = default;
        firstParent = null;
        var slot = FindSlot(id);
        if (slot < 0) return false;

        var entry = _commitData + slot * CommitDataSize;
        tree = new ObjectId(_data.AsSpan(entry, ObjectId.RawLength));

        var parent = BinaryPrimitives.ReadUInt32BigEndian(_data.AsSpan(entry + 20));
        if (parent == NoParent) return true;               // корневой коммит
        if ((parent & ExtraEdges) != 0) parent &= ~ExtraEdges;
        if (parent >= Count) return true;                  // повреждён — молча без родителя
        firstParent = OidAt((int)parent);
        return true;
    }

    private ObjectId OidAt(int index) =>
        new(_data.AsSpan(_lookup + index * ObjectId.RawLength, ObjectId.RawLength));

    private int FindSlot(in ObjectId id)
    {
        var bucket = id.FirstByte;
        var lo = bucket == 0 ? 0
            : (int)BinaryPrimitives.ReadUInt32BigEndian(_data.AsSpan(_fanout + (bucket - 1) * 4));
        var hi = (int)BinaryPrimitives.ReadUInt32BigEndian(_data.AsSpan(_fanout + bucket * 4));
        if (hi > Count) return -1;
        while (lo < hi)
        {
            var mid = lo + (hi - lo) / 2;
            var cmp = id.CompareTo(OidAt(mid));
            if (cmp == 0) return mid;
            if (cmp < 0) hi = mid; else lo = mid + 1;
        }
        return -1;
    }
}
