namespace Gitfs.Vfs;

/// <summary>Настройки одного монтирования — то, что в макете лежит под
/// «Advanced». Собраны в одном месте, потому что расходятся по трём слоям:
/// лимиты идут во вьюхи, размеры кэшей в снапшот, режим тома в адаптер.
///
/// Каждое поле здесь ДЕЙСТВУЕТ. Показать в диалоге переключатель, который
/// ничего не меняет, — худший вид неправды: пользователь считает, что
/// настроил, а поведение прежнее.</summary>
public sealed record MountOptions
{
    /// <summary>ПОРЯДОК ОБЪЯВЛЕНИЯ ЗДЕСЬ ЗНАЧИМ. Статические поля
    /// инициализируются сверху вниз, и когда AllViews стоял ниже Default,
    /// `new()` внутри Default брал ещё не заполненный массив: Views уходил
    /// в null, и КАЖДОЕ монтирование без --views падало с «Value cannot be
    /// null (Parameter 'source')». Компилятор об этом не предупреждает.</summary>
    public static readonly string[] AllViews =
        { "branches", "tags", "commits", "dates", "history" };

    public static readonly MountOptions Default = new();

    /// <summary>Какие вьюхи показывать. Пусто — все пять: том без вьюх
    /// бессмыслен, а «по умолчанию всё» — то, чего ждёт человек, впервые
    /// вводящий `gitfs mount`.</summary>
    public IReadOnlyCollection<string> Views { get; init; } = AllViews;

    /// <summary>Опорная точка для history/ и dates/. Пусто — HEAD.
    /// Имя разрешается как ref (main, refs/tags/v1.0) или как SHA.</summary>
    public string? HistoryRef { get; init; }

    /// <summary>Сколько коммитов перечисляет commits/. Разрешение по полному
    /// SHA работает и за пределами этого числа — листинг не равен доступу.</summary>
    public int CommitLimit { get; init; } = 200;

    /// <summary>Потолок версий в history/&lt;файл&gt;/. Достигнут — появляется
    /// .truncated, потому что молчаливое усечение недопустимо.</summary>
    public int HistoryLimit { get; init; } = 500;

    /// <summary>Бюджет кэша деревьев и листингов, МБ. Считается в БАЙТАХ
    /// содержимого, а не в числе записей: одно дерево может весить мегабайты.</summary>
    public int CacheMegabytes { get; init; } = 96;

    /// <summary>Потолок для одного объекта в кэше, МБ. Крупный блоб не должен
    /// вытеснять собой весь кэш ради одного чтения.</summary>
    public int MaxCachedBlobMegabytes { get; init; } = 8;

    /// <summary>native — правила текущей платформы; portable — правила Windows
    /// везде, чтобы дерево выглядело одинаково на всех системах.</summary>
    public NamePolicyKind NamePolicy { get; init; } = NamePolicyKind.Native;

    /// <summary>Том только на чтение. Выключено по умолчанию: жёсткий
    /// read-only ломает Word и Excel, которые пишут lock-файлы рядом с
    /// открываемым файлом.</summary>
    public bool ReadOnly { get; init; }

    /// <summary>Оставить песочницу после размонтирования — чтобы посмотреть,
    /// что именно туда написали. Обычно она удаляется вместе с томом.</summary>
    public bool KeepOverlay { get; init; }

    /// <summary>Проверка вводимых значений. Диалог показывает это сообщение
    /// рядом с полем; null — всё в порядке.</summary>
    public string? Validate()
    {
        if (CommitLimit is < 1 or > 100_000)
            return "commit limit must be between 1 and 100000";
        if (HistoryLimit is < 1 or > 100_000)
            return "history limit must be between 1 and 100000";
        if (CacheMegabytes is < 1 or > 4096)
            return "cache must be between 1 and 4096 MB";
        if (MaxCachedBlobMegabytes is < 1 or > 1024)
            return "max cached blob must be between 1 and 1024 MB";
        if (MaxCachedBlobMegabytes > CacheMegabytes)
            return "max cached blob cannot exceed the whole cache";
        return null;
    }
}
