using System.Text;
using Gitfs.Core.Objects;

namespace Gitfs.Core;

/// <summary>Аннотированный тег: object/type/tag/tagger + сообщение.
/// Peel идёт по цепочке tag→tag→…→не-тег (git допускает теги на теги).</summary>
public sealed class TagObject
{
    private const int MaxPeelDepth = 32;

    public ObjectId Id { get; }
    public ObjectId Target { get; }
    public GitObjectType TargetType { get; }
    public string Name { get; }
    public string Message { get; }

    private TagObject(ObjectId id, ObjectId target, GitObjectType targetType,
        string name, string message)
    {
        Id = id;
        Target = target;
        TargetType = targetType;
        Name = name;
        Message = message;
    }

    public static TagObject Parse(ObjectId id, ReadOnlySpan<byte> data)
    {
        ObjectId? target = null;
        GitObjectType? targetType = null;
        string? name = null;

        var pos = 0;
        while (pos < data.Length)
        {
            var nl = data[pos..].IndexOf((byte)'\n');
            if (nl < 0) throw new InvalidDataException($"tag {id}: header without newline");
            var line = data.Slice(pos, nl);
            pos += nl + 1;

            if (line.Length == 0) break;
            if (line[0] == (byte)' ') continue;

            var space = line.IndexOf((byte)' ');
            if (space < 0) throw new InvalidDataException($"tag {id}: malformed header");
            var key = line[..space];
            var value = Encoding.UTF8.GetString(line[(space + 1)..]);

            if (key.SequenceEqual("object"u8)) target = ObjectId.Parse(value);
            else if (key.SequenceEqual("type"u8)) targetType = value switch
            {
                "commit" => GitObjectType.Commit,
                "tree" => GitObjectType.Tree,
                "blob" => GitObjectType.Blob,
                "tag" => GitObjectType.Tag,
                _ => throw new InvalidDataException($"tag {id}: unknown target type '{value}'"),
            };
            else if (key.SequenceEqual("tag"u8)) name = value;
            // tagger и прочее — пропускаем
        }

        if (target is null || targetType is null || name is null)
            throw new InvalidDataException($"tag {id}: missing object/type/tag");
        return new TagObject(id, target.Value, targetType.Value, name,
            Encoding.UTF8.GetString(data[pos..]));
    }

    /// <summary>Разыменовывает id до первого не-тега. Для packed-refs peel уже
    /// лежит в строках «^», этот путь — для loose-тегов (спека §6.5).</summary>
    public static (ObjectId Id, GitObjectType Type) Peel(ObjectReader reader, ObjectId id)
    {
        for (var depth = 0; depth < MaxPeelDepth; depth++)
        {
            if (!reader.TryGetHeader(id, out var type, out _))
                throw new FileNotFoundException($"object not found while peeling: {id}");
            if (type != GitObjectType.Tag) return (id, type);
            var tag = Parse(id, reader.ReadAll(id, 1 << 20));
            id = tag.Target;
        }
        throw new InvalidDataException($"tag chain deeper than {MaxPeelDepth}");
    }
}
