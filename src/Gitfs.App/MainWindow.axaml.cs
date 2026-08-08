using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;

using Avalonia.Media;
using Gitfs.Cli;

namespace Gitfs.App;

public partial class MainWindow : Window
{
    private readonly List<MountEntry> _mounts = new();

    public MainWindow()
    {
        InitializeComponent();
        PlatformBadge.Text = OperatingSystem.IsWindows() ? "windows"
            : OperatingSystem.IsMacOS() ? "macos" : "linux";
        if (!MountService.Instance.CanMount)
        {
            MountButton.IsEnabled = false;
            StatusText.Text = MountService.Instance.MountBlockedReason;
        }
        RefreshMounts();
        ShowEnvironment();
    }


    private void RefreshMounts()
    {
        MountsList.ItemsSource = null;
        MountsList.ItemsSource = _mounts.ToList();
        EmptyState.IsVisible = _mounts.Count == 0;
        MountsList.IsVisible = _mounts.Count > 0;
        UnmountButton.IsEnabled = MountsList.SelectedItem is MountEntry;
    }

    // ---------- боковая панель ----------

    private bool _diagnosing;

    /// <summary>Диагностика запускает внешние процессы и обходит каталоги,
    /// поэтому выполняется в фоне: в UI-потоке она замораживала окно.</summary>
    private async void ShowEnvironment()
    {
        if (_diagnosing) return;
        _diagnosing = true;
        DoctorButton.IsEnabled = false;
        SidePanelTitle.Text = "ENVIRONMENT";
        SidePanel.Children.Clear();
        SidePanel.Children.Add(new TextBlock
        {
            Text = "checking…",
            Foreground = new SolidColorBrush(Color.Parse("#9397ab")),
            FontSize = 12,
        });

        try
        {
            var checks = await Task.Run(() => MountService.Diagnose(null));
            if (SidePanelTitle.Text != "ENVIRONMENT") return; // пользователь ушёл в детали
            SidePanel.Children.Clear();
            foreach (var check in checks) SidePanel.Children.Add(RenderCheck(check));
        }
        catch (Exception ex)
        {
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
            _diagnosing = false;
            DoctorButton.IsEnabled = true;
        }
    }

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
        catch (Exception)
        {
            // файловый менеджер может отсутствовать — не повод падать
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
            ShowEnvironment();
        }
    }

    private async void OnMountClicked(object? sender, RoutedEventArgs e)
    {
        var dialog = new MountDialog();
        var result = await dialog.ShowDialog<MountRequest?>(this);
        if (result is null) return;

        MountButton.IsEnabled = false;
        StatusText.Text = $"mounting {result.MountPoint} …";
        try
        {
            // монтирование открывает пакеты и поднимает драйвер — не в UI-потоке
            var entry = await Task.Run(() => MountService.Instance.Mount(
                result.RepositoryPath, result.MountPoint, result.Views));
            _mounts.Add(entry);
            RefreshMounts();
            StatusText.Text = $"mounted {entry.MountPoint} · {entry.Repository} · " +
                              $"{result.Views.Count} view{(result.Views.Count == 1 ? "" : "s")}";
            OpenInFileManager(entry.MountPoint);
        }
        catch (Exception ex)
        {
            StatusText.Text = "fail " + (ex.InnerException?.Message ?? ex.Message);
        }
        finally
        {
            MountButton.IsEnabled = MountService.Instance.CanMount;
        }
    }

    private async void OnUnmount(object? sender, RoutedEventArgs e)
    {
        if (MountsList.SelectedItem is not MountEntry entry) return;
        UnmountButton.IsEnabled = false;
        StatusText.Text = $"unmounting {entry.MountPoint} …";
        await Task.Run(() => MountService.Instance.Unmount(entry));
        _mounts.Remove(entry);
        RefreshMounts();
        ShowEnvironment();
        StatusText.Text = $"unmounted {entry.MountPoint}";
    }

    private void OnDoctor(object? sender, RoutedEventArgs e)
    {
        MountsList.SelectedItem = null;
        ShowEnvironment();
        StatusText.Text = "environment checked";
    }
}
