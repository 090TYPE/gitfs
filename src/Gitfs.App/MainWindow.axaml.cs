using System.Collections.ObjectModel;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Gitfs.Diagnostics;

namespace Gitfs.App;

public partial class MainWindow : Window
{
    private enum SidePanelMode { Environment, Mount }

    /// <summary>Коллекция живёт одна: пересоздание ItemsSource сбрасывало
    /// выделение, и после монтирования детали нового тома не показывались.</summary>
    private readonly ObservableCollection<MountEntry> _mounts = new();
    private readonly DispatcherTimer _uptimeTimer;

    private SidePanelMode _panelMode = SidePanelMode.Environment;
    private CancellationTokenSource? _diagnosticsCts;

    /// <summary>Трей показывает состояние монтирований без открытия окна.</summary>
    public event Action? MountsChanged;

    /// <summary>Есть ли куда прятаться при закрытии окна. На Linux без
    /// DBus-хоста иконки трея нет вовсе, и «спрятать окно» означало бы
    /// приложение, которое невозможно закрыть (находка ревью).</summary>
    public bool HasTray { get; set; }

    public void OpenMountDialog() => _ = MountAsync();

    public MainWindow()
    {
        InitializeComponent();
        MountsList.ItemsSource = _mounts;

        Closing += (_, e) =>
        {
            if (!HasTray) return;   // без трея закрытие означает выход
            e.Cancel = true;
            Hide();
        };

        PlatformBadge.Text = OperatingSystem.IsWindows() ? "windows"
            : OperatingSystem.IsMacOS() ? "macos" : "linux";
        if (!MountService.Instance.CanMount)
        {
            MountButton.IsEnabled = false;
            StatusText.Text = MountService.Instance.MountBlockedReason;
        }

        // время работы тикает: в макете это живая колонка
        _uptimeTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        _uptimeTimer.Tick += (_, _) => RefreshUptime();
        _uptimeTimer.Start();

        RefreshMounts();
        ShowEnvironment();
    }

    private void RefreshMounts()
    {
        EmptyState.IsVisible = _mounts.Count == 0;
        MountsList.IsVisible = _mounts.Count > 0;
        UnmountButton.IsEnabled = MountsList.SelectedItem is MountEntry;
        MountsChanged?.Invoke();
    }

    private void RefreshUptime()
    {
        if (_mounts.Count == 0) return;
        var selected = MountsList.SelectedItem;
        var snapshot = _mounts.ToList();
        _mounts.Clear();
        foreach (var entry in snapshot) _mounts.Add(entry);
        MountsList.SelectedItem = selected;
        if (_panelMode == SidePanelMode.Mount && selected is MountEntry live)
            ShowMountDetails(live);
    }

    // ---------- боковая панель ----------

    private async void ShowEnvironment()
    {
        try
        {
            _panelMode = SidePanelMode.Environment;
            _diagnosticsCts?.Cancel();
            var cts = new CancellationTokenSource();
            _diagnosticsCts = cts;

            DoctorButton.IsEnabled = false;
            SidePanelTitle.Text = "ENVIRONMENT";
            SidePanel.Children.Clear();
            SidePanel.Children.Add(Muted("checking…"));

            var checks = await Task.Run(() => MountService.Diagnose(null), cts.Token);
            if (cts.IsCancellationRequested || _panelMode != SidePanelMode.Environment) return;

            SidePanel.Children.Clear();
            foreach (var check in checks) SidePanel.Children.Add(RenderCheck(check));
        }
        catch (OperationCanceledException)
        {
            // панель переключили — это нормальный ход, не ошибка
        }
        catch (Exception ex)
        {
            Program.Log("diagnostics", ex);
            SidePanel.Children.Clear();
            SidePanel.Children.Add(new TextBlock
            {
                Text = "diagnostics failed: " + ex.Message,
                Foreground = new SolidColorBrush(Color.Parse("#E07B6D")),
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
            });
        }
        finally
        {
            DoctorButton.IsEnabled = true;
        }
    }

    private static TextBlock Muted(string text) => new()
    {
        Text = text,
        Foreground = new SolidColorBrush(Color.Parse("#9397ab")),
        FontSize = 12,
    };

    private static Control RenderCheck(Check check)
    {
        var colour = check.Status switch
        {
            CheckStatus.Ok => Color.Parse("#6FBF77"),
            CheckStatus.Warn => Color.Parse("#E3B341"),
            _ => Color.Parse("#E07B6D"),
        };
        var word = check.Status switch
        {
            CheckStatus.Ok => "ok",
            CheckStatus.Warn => "warn",
            _ => "fail",
        };

        var panel = new StackPanel { Spacing = 2 };
        var head = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        head.Children.Add(new TextBlock
        {
            Text = word,
            Foreground = new SolidColorBrush(colour),
            FontFamily = new FontFamily("Cascadia Mono, Consolas, monospace"),
            FontSize = 12,
            Width = 34,
        });
        head.Children.Add(new TextBlock
        {
            Text = check.Name,
            Foreground = new SolidColorBrush(Color.Parse("#cfd3e5")),
            FontSize = 12,
            Width = 96,
        });
        head.Children.Add(new TextBlock
        {
            Text = check.Value,
            Foreground = new SolidColorBrush(Color.Parse("#9397ab")),
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 150,
        });
        panel.Children.Add(head);

        // каждый провал несёт одну строку «что сделать» — правило дизайна
        if (check.Fix is not null)
        {
            panel.Children.Add(new TextBlock
            {
                Text = "→ " + check.Fix,
                Foreground = new SolidColorBrush(Color.Parse("#75798c")),
                FontSize = 11,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Avalonia.Thickness(42, 0, 0, 6),
            });
        }
        return panel;
    }

