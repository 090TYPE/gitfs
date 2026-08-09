using System.IO.Compression;

namespace Gitfs.App;

/// <summary>Состояние gitfs, читаемое с иконки в трее.</summary>
public enum TrayState
{
    /// <summary>Ничего не смонтировано и всё в порядке — бейджа нет.</summary>
    Idle,
    /// <summary>Тома есть — круг с их числом.</summary>
    Mounted,
    /// <summary>Doctor предупреждает — треугольник.</summary>
    Degraded,
    /// <summary>Doctor нашёл отказ — крест.</summary>
    Error,
}

/// <summary>Иконка трея со значком состояния, нарисованная в памяти
/// (макет 02).
///
/// Состояние читается ФОРМОЙ, а не цветом: круг, треугольник, крест. Цвет
/// добавляет, но ничего не решает — иначе иконка молчит для тех, кто не
/// различает красный и зелёный, и в монохромной теме панели задач.
///
/// Рисуется байтами, без графической подсистемы: во-первых, иконка нужна
/// раньше первого окна; во-вторых, так её можно проверить обычным тестом,
/// а не человеком, который посмотрел в угол экрана. PNG собирается вручную
/// (IHDR/IDAT/IEND + CRC32), ICO — из двух изображений, 16 и 32.</summary>
public static class TrayBadge
{
    // Палитра макета: акцент, предупреждение, отказ.
    private static readonly (byte R, byte G, byte B) Mark = Brand.Accent;
    private static readonly (byte R, byte G, byte B) Warn = Brand.Warn;
    private static readonly (byte R, byte G, byte B) Fail = Brand.Err;

    /// <summary>ICO из четырёх размеров. В 16 px число не рисуется: три
    /// пикселя высоты не читаются ни на одном экране, поэтому там остаётся
    /// точка — ровно то, что предписывает макет.
    ///
    /// Размеров было два, 16 и 32, и на этом всё кончалось. При системном
    /// масштабе 150% и 200% панель задач просит 24 и 48 — а получив только
    /// 32, растягивает их сама, и значок расплывается ровно там, где экран
    /// подробнее всего. Какое изображение взять, решает система; наше дело —
    /// предложить те, что она спрашивает.</summary>
    public static byte[] BuildIcon(TrayState state, int count)
    {
        var images = new List<(int, byte[])>();
        foreach (var size in new[] { 16, 24, 32, 48 })
        {
            // Число появляется там, где его видно: в 16 px цифра высотой в
            // три пикселя — не цифра, а грязь на точке.
            var withNumber = size >= 24;
            images.Add((size, EncodePng(Draw(size, state, count, withNumber))));
        }
        return BuildIco(images);
    }

    /// <summary>Состояние по тому, что приложение действительно знает:
    /// отказ и предупреждение берутся у doctor, число — у монтирований.
    /// Придумывать «деградацию» из ничего нельзя: значок, который загорается
    /// без причины, перестают замечать.</summary>
    public static TrayState StateOf(int mounts, IEnumerable<Gitfs.Diagnostics.Check> checks,
        bool anyDegraded = false)
    {
        var worst = Gitfs.Diagnostics.CheckStatus.Ok;
        foreach (var check in checks)
            if (check.Status > worst) worst = check.Status;

        // Отказ окружения важнее всего: смонтировано три, но драйвера нет —
        // человек должен увидеть крест, а не спокойную тройку.
        if (worst == Gitfs.Diagnostics.CheckStatus.Fail) return TrayState.Error;

        // Деградация — это СОСТОЯНИЕ ТОМА: переоткрытие пакетов, осиротевшая
        // песочница (бриф §4.1). Раньше треугольник зажигало любое
        // предупреждение doctor — например «диск почти полон», к томам
        // отношения не имеющее. Значок, загорающийся не по делу, перестают
        // замечать, и он не срабатывает тогда, когда нужен.
        if (anyDegraded) return TrayState.Degraded;
        if (mounts == 0 && worst == Gitfs.Diagnostics.CheckStatus.Warn) return TrayState.Degraded;
        return mounts > 0 ? TrayState.Mounted : TrayState.Idle;
    }

    // ---------- рисование ----------

