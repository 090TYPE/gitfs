using Avalonia;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Styling;

namespace Gitfs.App;

/// <summary>Движение в интерфейсе — включаемое.
///
/// Анимация здесь ровно одна: пульс у тома, под которым пересобрались
/// пакеты. Но выключатель ей нужен всё равно — движение на краю зрения
/// мешает читать, а кому-то от него физически плохо. Система про своё
/// «reduce motion» кроссплатформенно не рассказывает (Avalonia 11 такого
/// свойства не отдаёт), поэтому спрашиваем человека в настройках, а не
/// угадываем по платформе.
///
/// Реализовано добавлением и удалением НАБОРА СТИЛЕЙ: правило, которого нет
/// в приложении, не может сработать. Гасить анимацию условием внутри стиля
/// пришлось бы в каждом её месте, и первое же новое движение про условие
/// забыло бы.</summary>
internal static class Motion
{
    private static readonly Uri Source =
        new("avares://Gitfs.App/Motion.axaml");

    private static IStyle? _styles;

    /// <summary>Приводит стили приложения в согласие с настройкой. Зовётся
    /// при запуске и при каждом изменении переключателя: окна, уже
    /// открытые, обязаны перестать двигаться сразу — настройку меняют
    /// именно потому, что движение мешает СЕЙЧАС.</summary>
    public static void Apply()
    {
        if (Application.Current is not { } app) return;

        var wanted = !Settings.ReduceMotion;
        var present = _styles is not null && app.Styles.Contains(_styles);
        if (wanted == present) return;

        if (wanted)
        {
            _styles ??= new StyleInclude(Source) { Source = Source };
            app.Styles.Add(_styles);
        }
        else if (_styles is not null)
        {
            app.Styles.Remove(_styles);
        }
    }
}