    private void ShowMountDetails(MountEntry entry)
    {
        _panelMode = SidePanelMode.Mount;
        _diagnosticsCts?.Cancel();
        SidePanelTitle.Text = "MOUNT";
        SidePanel.Children.Clear();

        void Row(string key, string value)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            row.Children.Add(new TextBlock
            {
                Text = key,
                Foreground = new SolidColorBrush(Color.Parse("#75798c")),
                FontSize = 11, Width = 80,
            });
            row.Children.Add(new TextBlock
            {
                Text = value,
                Foreground = new SolidColorBrush(Color.Parse("#cfd3e5")),
                FontFamily = new FontFamily("Cascadia Mono, Consolas, monospace"),
                FontSize = 12, TextWrapping = TextWrapping.Wrap, MaxWidth = 200,
            });
            SidePanel.Children.Add(row);
        }

        Row("repository", entry.Repository);
        Row("path", entry.Path);
        Row("mount", entry.MountPoint);
        Row("views", entry.Views);
        Row("uptime", entry.Uptime);

        var open = new Button
        {
            Content = "Open in file manager",
            Classes = { "secondary" },
            Margin = new Avalonia.Thickness(0, 12, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        open.Click += (_, _) => OpenInFileManager(entry.MountPoint);
        SidePanel.Children.Add(open);
    }

    private static void OpenInFileManager(string path)
    {
        var target = path.EndsWith(':') ? path + "\\" : path;
        var (exe, args) = OperatingSystem.IsWindows() ? ("explorer.exe", target)
            : OperatingSystem.IsMacOS() ? ("open", target)
            : ("xdg-open", target);
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(exe, args)
            {
                UseShellExecute = true,
            });
        }
        catch (Exception e)
        {
            Program.Log("open-file-manager", e);
        }
    }

    // ---------- действия ----------

    private void OnMountSelected(object? sender, SelectionChangedEventArgs e)
    {
        if (MountsList.SelectedItem is MountEntry entry)
        {
            UnmountButton.IsEnabled = true;
            ShowMountDetails(entry);
        }
        else
        {
            UnmountButton.IsEnabled = false;
            if (_panelMode == SidePanelMode.Mount) ShowEnvironment();
        }
    }

    private void OnMountClicked(object? sender, RoutedEventArgs e) => _ = MountAsync();

    /// <summary>Весь путь под защитой: конструктор диалога тоже умеет
    /// бросать (перечисление дисков падает на отключённом сетевом),
    /// а необработанное исключение отсюда роняло процесс (находка ревью).</summary>
    private async Task MountAsync()
    {
        try
        {
            var dialog = new MountDialog();
            var result = await dialog.ShowDialog<MountRequest?>(this);
            if (result is null) return;

            MountButton.IsEnabled = false;
            StatusText.Text = $"mounting {result.MountPoint} …";
            var entry = await Task.Run(() => MountService.Instance.Mount(
                result.RepositoryPath, result.MountPoint, result.Views, result.Options));

            // Запоминаем ТОЛЬКО удавшееся монтирование: список недавних —
            // это «сюда можно вернуться», а не история попыток.
            RecentRepositories.Instance.Remember(result.RepositoryPath);

            _mounts.Add(entry);
            RefreshMounts();
            MountsList.SelectedItem = entry;   // сразу показываем детали нового тома
            StatusText.Text = $"mounted {entry.MountPoint} · {entry.Repository} · " +
                              $"{result.Views.Count} view{(result.Views.Count == 1 ? "" : "s")}";
            OpenInFileManager(entry.MountPoint);
        }
        catch (Exception ex)
        {
            Program.Log("mount", ex);
            StatusText.Text = "fail " + (ex.InnerException?.Message ?? ex.Message);
        }
        finally
        {
            MountButton.IsEnabled = MountService.Instance.CanMount;
        }
    }

    private void OnUnmount(object? sender, RoutedEventArgs e) => _ = UnmountAsync();

    private async Task UnmountAsync()
    {
        if (MountsList.SelectedItem is not MountEntry entry) return;
        try
        {
            UnmountButton.IsEnabled = false;
            StatusText.Text = $"unmounting {entry.MountPoint} …";
            await Task.Run(() => MountService.Instance.Unmount(entry));
            _mounts.Remove(entry);
            RefreshMounts();
            ShowEnvironment();
            StatusText.Text = $"unmounted {entry.MountPoint}";
        }
        catch (Exception ex)
        {
            Program.Log("unmount", ex);
            StatusText.Text = "fail " + ex.Message;
        }
        finally
        {
            UnmountButton.IsEnabled = MountsList.SelectedItem is MountEntry;
        }
    }

    private void OnDoctor(object? sender, RoutedEventArgs e)
    {
        MountsList.SelectedItem = null;
        _panelMode = SidePanelMode.Environment;
        ShowEnvironment();
        StatusText.Text = "environment checked";
    }
}
