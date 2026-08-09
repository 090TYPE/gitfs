using Gitfs.App;
using Gitfs.Diagnostics;

namespace Gitfs.App.Tests;

/// <summary>Иконка трея (макет 02). Проверяется по пикселям и байтам:
/// «состояние читается формой» — утверждение о картинке, и подтвердить его
/// можно только посмотрев на картинку, пусть и программой.</summary>
public class TrayBadgeTests
{
    // ---------- состояние ----------

    [Fact]
    public void Nothing_mounted_and_nothing_wrong_is_idle()
    {
        Assert.Equal(TrayState.Idle, TrayBadge.StateOf(0, Ok()));
    }

    [Fact]
    public void Mounted_volumes_make_the_icon_say_so()
    {
        Assert.Equal(TrayState.Mounted, TrayBadge.StateOf(3, Ok()));
    }

    /// <summary>Отказ важнее числа томов: смонтировано три, но драйвера нет —
    /// человек должен увидеть крест, а не спокойную тройку.</summary>
    [Fact]
    public void A_failed_check_outranks_the_mount_count()
    {
        var checks = new[]
        {
            new Check(CheckStatus.Ok, "git", "2.43"),
            new Check(CheckStatus.Fail, "winfsp", "not installed"),
        };
        Assert.Equal(TrayState.Error, TrayBadge.StateOf(3, checks));
    }

    [Fact]
    public void A_warning_outranks_the_mount_count_too_but_yields_to_a_failure()
    {
        var warn = new[] { new Check(CheckStatus.Warn, "disk", "97% full") };
        Assert.Equal(TrayState.Degraded, TrayBadge.StateOf(2, warn));

        var both = new[]
        {
            new Check(CheckStatus.Warn, "disk", "97% full"),
            new Check(CheckStatus.Fail, "winfsp", "not installed"),
        };
        Assert.Equal(TrayState.Error, TrayBadge.StateOf(2, both));
    }

    // ---------- форма, а не цвет ----------

    /// <summary>Главное обещание макета. Четыре состояния сравниваются по
    /// СИЛУЭТУ — по маске непрозрачных пикселей, без единого цвета. Если
    /// различие держится только на цвете, эта проверка падает.</summary>
    [Theory]
    [InlineData(32, true)]
    [InlineData(16, false)]   // именно здесь крест однажды рассыпался в точки
    public void Every_state_has_its_own_silhouette_in_monochrome(int size, bool withNumber)
    {
        var shapes = new Dictionary<TrayState, string>();
        foreach (var state in new[]
                 { TrayState.Idle, TrayState.Mounted, TrayState.Degraded, TrayState.Error })
        {
            shapes[state] = Silhouette(TrayBadge.Draw(size, state, 3, withNumber));
        }

        var distinct = shapes.Values.Distinct().Count();
        Assert.True(distinct == shapes.Count,
            $"at {size}px two states draw the same shape and differ only by colour: " +
            $"{distinct} of {shapes.Count}");
    }

    /// <summary>Бейдж обязан быть СВЯЗНЫМ пятном. Вырез по мелкому кругу
    /// рассыпал крест в горсть точек: формально силуэт отличался от прочих,
    /// а глазами читался как шум. Проверка считает связные области.</summary>
    [Theory]
    [InlineData(TrayState.Mounted)]
    [InlineData(TrayState.Degraded)]
    [InlineData(TrayState.Error)]
    public void The_badge_is_one_blob_and_not_a_scatter_of_dots(TrayState state)
    {
        var raster = TrayBadge.Draw(16, state, 3, withNumber: false);
        var badgeOnly = BadgeRegion(raster);
        var blobs = CountBlobs(badgeOnly, raster.Width);
        Assert.True(blobs <= 2, $"the 16px badge for {state} fell apart into {blobs} pieces");
    }

    [Fact]
    public void The_number_of_volumes_changes_the_picture()
    {
        // иначе бейдж «сколько-то» вместо бейджа «три»
        var one = Silhouette(TrayBadge.Draw(32, TrayState.Mounted, 1, withNumber: true));
        var two = Silhouette(TrayBadge.Draw(32, TrayState.Mounted, 2, withNumber: true));
        var twelve = Silhouette(TrayBadge.Draw(32, TrayState.Mounted, 12, withNumber: true));

        Assert.NotEqual(one, two);
        Assert.NotEqual(two, twelve);
    }

