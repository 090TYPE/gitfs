using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;

namespace Gitfs.App;

public partial class App : Application
{
    private MainWindow? _main;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // Единственный способ посмотреть на диалог автоматически: под
            // Xvfb окно открывается программой, а не мышью, и никаким
            // xdotool до кнопки внутри Avalonia не дотянуться. Переменная
            // окружения, а не ключ командной строки, — чтобы это не
            // выглядело возможностью продукта, которой оно не является.
            if (Environment.GetEnvironmentVariable("GITFS_UI_PREVIEW") == "mount-dialog")
            {
                desktop.MainWindow = new MountDialog();
                desktop.ShutdownMode = ShutdownMode.OnMainWindowClose;
                base.OnFrameworkInitializationCompleted();
                return;
            }

            _main = new MainWindow();
            desktop.MainWindow = _main;

            // Прятаться в трей можно только если трей существует. На Linux
            // без DBus-хоста иконки нет, и «спрятать окно» означало бы
            // приложение, которое нечем закрыть (находка ревью).
            var tray = TrayIcon.GetIcons(this);
            var hasTray = tray is { Count: > 0 } && OperatingSystem.IsWindows();
            _main.HasTray = hasTray;
            desktop.ShutdownMode = hasTray
                ? ShutdownMode.OnExplicitShutdown
                : ShutdownMode.OnMainWindowClose;

            desktop.ShutdownRequested += (_, _) => MountService.Instance.UnmountAll();
            _main.MountsChanged += UpdateTrayTooltip;
            UpdateTrayTooltip();
        }
        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>Состояние монтирований видно из трея без открытия окна
    /// (в макете это бейдж на иконке; подсказка — его текстовый эквивалент).</summary>
    private void UpdateTrayTooltip()
    {
        var icons = TrayIcon.GetIcons(this);
        if (icons is null || icons.Count == 0) return;
        var count = MountService.Instance.Entries.Count;
        icons[0].ToolTipText = count == 0
            ? "gitfs — nothing mounted"
            : $"gitfs — {count} mounted: " +
              string.Join(", ", MountService.Instance.Entries.Select(m => m.MountPoint));
    }

    private void ShowMain()
    {
        if (_main is null) return;
        _main.Show();
        _main.WindowState = WindowState.Normal;
        _main.Activate();
    }

    private void OnTrayClicked(object? sender, EventArgs e) => ShowMain();
    private void OnTrayOpen(object? sender, EventArgs e) => ShowMain();

    private void OnTrayMount(object? sender, EventArgs e)
    {
        ShowMain();
        _main?.OpenMountDialog();
    }

    private void OnTrayQuit(object? sender, EventArgs e)
    {
        MountService.Instance.UnmountAll();
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            desktop.Shutdown();
    }
}
