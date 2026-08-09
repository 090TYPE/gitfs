using Avalonia;
using Avalonia.Media;

namespace Gitfs.App;

/// <summary>Доступ к ролям палитры ИЗ КОДА. Разметка берёт их через
/// DynamicResource, а элементы, которые строятся руками (строки doctor,
/// дерево превью, фишки недавних), не могут — и раньше несли литеральные
/// hex. Из-за этого одна и та же роль была разного цвета в разных окнах:
/// «ok» — #6FBF77 в менеджере и #7FB77E на первом запуске.
///
/// Ищем ресурс с учётом ТЕКУЩЕЙ темы: то же имя в светлой и тёмной даёт
/// разный цвет, и без варианта вернулся бы тёмный на светлом фоне.</summary>
internal static class Palette
{
    public static IBrush Text => Brush("GfsTextBrush", Brand.TextHex);
    public static IBrush Muted => Brush("GfsTextMutedBrush", Brand.TextMutedHex);
    public static IBrush Faint => Brush("GfsTextFaintBrush", Brand.TextFaintHex);
    public static IBrush Accent => Brush("GfsAccentBrush", Brand.AccentHex);
    public static IBrush AccentInk => Brush("GfsAccentInkBrush", Brand.AccentInkHex);
    public static IBrush Ok => Brush("GfsOkBrush", Brand.OkHex);
    public static IBrush Warn => Brush("GfsWarnBrush", Brand.WarnHex);
    public static IBrush Err => Brush("GfsErrBrush", Brand.ErrHex);
    public static IBrush Strike => Brush("GfsStrikeBrush", Brand.StrikeHex);

    /// <summary>Чернила терминала. Сам фон ставит стиль Border.term — он
    /// один на все блоки; из кода нужен только цвет текста.</summary>
    public static IBrush TermInk => Brush("GfsTermInkBrush", Brand.TermInkHex);

    /// <summary>Моноширинная роль — та же, что в разметке. В коде она была
    /// написана руками и КОРОЧЕ: «Cascadia Mono, Consolas, monospace» без
    /// DejaVu, Liberation и Menlo. На Linux и macOS элементы, собранные
    /// кодом, падали на generic monospace, а соседние из разметки — нет:
    /// одна роль, два разных шрифта в одном окне.</summary>
    public static FontFamily Mono
    {
        get
        {
            var app = Application.Current;
            if (app is not null
                && app.TryGetResource("GfsMono", app.ActualThemeVariant, out var value)
                && value is FontFamily family)
            {
                return family;
            }
            return new FontFamily("Cascadia Mono, Consolas, DejaVu Sans Mono, Menlo, monospace");
        }
    }

    public static IBrush ForStatus(Gitfs.Diagnostics.CheckStatus status) => status switch
    {
        Gitfs.Diagnostics.CheckStatus.Ok => Ok,
        Gitfs.Diagnostics.CheckStatus.Warn => Warn,
        _ => Err,
    };

    private static IBrush Brush(string key, string fallback)
    {
        var app = Application.Current;
        if (app is not null)
        {
            var variant = app.ActualThemeVariant;
            if (app.TryGetResource(key, variant, out var value) && value is IBrush brush)
                return brush;
        }
        // Запасной цвет — не «на всякий случай»: до готовности приложения
        // ресурсов ещё нет, а рисовать чем-то надо. Значения совпадают с
        // тёмной темой, потому что она по-прежнему основная.
        return new SolidColorBrush(Color.Parse(fallback));
    }
}