    /// <summary>В 16 px число не рисуется — макет прямо это оговаривает:
    /// цифра высотой в три пикселя не читается. Остаётся точка.</summary>
    [Fact]
    public void At_sixteen_pixels_the_number_drops_and_a_dot_remains()
    {
        var one = Silhouette(TrayBadge.Draw(16, TrayState.Mounted, 1, withNumber: false));
        var nine = Silhouette(TrayBadge.Draw(16, TrayState.Mounted, 9, withNumber: false));
        Assert.Equal(one, nine);   // число не влияет

        // но бейдж всё-таки есть: он отличается от пустого состояния
        var idle = Silhouette(TrayBadge.Draw(16, TrayState.Idle, 0, withNumber: false));
        Assert.NotEqual(idle, one);
    }

    [Fact]
    public void The_idle_icon_is_the_mark_and_nothing_else()
    {
        // у пустого состояния бейджа нет: правый нижний угол чист
        var idle = TrayBadge.Draw(32, TrayState.Idle, 0, withNumber: true);
        var mounted = TrayBadge.Draw(32, TrayState.Mounted, 1, withNumber: true);

        var idleCorner = CornerInk(idle);
        var mountedCorner = CornerInk(mounted);
        Assert.True(mountedCorner > idleCorner + 20,
            $"the badge barely shows: {idleCorner} vs {mountedCorner} pixels in the corner");
    }

    [Fact]
    public void The_mark_itself_is_drawn_at_both_sizes()
    {
        // иначе «иконка» — это бейдж на пустом месте
        foreach (var size in new[] { 16, 32 })
        {
            var raster = TrayBadge.Draw(size, TrayState.Idle, 0, withNumber: false);
            var painted = Painted(raster);
            Assert.True(painted > size, $"{size}px icon has only {painted} painted pixels");
        }
    }

    // ---------- байты ----------

