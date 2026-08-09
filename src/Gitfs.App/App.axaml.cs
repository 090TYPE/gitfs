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
        // Тема — до создания окон: иначе первый кадр рисуется одной темой,
        // а следующий другой, и это видно.
        Settings.Apply(Settings.Theme);

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // Единственный способ посмотреть на диалог автоматически: под
            // Xvfb окно открывается программой, а не мышью, и никаким
            // xdotool до кнопки внутри Avalonia не дотянуться. Переменная
            // окружения, а не ключ командной строки, — чтобы это не
            // выглядело возможностью продукта, которой оно не является.
            switch (Environment.GetEnvironmentVariable("GITFS_UI_PREVIEW"))
            {
                case "mount-dialog":
                    desktop.MainWindow = new MountDialog();
                    desktop.ShutdownMode = ShutdownMode.OnMainWindowClose;
                    base.OnFrameworkInitializationCompleted();
                    return;
                case "icons":
                    // Отрисовка ассетов: приложение поднимается ради одного
                    // растеризатора и сразу выходит, окна не показывая.
                    {
                        var target = Environment.GetEnvironmentVariable("GITFS_ICON_OUT")
                                     ?? "src/Gitfs.Vfs/Assets";
                        var written = IconRenderer.RenderAll(target);
                        Console.WriteLine($"{written} icon(s) written to {target}");
                        // Именно Exit, а не Shutdown: цикл диспетчера ещё не
                        // запущен, и Shutdown из этой точки бросает «Dispatcher
                        // shut down» уже ПОСЛЕ того, как файлы записаны, —
                        // инструмент отрабатывал и всё равно падал.
                        Environment.Exit(written > 0 ? 0 : 1);
                        return;
                    }
                case "first-run":
                    desktop.MainWindow = new FirstRunWindow();
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
            // Только КОГДА главное окно уже на экране: владелец обязан быть
            // видимым, иначе Show(owner) бросает «Cannot show window with
            // non-visible owner», приветствие не появляется вовсе, и об этом
            // узнаёшь лишь из журнала. Здесь мы ещё до base — окна нет.
            _main.Opened += (_, _) => ShowFirstRunIfNeeded();
        }
        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>Экран первого запуска (макет 05). Показывается ПОСЛЕ главного
    /// окна и поверх него: закрыв его, человек оказывается в приложении, а не
    /// в пустоте. Ошибка здесь не имеет права мешать запуску — онбординг
    /// помогает начать, а не является условием работы.</summary>
    private void ShowFirstRunIfNeeded()
    {
        try
        {
            var checks = MountService.Diagnose(null);
            if (!FirstRunWindow.ShouldShow(checks)) return;

            var window = new FirstRunWindow();
            window.Closed += (_, _) =>
            {
                RefreshTray();     // драйвер мог появиться, пока экран был открыт
                if (window.WantsToMount) _main?.OpenMountDialog();
            };
            window.Show(_main!);
        }
        catch (Exception e) { Program.Log("first-run", e); }
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

        var degraded = mounts.Any(m => m.Health != MountHealth.Ok);
        var state = TrayBadge.StateOf(mounts.Count, checks, degraded);
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
            // Деградация тома — раньше в подсказке: она конкретнее, чем
            // предупреждение окружения, и именно из-за неё горит треугольник.
            TrayState.Degraded when degraded =>
                "gitfs — " + string.Join(", ", mounts.Where(m => m.Health != MountHealth.Ok)
                    .Select(m => $"{m.MountPoint}: packs reopened {m.Reopens}×")),
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

        menu.Add(new NativeMenuItemSeparator());
        if (mounts.Count == 0)
        {
            // Пустое состояние ЕСТЬ (макет 02): раздел, который просто
            // исчезает, читается как «меню сломалось», а не «томов нет».
            menu.Add(new NativeMenuItem("Nothing mounted") { IsEnabled = false });
        }
        else
        {
            foreach (var mount in mounts)
            {
                var submenu = new NativeMenu();
                // Заголовок подменю — откуда этот том: в меню видна буква, а
                // репозиториев у человека десятки (макет 02).
                submenu.Add(new NativeMenuItem(mount.Path) { IsEnabled = false });
                submenu.Add(new NativeMenuItemSeparator());
                submenu.Add(new NativeMenuItem("Open in file manager")
                {
                    Command = new Command(() => Reveal(mount.MountPoint)),
                });
                // «Показать лог» из брифа §4.2 — это .gitfs/log.txt самого
                // тома: журнал лежит внутри продукта, и показывать надо его,
                // а не отдельный файл где-то в профиле.
                submenu.Add(new NativeMenuItem("Show log")
                {
                    Command = new Command(() => Reveal(
                        System.IO.Path.Combine(mount.MountPoint, ".gitfs", "log.txt"))),
                });
                submenu.Add(new NativeMenuItem("Copy path")
                {
                    Command = new Command(() => CopyToClipboard(mount.MountPoint)),
                });
                submenu.Add(new NativeMenuItemSeparator());
                submenu.Add(new NativeMenuItem($"Unmount {mount.MountPoint}")
                {
                    Command = new Command(() => Unmount(mount)),
                });
                var dot = mount.Health == MountHealth.Ok ? "" : "  ▲ " + mount.HealthWord;
                menu.Add(new NativeMenuItem($"{mount.MountPoint}  {mount.Repository}{dot}")
                {
                    Menu = submenu,
                    // Клик по самому монтированию открывает Проводник —
                    // подменю для действий, а не для того, чтобы попасть в том.
                    Command = new Command(() => Reveal(mount.MountPoint)),
                });
            }
        }

        menu.Add(new NativeMenuItemSeparator());
        menu.Add(new NativeMenuItem("Doctor")
        {
            Command = new Command(() => { ShowMain(); _main?.OpenDoctor(); }),
        });
        menu.Add(new NativeMenuItem($"Theme: {Settings.Describe(Settings.Theme)}")
        {
            Command = new Command(() =>
            {
                Settings.Theme = Settings.Next(Settings.Theme);
                RefreshTray();   // подпись пункта обязана догнать выбор
            }),
        });
        menu.Add(new NativeMenuItemSeparator());
        menu.Add(new NativeMenuItem("Quit") { Command = new Command(Quit) });
        icon.Menu = menu;
    }

    private static void CopyToClipboard(string text)
    {
        try
        {
            if (Avalonia.Application.Current?.ApplicationLifetime
                is IClassicDesktopStyleApplicationLifetime { MainWindow: { } window })
            {
                window.Clipboard?.SetTextAsync(text);
            }
        }
        catch (Exception e) { Program.Log("clipboard", e); }
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
