using Gitfs.Core.Objects;

namespace Gitfs.Core.Walk;

/// <summary>Резолв пути внутри дерева коммита. Ничего не знает о ФС:
/// сегменты — уже разобранный путь, сравнение имён — ординальное
/// (байтовое, как в git).</summary>
public sealed class TreeWalker
{
    private const long MaxTreeBytes = 64L << 20; // деревья исчисляются мегабайтами максимум

    private readonly ObjectReader _reader;

    public TreeWalker(ObjectReader reader) => _reader = reader;

    /// <summary>null — путь не существует (в т.ч. попытка пройти «сквозь» файл).
    /// Пустой путь — корневая директория самого дерева.</summary>
    public TreeEntry? TryResolve(in ObjectId rootTree, ReadOnlySpan<string> segments)
    {
        var current = new TreeEntry("", rootTree, GitFileMode.Directory);
        foreach (var segment in segments)
        {
            if (current.Mode != GitFileMode.Directory) return null; // сквозь файл нельзя
            var tree = TreeObject.Parse(_reader.ReadAll(current.Id, MaxTreeBytes));
            TreeEntry? next = null;
            foreach (var entry in tree.Entries)
            {
                if (string.Equals(entry.Name, segment, StringComparison.Ordinal))
                {
                    next = entry;
                    break;
                }
            }
            if (next is null) return null;
            current = next.Value;
        }
        return current;
    }
}
