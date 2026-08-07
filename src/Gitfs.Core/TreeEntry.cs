using System.Text;

namespace Gitfs.Core;

/// <summary>Пять режимов записей дерева git. Прочие восьмеричные значения
/// (напр. легаси 100664) в v1 считаются повреждением — дифференциальные
/// тесты покажут, если реальность потребует ослабить.</summary>
public enum GitFileMode
{
    Directory = 1,      // 040000
    RegularFile = 2,    // 100644
    ExecutableFile = 3, // 100755
    Symlink = 4,        // 120000
    Gitlink = 5,        // 160000
}

public readonly struct TreeEntry
{
    public string Name { get; }
    public ObjectId Id { get; }
    public GitFileMode Mode { get; }

    public TreeEntry(string name, ObjectId id, GitFileMode mode)
    {
        Name = name;
        Id = id;
        Mode = mode;
    }
}

/// <summary>Разобранное дерево: повторяющиеся записи
/// "&lt;mode-octal&gt; &lt;name&gt;\0&lt;20 байт sha&gt;". Порядок записей сохраняется
/// как в объекте — git хранит их отсортированными, и на этом порядке
/// строится детерминизм правил имён (спека §3.3).</summary>
public sealed class TreeObject
{
    public IReadOnlyList<TreeEntry> Entries { get; }

    private TreeObject(List<TreeEntry> entries) => Entries = entries;

    public static TreeObject Parse(ReadOnlySpan<byte> data)
    {
        var entries = new List<TreeEntry>();
        var pos = 0;
        while (pos < data.Length)
        {
            var space = data[pos..].IndexOf((byte)' ');
            if (space < 0) throw new InvalidDataException("tree entry: no space after mode");
            var mode = ParseMode(data.Slice(pos, space));
            pos += space + 1;

            var nul = data[pos..].IndexOf((byte)0);
            if (nul < 0) throw new InvalidDataException("tree entry: no NUL after name");
            var name = Encoding.UTF8.GetString(data.Slice(pos, nul));
            pos += nul + 1;

            // Инвариант формата, не политика отображения (ревью M2): git fsck
            // такие деревья отвергает, а gitfs fsck не запускает — валидируем сами,
            // иначе '.', '..' и 'a/b' утекут в адаптер ФС как имена записей.
            if (name.Length == 0 || name is "." or ".." || name.Contains('/'))
                throw new InvalidDataException($"invalid tree entry name '{name}'");

            if (pos + ObjectId.RawLength > data.Length)
                throw new InvalidDataException("tree entry: truncated object id");
            var id = new ObjectId(data.Slice(pos, ObjectId.RawLength));
            pos += ObjectId.RawLength;

            entries.Add(new TreeEntry(name, id, mode));
        }
        return new TreeObject(entries);
    }

    private static GitFileMode ParseMode(ReadOnlySpan<byte> octal) => octal switch
    {
        [0x34, 0x30, 0x30, 0x30, 0x30] => GitFileMode.Directory,                   // "40000"
        [0x31, 0x30, 0x30, 0x36, 0x34, 0x34] => GitFileMode.RegularFile,           // "100644"
        [0x31, 0x30, 0x30, 0x37, 0x35, 0x35] => GitFileMode.ExecutableFile,        // "100755"
        [0x31, 0x32, 0x30, 0x30, 0x30, 0x30] => GitFileMode.Symlink,               // "120000"
        [0x31, 0x36, 0x30, 0x30, 0x30, 0x30] => GitFileMode.Gitlink,               // "160000"
        _ => throw new InvalidDataException(
            $"unknown tree entry mode '{Encoding.ASCII.GetString(octal)}'"),
    };
}
