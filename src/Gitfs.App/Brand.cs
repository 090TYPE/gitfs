namespace Gitfs.App;

/// <summary>Фирменные цвета одним написанием.
///
/// Акцент был написан ТРИЖДЫ и по-разному: токен темы #9184d9, литерал в
/// растеризаторе иконок #9184D9 и значок в трее (0x8B,0x80,0xDE). Третий не
/// принадлежит рампе макета вообще — его никто не выбирал, он получился.
/// Рядом это не видно: значок в трее 16 px, а иконка вьюхи — в Проводнике,
/// и глазом расхождение не поймать. Поэтому оно и прожило.
///
/// Здесь лежат роли в том виде, в каком они нужны ВНЕ системы тем: значок
/// трея рисуется прямо в байты, иконки растеризуются до готовности
/// приложения, а <see cref="Palette"/> нужен запасной цвет, пока словари
/// ресурсов ещё не загружены. Значения — тёмная тема, она основная.
///
/// Совпадение с Theme.axaml не на честном слове: ThemeTokensTests читает
/// словарь Dark и сверяет каждую роль с этим файлом.</summary>
internal static class Brand
{
    /// <summary>Акцент, ступень accent-500 рампы Nocturne. Из
    /// docs/design/ui/nocturne.css: «the accent moves to #9184d9».</summary>
    public const string AccentHex = "#9184d9";

    public const string AccentInkHex = "#d2cefd";
    public const string TextHex = "#e9e9ed";
    public const string TextMutedHex = "#9397ab";
    public const string TextFaintHex = "#75798c";
    public const string StrikeHex = "#595d6c";

    /// <summary>Терминал — единственная роль, ОДИНАКОВАЯ в обеих темах
    /// (tokens.css: «--term не переопределяется»). Цитата чужого текста —
    /// журнала, вывода команды — обязана отличаться от собственных слов окна.
    /// #161826, смешанный с чёрным на 74%.</summary>
    public const string TermHex = "#10121c";
    public const string TermInkHex = "#9397ab";

    /// <summary>Семантика — один тон на роль, отдельно от акцента (бриф §1.2).</summary>
    public const string OkHex = "#7fb77e";
    public const string WarnHex = "#e0b26d";
    public const string ErrHex = "#e07b6d";

    // Те же цвета байтами: растровый значок трея собирается без Avalonia.
    public static readonly (byte R, byte G, byte B) Accent = Bytes(AccentHex);
    public static readonly (byte R, byte G, byte B) Warn = Bytes(WarnHex);
    public static readonly (byte R, byte G, byte B) Err = Bytes(ErrHex);

    /// <summary>Разбор #rrggbb. Написать кортеж руками рядом с hex — ровно тот
    /// способ, которым и разъехался акцент; пусть его считает машина.</summary>
    public static (byte R, byte G, byte B) Bytes(string hex)
    {
        var digits = hex.TrimStart('#');
        return (Convert.ToByte(digits[..2], 16),
                Convert.ToByte(digits.Substring(2, 2), 16),
                Convert.ToByte(digits.Substring(4, 2), 16));
    }
}
