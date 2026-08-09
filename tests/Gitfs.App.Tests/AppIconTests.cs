namespace Gitfs.App.Tests;

/// <summary>Иконка приложения и тома. Бриф §2.1: мультиразмер 16/24/32/48/256,
/// «должна отличимо читаться рядом со стандартными дисками», §7: «иконки
/// читаются в 16 px».
///
/// В репозитории лежал .ico с ОДНИМ изображением 32×32: систему заставляли
/// растягивать его и в 256, и ужимать в 16 — то есть требование проверить
/// было не на чем. Файл собирается tools/build-icon.csx и лежит в
/// репозитории, поэтому проверяется как обычные данные.</summary>
public class AppIconTests
{
    private static string IconPath()
    {
        // от каталога сборки вверх до корня репозитория
        var dir = AppContext.BaseDirectory;
        for (var i = 0; i < 8 && dir is not null; i++)
        {
            var candidate = Path.Combine(dir, "src", "Gitfs.App", "Assets", "gitfs.ico");
            if (File.Exists(candidate)) return candidate;
            dir = Path.GetDirectoryName(dir);
        }
        throw new FileNotFoundException("gitfs.ico not found above " + AppContext.BaseDirectory);
    }

    private static (int Width, int Height, int Length, int Offset)[] Entries(byte[] ico)
    {
        Assert.True(ico.Length > 22, "the icon is too small to be an icon");
        Assert.Equal(0, BitConverter.ToUInt16(ico, 0));      // reserved
        Assert.Equal(1, BitConverter.ToUInt16(ico, 2));      // тип: иконка
        var count = BitConverter.ToUInt16(ico, 4);

        var entries = new (int, int, int, int)[count];
        for (var i = 0; i < count; i++)
        {
            var at = 6 + 16 * i;
            entries[i] = (
                ico[at] == 0 ? 256 : ico[at],
                ico[at + 1] == 0 ? 256 : ico[at + 1],
                BitConverter.ToInt32(ico, at + 8),
                BitConverter.ToInt32(ico, at + 12));
        }
        return entries;
    }

    [Fact]
    public void The_icon_carries_more_than_one_size()
    {
        var entries = Entries(File.ReadAllBytes(IconPath()));
        Assert.True(entries.Length >= 4,
            $"only {entries.Length} size(s) in the icon; the brief asks for a set");
    }

    [Fact]
    public void It_reads_at_sixteen_pixels_and_scales_to_two_hundred_fifty_six()
    {
        var sizes = Entries(File.ReadAllBytes(IconPath())).Select(e => e.Width).ToList();
        Assert.Contains(16, sizes);      // трей, фавикон, мелкий вид Проводника
        Assert.Contains(256, sizes);     // плитки и «крупные значки»
    }

    [Fact]
    public void Every_image_is_square_and_inside_the_file()
    {
        var ico = File.ReadAllBytes(IconPath());
        foreach (var (width, height, length, offset) in Entries(ico))
        {
            Assert.Equal(width, height);
            Assert.InRange(offset, 22, ico.Length);
            Assert.InRange(offset + length, offset, ico.Length);
            Assert.True(length > 0, $"the {width}px image is empty");
        }
    }

    /// <summary>Каждое изображение — настоящий PNG. Битая полезная нагрузка
    /// даёт иконку, которую система молча не покажет: ошибки не будет,
    /// просто пустое место.</summary>
    [Fact]
    public void Every_payload_is_a_real_png()
    {
        var ico = File.ReadAllBytes(IconPath());
        foreach (var (width, _, _, offset) in Entries(ico))
        {
            Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47 },
                ico[offset..(offset + 4)]);

            // и заголовок PNG объявляет тот же размер, что и запись каталога:
            // расхождение здесь — иконка, которая масштабируется не туда
            var declared = (ico[offset + 16] << 24) | (ico[offset + 17] << 16)
                           | (ico[offset + 18] << 8) | ico[offset + 19];
            Assert.Equal(width, declared);
        }
    }
}
