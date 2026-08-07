namespace Gitfs.Vfs;

/// <summary>Вьюха — чистая функция над снапшотом (спека §9): не знает о ФС,
/// не имеет состояния сверх кэшей. Сегменты — уже разобранный путь
/// ОТНОСИТЕЛЬНО корня вьюхи (без её имени).
/// Отступление от эскиза §9: IReadOnlyList вместо ReadOnlySpan —
/// итераторные методы не принимают span.</summary>
public interface IView
{
    string Name { get; }
    NodeInfo? Resolve(RepoSnapshot snapshot, IReadOnlyList<string> segments);
    IEnumerable<DirEntry> List(RepoSnapshot snapshot, IReadOnlyList<string> segments);
}