    [Fact]
    public void The_icon_is_a_well_formed_ico_with_both_sizes()
    {
        var bytes = TrayBadge.BuildIcon(TrayState.Mounted, 2);

        Assert.Equal(0, BitConverter.ToUInt16(bytes, 0));   // reserved
        Assert.Equal(1, BitConverter.ToUInt16(bytes, 2));   // тип: иконка
        var count = BitConverter.ToUInt16(bytes, 4);
        Assert.Equal(2, count);

        var sizes = new List<int>();
        for (var i = 0; i < count; i++)
        {
            var entry = 6 + 16 * i;
            var width = bytes[entry] == 0 ? 256 : bytes[entry];
            var length = BitConverter.ToInt32(bytes, entry + 8);
            var offset = BitConverter.ToInt32(bytes, entry + 12);

            Assert.Equal(bytes[entry], bytes[entry + 1]);         // квадратная
            Assert.InRange(offset, 6 + 16 * count, bytes.Length);
            Assert.InRange(offset + length, offset, bytes.Length); // не за пределы
            // и по этому смещению действительно лежит PNG
            Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47 }, bytes[offset..(offset + 4)]);
            sizes.Add(width);
        }
        Assert.Equal(new[] { 16, 32 }, sizes);
    }

    /// <summary>PNG собирается вручную, поэтому CRC каждого блока проверяется
    /// отдельно: битый CRC — это иконка, которую система молча не покажет.</summary>
    [Fact]
    public void Every_png_chunk_carries_a_correct_checksum()
    {
        var png = TrayBadge.EncodePng(TrayBadge.Draw(32, TrayState.Error, 0, withNumber: true));
        Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }, png[..8]);

        var types = new List<string>();
        var at = 8;
        while (at < png.Length)
        {
            var length = ReadBigEndian(png, at);
            var type = System.Text.Encoding.ASCII.GetString(png, at + 4, 4);
            var declared = (uint)ReadBigEndian(png, at + 8 + length);
            var actual = Crc32(png.AsSpan(at + 4, 4 + length));
            Assert.True(declared == actual, $"{type} chunk has a bad CRC");
            types.Add(type);
            at += 12 + length;
        }
        Assert.Equal(new[] { "IHDR", "IDAT", "IEND" }, types);
        Assert.Equal(png.Length, at);   // ни одного лишнего байта
    }

    [Fact]
    public void The_png_header_describes_the_image_that_was_drawn()
    {
        var png = TrayBadge.EncodePng(TrayBadge.Draw(32, TrayState.Idle, 0, withNumber: false));
        Assert.Equal(32, ReadBigEndian(png, 16));   // width
        Assert.Equal(32, ReadBigEndian(png, 20));   // height
        Assert.Equal(8, png[24]);                   // бит на канал
        Assert.Equal(6, png[25]);                   // RGBA
    }

    /// <summary>Не проверка, а глаза: кладёт все состояния на диск, чтобы на
    /// них можно было посмотреть. Молчит, пока не попросили — переменная
    /// GITFS_TRAY_DUMP задаёт каталог. Утверждения о картинке проверяются
    /// выше программой, но «выглядит ли это иконкой» решает только человек.</summary>
    [Fact]
    public void Dump_every_state_when_asked()
    {
        var dir = Environment.GetEnvironmentVariable("GITFS_TRAY_DUMP");
        if (string.IsNullOrWhiteSpace(dir)) return;
        Directory.CreateDirectory(dir);

        foreach (var (state, count, name) in new[]
        {
            (TrayState.Idle, 0, "idle"),
            (TrayState.Mounted, 1, "mounted-1"),
            (TrayState.Mounted, 3, "mounted-3"),
            (TrayState.Mounted, 12, "mounted-12"),
            (TrayState.Degraded, 2, "degraded"),
            (TrayState.Error, 0, "error"),
        })
        {
            File.WriteAllBytes(Path.Combine(dir, $"{name}-32.png"),
                TrayBadge.EncodePng(TrayBadge.Draw(32, state, count, withNumber: true)));
            File.WriteAllBytes(Path.Combine(dir, $"{name}-16.png"),
                TrayBadge.EncodePng(TrayBadge.Draw(16, state, count, withNumber: false)));
            File.WriteAllBytes(Path.Combine(dir, $"{name}.ico"),
                TrayBadge.BuildIcon(state, count));
        }
    }

    // ---------- служебное ----------

    private static Check[] Ok() => new[] { new Check(CheckStatus.Ok, "git", "2.43") };

    /// <summary>Маска непрозрачных пикселей — картинка без цвета.</summary>
    private static string Silhouette(TrayBadge.Raster r)
    {
        var sb = new System.Text.StringBuilder(r.Width * r.Height);
        for (var y = 0; y < r.Height; y++)
            for (var x = 0; x < r.Width; x++)
                sb.Append(r.Alpha(x, y) > 0 ? '#' : '.');
        return sb.ToString();
    }

    private static int Painted(TrayBadge.Raster r)
    {
        var n = 0;
        for (var y = 0; y < r.Height; y++)
            for (var x = 0; x < r.Width; x++)
                if (r.Alpha(x, y) > 0) n++;
        return n;
    }

    private static int CornerInk(TrayBadge.Raster r)
    {
        var n = 0;
        for (var y = r.Height / 2; y < r.Height; y++)
            for (var x = r.Width / 2; x < r.Width; x++)
                if (r.Alpha(x, y) > 0) n++;
        return n;
    }

    /// <summary>Только правый нижний квадрант — там живёт бейдж; знак папки
    /// в счёт не идёт, иначе считались бы его собственные части.</summary>
    private static bool[] BadgeRegion(TrayBadge.Raster r)
    {
        var mask = new bool[r.Width * r.Height];
        for (var y = r.Height / 2; y < r.Height; y++)
            for (var x = r.Width / 2; x < r.Width; x++)
                mask[y * r.Width + x] = r.Alpha(x, y) > 0;
        return mask;
    }

    private static int CountBlobs(bool[] mask, int width)
    {
        var height = mask.Length / width;
        var seen = new bool[mask.Length];
        var blobs = 0;
        for (var i = 0; i < mask.Length; i++)
        {
            if (!mask[i] || seen[i]) continue;
            blobs++;
            var queue = new Queue<int>();
            queue.Enqueue(i);
            seen[i] = true;
            while (queue.Count > 0)
            {
                var p = queue.Dequeue();
                var x = p % width;
                var y = p / width;
                foreach (var (dx, dy) in new[] { (1, 0), (-1, 0), (0, 1), (0, -1) })
                {
                    var nx = x + dx;
                    var ny = y + dy;
                    if (nx < 0 || ny < 0 || nx >= width || ny >= height) continue;
                    var n = ny * width + nx;
                    if (!mask[n] || seen[n]) continue;
                    seen[n] = true;
                    queue.Enqueue(n);
                }
            }
        }
        return blobs;
    }

    private static int ReadBigEndian(byte[] b, int at) =>
        (b[at] << 24) | (b[at + 1] << 16) | (b[at + 2] << 8) | b[at + 3];

    private static uint Crc32(ReadOnlySpan<byte> data)
    {
        var c = 0xFFFFFFFFu;
        foreach (var b in data)
        {
            c ^= b;
            for (var k = 0; k < 8; k++)
                c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
        }
        return c ^ 0xFFFFFFFFu;
    }
}