    internal static Raster Draw(int size, TrayState state, int count, bool withNumber)
    {
        var r = new Raster(size, size);
        DrawMark(r);

        var badge = size * 9 / 16;                 // ~18 px из 32
        var x = size - badge;
        var y = size - badge;

        switch (state)
        {
            case TrayState.Idle:
                break;
            case TrayState.Mounted:
                r.FillCircle(x + badge / 2, y + badge / 2, badge / 2, Mark);
                // Цифра ВЫРЕЗАЕТСЯ из круга, а не рисуется поверх. Нарисованная
                // поверх, она исчезает там, где панель задач перекрашивает
                // иконку в один тон, — а обещано, что состояние читается и в
                // монохроме. Вырез остаётся вырезом при любой перекраске.
                if (withNumber && count is > 0 and < 100)
                    PunchNumber(r, count, x + badge / 2, y + badge / 2);
                else if (!withNumber)
                    r.ClearCircle(x + badge / 2, y + badge / 2, Math.Max(1, badge / 5));
                break;
            case TrayState.Degraded:
                r.FillTriangle(x + badge / 2, y, x, y + badge - 1, x + badge - 1, y + badge - 1, Warn);
                break;
            case TrayState.Error:
                // Крупный бейдж — круг с вырезанным крестом. Мелкий — сам
                // крест: вырез шириной в пиксель по кругу диаметром девять
                // рассыпается в горсть точек, и «ошибка» перестаёт читаться.
                // Проверено глазами на 16 px, а не выведено из общих слов.
                if (badge >= 12)
                {
                    r.FillCircle(x + badge / 2, y + badge / 2, badge / 2, Fail);
                    PunchCross(r, x + badge / 2, y + badge / 2, badge / 4);
                }
                else
                {
                    DrawCross(r, x + badge / 2, y + badge / 2, badge / 2, Fail);
                }
                break;
        }
        return r;
    }

    /// <summary>Знак продукта: папка с двумя точками коммитов внутри.
    /// Рисуется пропорционально размеру, чтобы 16 и 32 были одним знаком,
    /// а не двумя разными.</summary>
    private static void DrawMark(Raster r)
    {
        var s = r.Width;
        var t = Math.Max(1, s / 12);                 // толщина линии
        var left = s / 8;
        var top = s / 5;
        var right = s - s / 8;
        var bottom = s - s / 5;

        r.StrokeRect(left, top, right - left, bottom - top, t, Mark);
        // «язычок» папки сверху слева
        r.FillRect(left, top - t, (right - left) / 3, t, Mark);

        // две точки, соединённые уголком, — коммиты в истории
        var cx1 = left + (right - left) / 3;
        var cy1 = bottom - (bottom - top) / 3;
        var cx2 = right - (right - left) / 3;
        var cy2 = top + (bottom - top) / 3;
        var dot = Math.Max(1, s / 12);
        r.FillRect(cx1, cy2, cx2 - cx1, Math.Max(1, t - 1), Mark);
        r.FillRect(cx1, cy2, Math.Max(1, t - 1), cy1 - cy2, Mark);
        r.FillCircle(cx1, cy1, dot, Mark);
        r.FillCircle(cx2, cy2, dot, Mark);
    }

    private static void DrawCross(Raster r, int cx, int cy, int reach, (byte, byte, byte) c)
    {
        for (var d = -reach; d <= reach; d++)
        {
            r.Set(cx + d, cy + d, c);
            r.Set(cx + d, cy - d, c);
            r.Set(cx + d + 1, cy + d, c);
            r.Set(cx + d + 1, cy - d, c);
        }
    }

    private static void PunchCross(Raster r, int cx, int cy, int reach)
    {
        for (var d = -reach; d <= reach; d++)
        {
            r.Clear(cx + d, cy + d);
            r.Clear(cx + d, cy - d);
            r.Clear(cx + d + 1, cy + d);
            r.Clear(cx + d + 1, cy - d);
        }
    }

    // Шрифт 3×5 на цифру: в бейдж 18 px не помещается никакой настоящий, а
    // эти формы читаются и в 32 px, и на монохромной панели.
    private static readonly byte[][] Digits =
    {
        new byte[] { 0b111, 0b101, 0b101, 0b101, 0b111 }, // 0
        new byte[] { 0b010, 0b110, 0b010, 0b010, 0b111 }, // 1
        new byte[] { 0b111, 0b001, 0b111, 0b100, 0b111 }, // 2
        new byte[] { 0b111, 0b001, 0b111, 0b001, 0b111 }, // 3
        new byte[] { 0b101, 0b101, 0b111, 0b001, 0b001 }, // 4
        new byte[] { 0b111, 0b100, 0b111, 0b001, 0b111 }, // 5
        new byte[] { 0b111, 0b100, 0b111, 0b101, 0b111 }, // 6
        new byte[] { 0b111, 0b001, 0b010, 0b010, 0b010 }, // 7
        new byte[] { 0b111, 0b101, 0b111, 0b101, 0b111 }, // 8
        new byte[] { 0b111, 0b101, 0b111, 0b001, 0b111 }, // 9
    };

