using Avalonia;
using Avalonia.Threading;

namespace Gitfs.App;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        // Последний рубеж: необработанное исключение в async void обработчике
        // иначе убивает процесс, оставляя тома смонтированными. Снимаем их
        // прежде чем упасть, и не падаем от того, что можно пережить.
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            TryUnmountAll();
            Log("fatal", e.ExceptionObject as Exception);
        };

        try
        {
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        finally
        {
            TryUnmountAll();
        }
    }

    private static void TryUnmountAll()
    {
        try { MountService.Instance.UnmountAll(); }
        catch (Exception) { /* уже падаем — не мешаем */ }
    }

    internal static string LogPath { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "gitfs", "app.log");

    internal static void Log(string context, Exception? e)
    {
        if (e is null) return;
        try
        {
            var dir = Path.GetDirectoryName(LogPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            // Одна строка на запись: хвост журнала читается построчно, и
            // многострочный стек превращал бы шесть последних событий в шесть
            // обрывков одного.
            var text = e.ToString().ReplaceLineEndings(" ⏎ ");
            File.AppendAllText(LogPath,
                $"{DateTimeOffset.Now:O} {context}: {text}{Environment.NewLine}");
        }
        catch (Exception) { /* лог не должен ронять приложение */ }
    }

    /// <summary>Последние строки журнала для панели тома (макет 03). Читает
    /// с общим доступом: журнал открыт на дозапись этим же процессом, и
    /// эксклюзивное чтение отдавало бы «файл занят» вместо содержимого.</summary>
    internal static IReadOnlyList<string> TailLog(int count)
    {
        try
        {
            if (!File.Exists(LogPath)) return [];
            using var stream = new FileStream(LogPath, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            var tail = new Queue<string>(count);
            while (reader.ReadLine() is { } line)
            {
                if (line.Length == 0) continue;
                if (tail.Count == count) tail.Dequeue();
                tail.Enqueue(line);
            }
            return tail.ToList();
        }
        catch (Exception)
        {
            return [];   // журнал — удобство, а не условие работы окна
        }
    }

    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>()
        .UsePlatformDetect()
        .WithInterFont()
        .LogToTrace()
        .AfterSetup(_ => Dispatcher.UIThread.UnhandledException += (_, e) =>
        {
            // диспетчер продолжает работу: окно остаётся живым, ошибка в логе
            Log("ui", e.Exception);
            e.Handled = true;
        });
}
