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
            _main = new MainWindow();
            desktop.MainWindow = _main;
            // окно закрывается в трей, программа продолжает держать тома
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
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