    private static void PunchNumber(Raster r, int value, int cx, int cy)
    {
        var text = value.ToString();
        const int gw = 3, gh = 5, gap = 1;
        var scale = Math.Max(1, r.Width / 32);
        var width = (text.Length * gw + (text.Length - 1) * gap) * scale;
        var x0 = cx - width / 2;
        var y0 = cy - gh * scale / 2;

        foreach (var ch in text)
        {
            var glyph = Digits[ch - '0'];
            for (var row = 0; row < gh; row++)
                for (var col = 0; col < gw; col++)
                    if ((glyph[row] & (1 << (gw - 1 - col))) != 0)
                        r.ClearRect(x0 + col * scale, y0 + row * scale, scale, scale);
            x0 += (gw + gap) * scale;
        }
    }

    // ---------- растр ----------

    internal sealed class Raster
    {
        public int Width { get; }
        public int Height { get; }
        public byte[] Pixels { get; }   // RGBA

        public Raster(int width, int height)
        {
            Width = width;
            Height = height;
            Pixels = new byte[width * height * 4];
        }

        public void Set(int x, int y, (byte R, byte G, byte B) c)
        {
            if ((uint)x >= (uint)Width || (uint)y >= (uint)Height) return;
            var i = (y * Width + x) * 4;
            Pixels[i] = c.R;
            Pixels[i + 1] = c.G;
            Pixels[i + 2] = c.B;
            Pixels[i + 3] = 255;
        }

        /// <summary>Стирает пиксель — делает его сквозным. Так вырезаются
        /// цифра и крест: вырез виден при любой перекраске иконки.</summary>
        public void Clear(int x, int y)
        {
            if ((uint)x >= (uint)Width || (uint)y >= (uint)Height) return;
            var i = (y * Width + x) * 4;
            Pixels[i] = Pixels[i + 1] = Pixels[i + 2] = Pixels[i + 3] = 0;
        }

        public void ClearRect(int x, int y, int w, int h)
        {
            for (var j = y; j < y + h; j++)
                for (var i = x; i < x + w; i++)
                    Clear(i, j);
        }

        public void ClearCircle(int cx, int cy, int radius)
        {
            for (var y = cy - radius; y <= cy + radius; y++)
                for (var x = cx - radius; x <= cx + radius; x++)
                {
                    var dx = x - cx;
                    var dy = y - cy;
                    if (dx * dx + dy * dy <= radius * radius) Clear(x, y);
                }
        }

        public byte Alpha(int x, int y) =>
            (uint)x >= (uint)Width || (uint)y >= (uint)Height ? (byte)0 : Pixels[(y * Width + x) * 4 + 3];

        public void FillRect(int x, int y, int w, int h, (byte, byte, byte) c)
        {
            for (var j = y; j < y + h; j++)
                for (var i = x; i < x + w; i++)
                    Set(i, j, c);
        }

        public void StrokeRect(int x, int y, int w, int h, int t, (byte, byte, byte) c)
        {
            FillRect(x, y, w, t, c);
            FillRect(x, y + h - t, w, t, c);
            FillRect(x, y, t, h, c);
            FillRect(x + w - t, y, t, h, c);
        }

        public void FillCircle(int cx, int cy, int radius, (byte, byte, byte) c)
        {
            for (var y = cy - radius; y <= cy + radius; y++)
                for (var x = cx - radius; x <= cx + radius; x++)
                {
                    var dx = x - cx;
                    var dy = y - cy;
                    if (dx * dx + dy * dy <= radius * radius) Set(x, y, c);
                }
        }

