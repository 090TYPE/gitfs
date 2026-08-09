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
            _main.MountsChanged += RefreshTray;
            RefreshTray();
        }
        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>Иконка, подсказка и меню трея пересобираются вместе: они
    /// описывают одно состояние, и разъехавшись однажды, разъезжаются
    /// навсегда — бейдж «3» рядом с меню, где два тома.</summary>
    private void RefreshTray()
    {
        var icons = TrayIcon.GetIcons(this);
        if (icons is null || icons.Count == 0) return;
        var mounts = MountService.Instance.Entries;

        // Doctor даёт «деградацию» и «отказ». Считается редко — только когда
        // меняется набор томов: это чтение реестра и файловой системы.
        IReadOnlyList<Gitfs.Diagnostics.Check> checks;
        try { checks = MountService.Diagnose(null); }
        catch (Exception e) { Program.Log("tray-doctor", e); checks = []; }

        var state = TrayBadge.StateOf(mounts.Count, checks);
        try
        {
            icons[0].Icon = new WindowIcon(
                new MemoryStream(TrayBadge.BuildIcon(state, mounts.Count)));
        }
        catch (Exception e)
        {
            // Иконка — не повод падать: остаётся прежняя, состояние всё
            // равно видно в подсказке и в меню.
            Program.Log("tray-icon", e);
        }

        icons[0].ToolTipText = state switch
        {
            TrayState.Error => "gitfs — " + FirstProblem(checks, Gitfs.Diagnostics.CheckStatus.Fail),
            TrayState.Degraded => "gitfs — " + FirstProblem(checks, Gitfs.Diagnostics.CheckStatus.Warn),
            TrayState.Idle => "gitfs — nothing mounted",
            _ => $"gitfs — {mounts.Count} mounted: " +
                 string.Join(", ", mounts.Select(m => m.MountPoint)),
        };

        BuildTrayMenu(icons[0], mounts);
    }

    private static string FirstProblem(IReadOnlyList<Gitfs.Diagnostics.Check> checks,
        Gitfs.Diagnostics.CheckStatus status)
    {
        var check = checks.FirstOrDefault(c => c.Status == status);
        return check is null ? "something is wrong" : $"{check.Name}: {check.Value}";
    }

    /// <summary>Меню трея с подменю на каждый том (макет 02). Том можно снять
    /// не открывая окна — иначе «живёт в трее» означает лишь «прячется».</summary>
    private void BuildTrayMenu(TrayIcon icon, IReadOnlyList<MountEntry> mounts)
    {
        var menu = new NativeMenu();
        menu.Add(new NativeMenuItem("Open gitfs") { Command = new Command(ShowMain) });
        menu.Add(new NativeMenuItem("Mount…") { Command = new Command(() =>
        {
            ShowMain();
            _main?.OpenMountDialog();
        }) });

        if (mounts.Count > 0)
        {
            menu.Add(new NativeMenuItemSeparator());
            foreach (var mount in mounts)
            {
                var submenu = new NativeMenu();
                submenu.Add(new NativeMenuItem($"Unmount {mount.MountPoint}")
                {
                    Command = new Command(() => Unmount(mount)),
                });
                submenu.Add(new NativeMenuItem("Open in file manager")
                {
                    Command = new Command(() => Reveal(mount.MountPoint)),
                });
                menu.Add(new NativeMenuItem($"{mount.MountPoint}  {mount.Repository}")
                {
                    Menu = submenu,
                });
            }
        }

        menu.Add(new NativeMenuItemSeparator());
        menu.Add(new NativeMenuItem("Quit") { Command = new Command(Quit) });
        icon.Menu = menu;
    }

    private void Unmount(MountEntry mount)
    {
        try { MountService.Instance.Unmount(mount); }
        catch (Exception e) { Program.Log("tray-unmount", e); }
        _main?.ReloadMounts();
        RefreshTray();
    }

    private static void Reveal(string mountPoint)
    {
        try
        {
            using var process = new System.Diagnostics.Process();
            process.StartInfo = new System.Diagnostics.ProcessStartInfo(mountPoint)
            {
                UseShellExecute = true,
            };
            process.Start();
        }
        catch (Exception e) { Program.Log("tray-reveal", e); }
    }

    private void Quit()
    {
        MountService.Instance.UnmountAll();
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            desktop.Shutdown();
    }

    /// <summary>NativeMenuItem вызывает действие только через ICommand:
    /// событие Click у пункта, созданного в коде, не к чему привязать.</summary>
    private sealed class Command(Action run) : System.Windows.Input.ICommand
    {
        public event EventHandler? CanExecuteChanged { add { } remove { } }
        public bool CanExecute(object? parameter) => true;
        public void Execute(object? parameter) => run();
    }

    private void ShowMain()
    {
        if (_main is null) return;
        _main.Show();
        _main.WindowState = WindowState.Normal;
        _main.Activate();
    }

    private void OnTrayClicked(object? sender, EventArgs e) => ShowMain();
}
