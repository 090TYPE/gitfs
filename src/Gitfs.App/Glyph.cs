using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Gitfs.App;

/// <summary>Знак комплекта как ОДИН элемент.
///
/// Раньше знак в разметке собирался из двух Path в общем Canvas: обводка и
/// залитые детали. Avalonia приводит геометрию каждой фигуры Shape к началу
/// её собственных границ — и точка внутри метки, кружок в коммите, три
/// точки в календаре уезжали в угол значка. Растровые иконки рисуются
/// напрямую в DrawingContext и потому выглядели правильно: расхождение было
/// видно только при сравнении окна со снимком иконок, никакой тест на это
/// не смотрел.
///
/// Здесь рисование одно на всех — то же, что в <see cref="IconRenderer"/>:
/// одна система координат 24×24, обводка и заливка в ней же.</summary>
public class Glyph : Control
{
    /// <summary>Поле, в котором нарисованы исходники дизайн-отдела.</summary>
    public const double Design = 24.0;

    public static readonly StyledProperty<string?> KeyProperty =
        AvaloniaProperty.Register<Glyph, string?>(nameof(Key));

    public static readonly StyledProperty<IBrush?> StrokeProperty =
        AvaloniaProperty.Register<Glyph, IBrush?>(nameof(Stroke));

    /// <summary>Кисть залитых деталей. Не задана — та же, что у обводки:
    /// в комплекте они всегда одного цвета (fill="currentColor").</summary>
    public static readonly StyledProperty<IBrush?> FillProperty =
        AvaloniaProperty.Register<Glyph, IBrush?>(nameof(Fill));

    public static readonly StyledProperty<double> ThicknessProperty =
        AvaloniaProperty.Register<Glyph, double>(nameof(Thickness), 1.7);

    public string? Key
    {
        get => GetValue(KeyProperty);
        set => SetValue(KeyProperty, value);
    }

    public IBrush? Stroke
    {
        get => GetValue(StrokeProperty);
        set => SetValue(StrokeProperty, value);
    }

    public IBrush? Fill
    {
        get => GetValue(FillProperty);
        set => SetValue(FillProperty, value);
    }

    public double Thickness
    {
        get => GetValue(ThicknessProperty);
        set => SetValue(ThicknessProperty, value);
    }

    static Glyph()
    {
        AffectsRender<Glyph>(KeyProperty, StrokeProperty, FillProperty, ThicknessProperty);
    }

    public override void Render(DrawingContext context)
    {
        if (Key is not { } key || Stroke is not { } stroke) return;
        if (Lookup(key + "Stroke") is not { } outline) return;

        var size = Math.Min(Bounds.Width, Bounds.Height);
        if (size <= 0) return;
        var scale = size / Design;

        using (context.PushTransform(Matrix.CreateScale(scale, scale)))
        {
            context.DrawGeometry(null, new Pen(stroke, Thickness)
            {
                LineCap = PenLineCap.Round,
                LineJoin = PenLineJoin.Round,
            }, outline);
            // Залитые детали обводкой дают кольцо вместо точки, поэтому
            // геометрия для них отдельная — и рисуется в ЭТИХ ЖЕ координатах.
            if (Lookup(key + "Fill") is { } solid)
                context.DrawGeometry(Fill ?? stroke, null, solid);
        }
    }

    /// <summary>Геометрия по ключу из Icons.axaml. Нет такой — знак просто не
    /// рисуется: валить окно из-за опечатки в имени значка незачем.</summary>
    private static Geometry? Lookup(string key)
    {
        var app = Application.Current;
        if (app is null) return null;
        return app.TryGetResource(key, app.ActualThemeVariant, out var value)
            ? value as Geometry
            : null;
    }

    /// <summary>Знак из кода — для дерева превью и панелей, которые строятся
    /// руками и до ресурсов разметки не дотягиваются.</summary>
    public static Glyph Make(string key, double size, IBrush stroke, double thickness = 1.7) =>
        new() { Key = key, Stroke = stroke, Thickness = thickness, Width = size, Height = size };
}
