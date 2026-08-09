using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using Gitfs.Diagnostics;

namespace Gitfs.App;

/// <summary>Экран первого запуска (макет 05): «одна проверка перед первым
/// монтированием».
///
/// Строки берутся у doctor — того же, что печатает <c>gitfs doctor</c>.
/// Отдельный список проверок для онбординга однажды разошёлся бы с
/// настоящим, и человек увидел бы зелёный экран там, где том не создаётся.
///
/// Кнопка установки НИЧЕГО НЕ КАЧАЕТ И НЕ ЗАПУСКАЕТ: она открывает страницу
/// драйвера и начинает следить за проверкой. Приложение, которое молча
/// скачивает и запускает установщик, требует доверия, которого пользователь
/// ему не давал. Цикл «нет → ставится → есть» при этом настоящий: как только
/// драйвер появляется, строка становится зелёной сама.</summary>
public partial class FirstRunWindow : Window
{
    private readonly DispatcherTimer _poll;
    private Check? _blocker;

    /// <summary>Что делать после закрытия: открыть диалог монтирования или
    /// просто показать главное окно.</summary>
    public bool WantsToMount { get; private set; }

    public FirstRunWindow()
    {
        InitializeComponent();
        Recheck();

        // Пока экран открыт, проверка повторяется: драйвер ставится в другом
        // окне, и требовать «нажмите ещё раз» значит перекладывать на
        // человека работу, которую программа делает сама.
        _poll = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _poll.Tick += (_, _) => Recheck();
        _poll.Start();
        Closed += (_, _) => _poll.Stop();
    }

    /// <summary>Экран нужен, когда монтировать нельзя, — или когда это
    /// вообще первый запуск. Во второй раз при здоровой среде он не
    /// появляется: приветствие, которое нельзя пройти навсегда, — препятствие.</summary>
    public static bool ShouldShow(IReadOnlyList<Check> checks) =>
        !FirstRunMarker.Seen() || checks.Any(c => c.Status == CheckStatus.Fail);

    private void Recheck()
    {
        IReadOnlyList<Check> checks;
        try { checks = Doctor.Run(null); }
        catch (Exception e)
        {
            Program.Log("first-run-doctor", e);
            return;
        }

        Rows.Children.Clear();
        foreach (var check in checks)
        {
            var row = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("Auto,Auto,*"),
            };
            row.Children.Add(Cell(Symbol(check.Status), 0, Colour(check.Status), bold: true));
            row.Children.Add(Cell(check.Name, 1, "#e9e9ed", width: 90));
            row.Children.Add(Cell(check.Value, 2, "#9397ab"));
            Rows.Children.Add(row);
        }

        _blocker = checks.FirstOrDefault(c => c.Status == CheckStatus.Fail);
        var ready = _blocker is null;

        FixText.IsVisible = _blocker?.Fix is not null;
        FixText.Text = _blocker?.Fix;

        InstallAction.IsVisible = _blocker is { Link: not null } or { Value: not null } && !ready
                                  && DownloadPage(_blocker) is not null;
        InstallAction.Content = InstallLabel(_blocker);
        RecheckAction.IsVisible = !ready;
        MountAction.IsVisible = ready;

        StepText.Text = ready
            ? "Step 2 of 2 · everything the volume needs is in place"
            : "Step 1 of 2 · next: mount your first repository";

        // Предупреждение — не отказ, но и не «всё хорошо». Зелёная фраза над
        // жёлтой строкой читается как противоречие, а противоречие в первом
        // же экране учит не верить остальным.
        var warned = checks.Any(c => c.Status == CheckStatus.Warn);
        Lead.Text = !ready
            ? "gitfs builds a real volume, so it needs a filesystem driver. Nothing else is required."
            : warned
                ? "Nothing blocks a mount. One thing above is worth knowing about."
                : "Everything gitfs needs is already here. The next step is a repository.";
    }

    private static string InstallLabel(Check? blocker) => blocker?.Name switch
    {
        "winfsp" => "Install WinFsp",
        "fuse" => "How to install fuse3",
        _ => "How to fix this",
    };

    /// <summary>Куда вести за помощью. Ссылка проверки — первая, потому что
    /// её пишет тот же код, что нашёл проблему.</summary>
    private static string? DownloadPage(Check? blocker) => blocker switch
    {
        null => null,
        { Name: "winfsp" } => "https://" + Doctor.WinFspDownload,
        { Link: { } link } => link.StartsWith("http") ? link : "https://" + link,
        _ => null,
    };

    private static string Symbol(CheckStatus status) => status switch
    {
        CheckStatus.Ok => "✓",
        CheckStatus.Warn => "▲",
        _ => "✕",
    };

    private static string Colour(CheckStatus status) => status switch
    {
        CheckStatus.Ok => "#7FB77E",
        CheckStatus.Warn => "#E0B26D",
        _ => "#E07B6D",
    };

    private static TextBlock Cell(string text, int column, string colour,
        bool bold = false, double? width = null)
    {
        var block = new TextBlock
        {
            Text = text,
            Foreground = new SolidColorBrush(Color.Parse(colour)),
            FontSize = 13,
            Margin = new Thickness(0, 0, 12, 0),
            FontWeight = bold ? FontWeight.Bold : FontWeight.Normal,
            TextWrapping = TextWrapping.Wrap,
        };
        if (width is { } w) block.Width = w;
        Grid.SetColumn(block, column);
        return block;
    }

    private void OnInstall(object? sender, RoutedEventArgs e)
    {
        if (DownloadPage(_blocker) is not { } url) return;
        try
        {
            using var process = new System.Diagnostics.Process();
            process.StartInfo = new System.Diagnostics.ProcessStartInfo(url)
            {
                UseShellExecute = true,
            };
            process.Start();
            FixText.IsVisible = true;
            FixText.Text = "Opened " + url +
                " — gitfs is watching, and this screen turns green on its own once the driver is there.";
        }
        catch (Exception ex)
        {
            Program.Log("first-run-open", ex);
            FixText.IsVisible = true;
            FixText.Text = "Could not open a browser. The page is " + url;
        }
    }

    private void OnRecheck(object? sender, RoutedEventArgs e) => Recheck();

    private void OnMount(object? sender, RoutedEventArgs e)
    {
        WantsToMount = true;
        FirstRunMarker.Remember();
        Close();
    }

    private void OnDismiss(object? sender, RoutedEventArgs e)
    {
        FirstRunMarker.Remember();
        Close();
    }
}

/// <summary>Отметка «экран первого запуска уже видели». Обычный пустой файл:
/// его можно удалить руками, чтобы посмотреть онбординг заново, — и это
/// единственный способ, который не требует объяснений.</summary>
public static class FirstRunMarker
{
    public static string Path { get; set; } = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "gitfs", "first-run-done");

    public static bool Seen()
    {
        try { return File.Exists(Path); }
        catch (Exception) { return true; } // не можем проверить — не мешаем
    }

    public static void Remember()
    {
        try
        {
            var dir = System.IO.Path.GetDirectoryName(Path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(Path, DateTimeOffset.UtcNow.ToString("O"));
        }
        catch (Exception e) { Program.Log("first-run-marker", e); }
    }
}
