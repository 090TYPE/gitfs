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

    /// <summary>Контракт (ревью M2): List осмыслен только для путей, где
    /// Resolve вернул Directory; для файла или несуществующего пути результат —
    /// пустая последовательность, неотличимая от пустой директории. Адаптер
    /// обязан сначала Resolve и транслировать не-директорию в NotADirectory
    /// (§11/§12), а не полагаться на пустоту List.</summary>
    IEnumerable<DirEntry> List(RepoSnapshot snapshot, IReadOnlyList<string> segments);
}
