// Собирает мультиразмерный gitfs.ico из PNG дизайн-отдела.
//
// Бриф §2.1 и §6: иконка тома — 16/24/32/48/256, «должна отличимо читаться
// рядом со стандартными дисками». В репозитории лежал .ico с ОДНИМ
// изображением 32×32: система масштабировала его и в 16, и в 256, а
// требование «читается в 16 px» проверить было не на чем.
//
//   dotnet script tools/build-icon.csx
//
// Скрипт, а не шаг сборки: исходники PNG меняются раз в год, а результат
// лежит в репозитории и участвует в ревью как обычный файл.

using System.IO;

var assets = Path.Combine("src", "Gitfs.App", "Assets");
var sources = new[] { 16, 32, 48, 256 }
    .Select(size => (Size: size, Path: Path.Combine(assets, $"gitfs-{size}.png")))
    .Where(s => File.Exists(s.Path))
    .ToList();

if (sources.Count == 0) { Console.Error.WriteLine("нет исходных PNG"); return 1; }

using var ico = new MemoryStream();
var writer = new BinaryWriter(ico);
writer.Write((ushort)0);                 // reserved
writer.Write((ushort)1);                 // тип: иконка
writer.Write((ushort)sources.Count);

var payloads = sources.Select(s => File.ReadAllBytes(s.Path)).ToList();
var offset = 6 + 16 * sources.Count;
for (var i = 0; i < sources.Count; i++)
{
    var size = sources[i].Size;
    writer.Write((byte)(size >= 256 ? 0 : size));   // 0 означает 256
    writer.Write((byte)(size >= 256 ? 0 : size));
    writer.Write((byte)0);               // палитры нет
    writer.Write((byte)0);               // reserved
    writer.Write((ushort)1);             // плоскостей
    writer.Write((ushort)32);            // бит на пиксель
    writer.Write(payloads[i].Length);
    writer.Write(offset);
    offset += payloads[i].Length;
}
foreach (var payload in payloads) writer.Write(payload);
writer.Flush();

var target = Path.Combine(assets, "gitfs.ico");
File.WriteAllBytes(target, ico.ToArray());
Console.WriteLine($"{target}: {sources.Count} размер(ов) — " +
                  string.Join(", ", sources.Select(s => s.Size)));
return 0;
