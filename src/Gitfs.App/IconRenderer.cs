using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace Gitfs.App;

/// <summary>Рисует знаки дизайн-отдела в мультиразмерные .ico.
///
/// Запускается вручную и редко:
///
///   GITFS_UI_PREVIEW=icons Gitfs.App           → src/Gitfs.Vfs/Assets/*.ico
///
/// Почему через приложение, а не отдельным инструментом: рисовать нужно
/// НАСТОЯЩИЕ пути дизайнера, а векторный растеризатор в проекте только один —
/// тот, что уже тянет Avalonia. Перерисовывать знаки примитивами руками
/// значило бы выдать свою работу за чужую, а изображения потом расходились бы
/// с исходниками при первой же правке.
///
/// Результат лежит в репозитории обычными файлами и участвует в ревью:
/// это ассеты, а не шаг сборки.</summary>
internal static class IconRenderer
{
    /// <summary>Вьюха → ключ геометрии в Icons.axaml.
    ///
    /// Знаков маркеров (.truncated, .gitfs-submodule) здесь НЕТ, и это не
    /// недоделка. Иконку отдельному ФАЙЛУ Проводник берёт по расширению — из
    /// реестра, для всей системы сразу. Записать туда «.truncated выглядит
    /// так» значит поменять вид этих файлов у человека везде, включая те, что
    /// к gitfs отношения не имеют. Ради украшения двух служебных имён такого
    /// не делают; сами имена говорят о себе словами, а не значком.</summary>
    public static readonly (string View, string Key)[] Views =
    {
        ("branches", "IcViewBranches"),
        ("tags", "IcViewTags"),
        ("commits", "IcViewCommits"),
        ("dates", "IcViewDates"),
        ("history", "IcViewHistory"),
        (".gitfs", "IcViewSystem"),
        ("volume", "IcMark"),
    };

    /// <summary>Мелкие размеры рисуются упрощённым знаком. Ради этого он и
    /// нарисован: в 16 px внутренняя деталь полного знака сливается в кашу —
    /// три линии и две точки на площади с ноготь. Раньше упрощённый брали
    /// ВСЕГДА, и в 256 px том получал знак без половины смысла.</summary>
    private static string KeyFor(string key, int size) =>
        key == "IcMark" && size <= 32 ? "IcMarkMin" : key;

    private static readonly int[] Sizes = { 16, 32, 48, 256 };

    public static int RenderAll(string targetDirectory)
    {
        Directory.CreateDirectory(targetDirectory);
        var written = 0;
        foreach (var (view, key) in Views)
        {
            var images = new List<(int Size, byte[] Png)>();
            foreach (var size in Sizes)
            {
                if (Render(KeyFor(key, size), size) is { } png) images.Add((size, png));
            }
            if (images.Count == 0)
            {
                Console.Error.WriteLine($"no geometry for {key}");
                continue;
            }
            var name = view == ".gitfs" ? "system" : view;
            File.WriteAllBytes(Path.Combine(targetDirectory, name + ".ico"),
                TrayBadge.BuildIco(images));
            written++;
            Console.WriteLine($"{name}.ico — {string.Join(", ", images.Select(i => i.Size))}");
        }
        return written;
    }

    /// <summary>Один размер. null — геометрии с таким ключом нет.</summary>
    private static byte[]? Render(string key, int size)
    {
        if (Application.Current is not { } app) return null;
        if (!app.TryGetResource(key + "Stroke", app.ActualThemeVariant, out var strokeValue)
            || strokeValue is not Geometry stroke)
        {
            return null;
        }
        app.TryGetResource(key + "Fill", app.ActualThemeVariant, out var fillValue);

        // Знаки нарисованы в поле 24×24; масштабируем и оставляем поля,
        // иначе обводка обрезается по краю на мелких размерах.
        var scale = size / 24.0 * 0.92;
        var offset = (size - 24 * scale) / 2;

        var bitmap = new RenderTargetBitmap(new PixelSize(size, size), new Vector(96, 96));
        using (var context = bitmap.CreateDrawingContext())
        {
            var brush = new SolidColorBrush(Color.Parse(Brand.AccentHex));
            using (context.PushTransform(Matrix.CreateScale(scale, scale)
                                         * Matrix.CreateTranslation(offset, offset)))
            {
                // Толщина обводки НЕ масштабируется вместе с формой: в 16 px
                // волосяная линия исчезает, и знак читается пятном.
                var thickness = size <= 16 ? 2.2 : size <= 32 ? 1.9 : 1.7;
                context.DrawGeometry(null, new Pen(brush, thickness)
                {
                    LineCap = PenLineCap.Round,
                    LineJoin = PenLineJoin.Round,
                }, stroke);
                if (fillValue is Geometry fill) context.DrawGeometry(brush, null, fill);
            }
        }

        using var stream = new MemoryStream();
        bitmap.Save(stream);          // Avalonia пишет PNG
        return stream.ToArray();
    }
}
