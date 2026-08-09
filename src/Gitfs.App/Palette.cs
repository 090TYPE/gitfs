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
    public static IBrush Text => Brush("GfsTextBrush", "#e9e9ed");
    public static IBrush Muted => Brush("GfsTextMutedBrush", "#9397ab");
    public static IBrush Faint => Brush("GfsTextFaintBrush", "#75798c");
    public static IBrush Accent => Brush("GfsAccentBrush", "#9184d9");
    public static IBrush AccentInk => Brush("GfsAccentInkBrush", "#d2cefd");
    public static IBrush Ok => Brush("GfsOkBrush", "#7FB77E");
    public static IBrush Warn => Brush("GfsWarnBrush", "#E0B26D");
    public static IBrush Err => Brush("GfsErrBrush", "#E07B6D");
    public static IBrush Strike => Brush("GfsStrikeBrush", "#595d6c");

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
