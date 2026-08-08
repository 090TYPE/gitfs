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

    internal static void Log(string context, Exception? e)
    {
        if (e is null) return;
        try
        {
            var dir = Path.Combine(Environment.GetFolderPath(
                Environment.SpecialFolder.LocalApplicationData), "gitfs");
            Directory.CreateDirectory(dir);
            File.AppendAllText(Path.Combine(dir, "app.log"),
                $"{DateTimeOffset.Now:O} {context}: {e}{Environment.NewLine}");
        }
        catch (Exception) { /* лог не должен ронять приложение */ }
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
