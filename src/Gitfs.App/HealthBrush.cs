using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace Gitfs.App;

/// <summary>Цвет индикатора по состоянию тома. Отдельный конвертер, а не
/// свойство у MountEntry: цвет зависит от ТЕМЫ, а запись о монтировании о
/// теме ничего не знает и знать не должна.
///
/// Цвет здесь — добавка, а не носитель смысла: рядом с кружком стоит слово
/// («ok» / «reopening»), потому что состояние, различимое только по оттенку,
/// молчит для тех, кто этот оттенок не различает.</summary>
public sealed class HealthBrush : IValueConverter
{
    public static readonly HealthBrush Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is MountHealth.Reopening ? Palette.Warn : Palette.Ok;

    public object ConvertBack(object? value, Type targetType, object? parameter,
        CultureInfo culture) => throw new NotSupportedException();
}