        public void FillTriangle(int x1, int y1, int x2, int y2, int x3, int y3,
            (byte, byte, byte) c)
        {
            var minY = Math.Min(y1, Math.Min(y2, y3));
            var maxY = Math.Max(y1, Math.Max(y2, y3));
            var minX = Math.Min(x1, Math.Min(x2, x3));
            var maxX = Math.Max(x1, Math.Max(x2, x3));
            for (var y = minY; y <= maxY; y++)
                for (var x = minX; x <= maxX; x++)
                    if (Inside(x, y)) Set(x, y, c);

            bool Inside(int px, int py)
            {
                var d1 = Sign(px, py, x1, y1, x2, y2);
                var d2 = Sign(px, py, x2, y2, x3, y3);
                var d3 = Sign(px, py, x3, y3, x1, y1);
                var neg = d1 < 0 || d2 < 0 || d3 < 0;
                var pos = d1 > 0 || d2 > 0 || d3 > 0;
                return !(neg && pos);
            }

            static int Sign(int px, int py, int ax, int ay, int bx, int by) =>
                (px - bx) * (ay - by) - (ax - bx) * (py - by);
        }
    }

    // ---------- PNG ----------

    internal static byte[] EncodePng(Raster r)
    {
        // Строки с фильтром 0: сжатие здесь вторично, а предсказуемость байтов
        // важнее — иконка весит сотни байт при любом фильтре.
        var raw = new byte[(r.Width * 4 + 1) * r.Height];
        for (var y = 0; y < r.Height; y++)
        {
            raw[y * (r.Width * 4 + 1)] = 0;
            Array.Copy(r.Pixels, y * r.Width * 4, raw, y * (r.Width * 4 + 1) + 1, r.Width * 4);
        }

        using var deflated = new MemoryStream();
        using (var z = new ZLibStream(deflated, CompressionLevel.Optimal, leaveOpen: true))
            z.Write(raw, 0, raw.Length);

        using var png = new MemoryStream();
        png.Write(new byte[] { 0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A });

        var ihdr = new byte[13];
        WriteBigEndian(ihdr, 0, r.Width);
        WriteBigEndian(ihdr, 4, r.Height);
        ihdr[8] = 8;    // бит на канал
        ihdr[9] = 6;    // truecolour + alpha
        Chunk(png, "IHDR", ihdr);
        Chunk(png, "IDAT", deflated.ToArray());
        Chunk(png, "IEND", Array.Empty<byte>());
        return png.ToArray();
    }

    private static void Chunk(Stream to, string type, byte[] data)
    {
        var header = new byte[4];
        WriteBigEndian(header, 0, data.Length);
        to.Write(header);

        var typed = new byte[4 + data.Length];
        for (var i = 0; i < 4; i++) typed[i] = (byte)type[i];
        data.CopyTo(typed, 4);
        to.Write(typed);

        var crc = new byte[4];
        WriteBigEndian(crc, 0, unchecked((int)Crc32(typed)));
        to.Write(crc);
    }

    private static void WriteBigEndian(byte[] to, int offset, int value)
    {
        to[offset] = (byte)(value >> 24);
        to[offset + 1] = (byte)(value >> 16);
        to[offset + 2] = (byte)(value >> 8);
        to[offset + 3] = (byte)value;
    }

    private static readonly uint[] CrcTable = BuildCrcTable();

    private static uint[] BuildCrcTable()
    {
        var table = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            var c = n;
            for (var k = 0; k < 8; k++)
                c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            table[n] = c;
        }
        return table;
    }

    private static uint Crc32(byte[] data)
    {
        var c = 0xFFFFFFFFu;
        foreach (var b in data) c = CrcTable[(c ^ b) & 0xFF] ^ (c >> 8);
        return c ^ 0xFFFFFFFFu;
    }

    // ---------- ICO ----------

    public static byte[] BuildIco(IReadOnlyList<(int Size, byte[] Png)> images)
    {
        using var ico = new MemoryStream();
        var writer = new BinaryWriter(ico);
        writer.Write((ushort)0);                    // reserved
        writer.Write((ushort)1);                    // тип: иконка
        writer.Write((ushort)images.Count);

        var offset = 6 + 16 * images.Count;
        foreach (var (size, png) in images)
        {
            writer.Write((byte)(size >= 256 ? 0 : size));
            writer.Write((byte)(size >= 256 ? 0 : size));
            writer.Write((byte)0);                  // палитра не используется
            writer.Write((byte)0);                  // reserved
            writer.Write((ushort)1);                // плоскостей
            writer.Write((ushort)32);               // бит на пиксель
            writer.Write(png.Length);
            writer.Write(offset);
            offset += png.Length;
        }
        foreach (var (_, png) in images) writer.Write(png);
        writer.Flush();
        return ico.ToArray();
    }
}
