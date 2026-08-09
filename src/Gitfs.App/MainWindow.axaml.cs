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
    private readonly DispatcherTimer _panelTimer;

    private SidePanelMode _panelMode = SidePanelMode.Environment;
    private CancellationTokenSource? _diagnosticsCts;

    /// <summary>Трей показывает состояние монтирований без открытия окна.</summary>
    public event Action? MountsChanged;

    /// <summary>Есть ли куда прятаться при закрытии окна. На Linux без
    /// DBus-хоста иконки трея нет вовсе, и «спрятать окно» означало бы
    /// приложение, которое невозможно закрыть (находка ревью).</summary>
    public bool HasTray { get; set; }

    public void OpenMountDialog() => _ = MountAsync();


    /// <summary>Показать диагностику окружения. Нужно пункту «Doctor» в меню
    /// трея (бриф §4.2): проверить среду, не открывая окно самому.</summary>
    public void OpenDoctor() => ShowEnvironment();

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

        // Счётчики кэша обновляются отдельно и часто. Раньше панель
        // перерисовывалась только при СМЕНЕ выделения и раз в тридцать секунд
        // вместе со временем работы: числа, обещанные живыми, стояли на нуле,
        // пока по тому шли чтения. Отдельный таймер трогает только панель и
        // не пересобирает список — иначе выделение слетало бы каждые две
        // секунды прямо под курсором.
        _panelTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _panelTimer.Tick += (_, _) =>
        {
            if (_panelMode == SidePanelMode.Mount && MountsList.SelectedItem is MountEntry live)
                ShowMountDetails(live);
        };
        _panelTimer.Start();

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

    /// <summary>Перечитывает список у службы. Нужно, когда том сняли МИМО
    /// окна — из меню в трее: иначе таблица показывает том, которого уже нет,
    /// и «Unmount» по нему ничего не делает.</summary>
    public void ReloadMounts()
    {
        var live = MountService.Instance.Entries;
        _mounts.Clear();
        foreach (var entry in live) _mounts.Add(entry);
        RefreshMounts();
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
                Foreground = Palette.Err,
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
        Foreground = Palette.Muted,
        FontSize = 12,
    };

    private static Control RenderCheck(Check check)
    {
        var colour = check.Status switch
        {
            CheckStatus.Ok => ((SolidColorBrush)Palette.Ok).Color,
            CheckStatus.Warn => ((SolidColorBrush)Palette.Warn).Color,
            _ => ((SolidColorBrush)Palette.Err).Color,
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
            Foreground = Palette.Text,
            FontSize = 12,
            Width = 96,
        });
        head.Children.Add(new TextBlock
        {
            Text = check.Value,
            Foreground = Palette.Muted,
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
                Foreground = Palette.Faint,
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
                Foreground = Palette.Faint,
                FontSize = 11, Width = 80,
            });
            row.Children.Add(new TextBlock
            {
                Text = value,
                Foreground = Palette.Text,
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

        ShowCacheCounters(entry, Row);
        ShowLogTail(entry);

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

    /// <summary>Счётчики кэшей тома (макет 03). Числа берутся у работающих
    /// кэшей, а не считаются заново: панель ОТЧИТЫВАЕТСЯ о томе, а не
    /// изображает отчёт.</summary>
    private void ShowCacheCounters(MountEntry entry, Action<string, string> Row)
    {
        var stats = MountService.Instance.StatsFor(entry.MountPoint);
        if (stats is null)
        {
            // Том мог быть смонтирован другим процессом или уже сниматься.
            // Пустая рамка «0 / 0 / 0%» солгала бы, что кэш простаивает.
            SidePanel.Children.Add(Heading("CACHE"));
            SidePanel.Children.Add(Muted("no counters: this volume is not held by this process"));
            return;
        }

        SidePanel.Children.Add(Heading("CACHE"));
        Row("hit rate", stats.HitRate is { } rate
            ? $"{rate * 100:0.0}%"
            : "nothing read yet");
        Row("trees", $"{stats.TreeHits} hit / {stats.TreeMisses} miss");
        Row("listings", $"{stats.ListingHits} hit / {stats.ListingMisses} miss");
        Row("paths", $"{stats.PathHits} hit / {stats.PathMisses} miss");
        Row("tree memory", $"{Bytes(stats.TreeBytes)} of {Bytes(stats.TreeBudget)}");
        Row("objects", $"{stats.PackedObjects:N0} packed · {stats.Packs} pack" + (stats.Packs == 1 ? "" : "s"));
        Row("deltas", $"{stats.DeltaHits} hit");
        // Переоткрытия — единственный показатель деградации, обещанный
        // макетом. Ноль пишется словом: «0» в столбце цифр читается как
        // «счётчик сломан», а не «этого не случалось».
        Row("reopens", stats.Reopens == 0 ? "none" : stats.Reopens.ToString());
        Row("sandbox", stats.OverlayFiles == 0
            ? "nothing written"
            : $"{stats.OverlayFiles} file{(stats.OverlayFiles == 1 ? "" : "s")} · {Bytes(stats.OverlayBytes)}");
    }

    private static string Bytes(long value) => value switch
    {
        < 1024 => value + " B",
        < 1024 * 1024 => $"{value / 1024.0:0.#} KB",
        < 1024L * 1024 * 1024 => $"{value / (1024.0 * 1024):0.#} MB",
        _ => $"{value / (1024.0 * 1024 * 1024):0.##} GB",
    };

    /// <summary>Хвост журнала ВЫБРАННОГО ТОМА (макет 03). Здесь стоял журнал
    /// приложения: он одинаков для всех строк таблицы, и переключение тома
    /// его не меняло — заголовок «LOG» под выделенной строкой обещал журнал
    /// этого тома, а показывал исключения самого GUI.
    ///
    /// Три исхода, и все три разные: строки есть; журнал пуст (всё шло
    /// хорошо); журнала не достать (том держит другой процесс и не отвечает).
    /// Слить последние два в «empty» значило бы сказать «всё в порядке» там,
    /// где мы просто не знаем.</summary>
    private void ShowLogTail(MountEntry entry)
    {
        // Заголовок — имя файла, а не слово «LOG»: он говорит, ОТКУДА текст,
        // и человек может открыть тот же файл сам (макет 03: «logcap»).
        SidePanel.Children.Add(Heading("log.txt"));
        var lines = MountService.Instance.LogFor(entry.MountPoint, 6);
        if (lines is null)
        {
            SidePanel.Children.Add(Muted("out of reach: this volume is held by another process"));
            return;
        }
        if (lines.Count == 0)
        {
            SidePanel.Children.Add(Muted("empty — nothing has gone wrong yet"));
            return;
        }
        SidePanel.Children.Add(Terminal(lines, 10));
    }

    /// <summary>Блок чужого текста — журнала, вывода команды. Тёмный В ОБЕИХ
    /// темах: так в токенах макета («--term не переопределяется»). Смысл не в
    /// красоте — цитата обязана отличаться от собственных слов окна, иначе
    /// строка журнала читается как утверждение продукта.</summary>
    private static Control Terminal(IReadOnlyList<string> lines, double fontSize) => new Border
    {
        Background = Palette.Term,
        CornerRadius = new Avalonia.CornerRadius(6),
        Padding = new Avalonia.Thickness(11, 10),
        Child = new StackPanel
        {
            Children =
            {
                new TextBlock
                {
                    Text = string.Join('\n', lines),
                    Foreground = Palette.TermInk,
                    FontFamily = new FontFamily("Cascadia Mono, Consolas, monospace"),
                    FontSize = fontSize,
                    TextWrapping = TextWrapping.Wrap,
                    LineHeight = fontSize * 1.7,
                },
            },
        },
    };

    private static TextBlock Heading(string text) => new()
    {
        Text = text,
        Classes = { "kicker" },
        Margin = new Avalonia.Thickness(0, 14, 0, 4),
    };

    // ---------- тост ----------

    private MountEntry? _toastMount;
    private DispatcherTimer? _toastTimer;

    /// <summary>Сообщение об удачном монтировании с прямым переходом к тому.
    /// Смысл именно в кнопке: сказать «готово» и оставить человека искать
    /// букву диска самому — это половина сообщения.</summary>
    private void ShowToast(MountEntry entry)
    {
        _toastMount = entry;
        ToastTitle.Text = $"{entry.MountPoint} is ready";
        ToastDetail.Text = $"{entry.Repository} · {entry.Views.Count(c => c != ' ' && c != '·')} views";
        Toast.IsVisible = true;

        // Тост исчезает сам. Он сообщает о том, что УЖЕ случилось, и висеть
        // до щелчка ему незачем — а закрывать каждый вручную утомительно.
        _toastTimer?.Stop();
        _toastTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(12) };
        _toastTimer.Tick += (_, _) => HideToast();
        _toastTimer.Start();
    }

    private void HideToast()
    {
        _toastTimer?.Stop();
        _toastTimer = null;
        Toast.IsVisible = false;
        _toastMount = null;
    }

    private void OnToastOpen(object? sender, RoutedEventArgs e)
    {
        // Том мог быть снят за те секунды, что тост висит — из меню в трее
        // или из этого же окна. Открывать по мёртвому пути значит показать
        // человеку пустую папку и промолчать о причине.
        if (_toastMount is { } mount
            && MountService.Instance.Entries.Any(m => m.MountPoint == mount.MountPoint))
        {
            OpenInFileManager(mount.MountPoint);
        }
        else
        {
            StatusText.Text = "that volume is no longer mounted";
        }
        HideToast();
    }

    private void OnToastDismiss(object? sender, RoutedEventArgs e) => HideToast();

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

            ShowToast(entry);
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
