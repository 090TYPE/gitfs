using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Gitfs.App;

/// <summary>Экран настроек. Бриф §4.2 называет пункт «Настройки» в меню
/// трея; вместо экрана там стоял один пункт, перебиравший темы по кругу —
/// чтобы попасть в нужную, приходилось нажимать его дважды и смотреть, что
/// вышло, а больше настроек не существовало вовсе.
///
/// Здесь ровно то, что у приложения ЕСТЬ: тема, пути двух файлов и хвост
/// собственного журнала. Придумывать переключатели «на будущее» нельзя —
/// настройка, которая ничего не меняет, хуже отсутствующей.</summary>
public partial class SettingsWindow : Window
{
    /// <summary>Пока окно наполняется, IsCheckedChanged летит от каждого
    /// переключателя — и без этого флага открытие настроек ЗАПИСЫВАЛО бы
    /// тему, которую никто не выбирал.</summary>
    private bool _loading = true;

    public SettingsWindow()
    {
        InitializeComponent();

        switch (Settings.Theme)
        {
            case ThemeChoice.Light: ThemeLight.IsChecked = true; break;
            case ThemeChoice.Dark: ThemeDark.IsChecked = true; break;
            default: ThemeAuto.IsChecked = true; break;
        }
        ReduceMotion.IsChecked = Settings.ReduceMotion;
        _loading = false;

        SettingsPath.Text = Settings.Path;
        LogPath.Text = Program.LogPath;

        var lines = Program.TailLog(8);
        LogTail.Text = lines.Count == 0
            ? "empty — nothing has gone wrong yet"
            : string.Join('\n', lines);

        var version = typeof(SettingsWindow).Assembly.GetName().Version;
        VersionText.Text = version is null ? "gitfs" : "gitfs " + version.ToString(3);
    }

    private void OnThemeChanged(object? sender, RoutedEventArgs e)
    {
        if (_loading) return;
        if (ThemeLight.IsChecked == true) Settings.Theme = ThemeChoice.Light;
        else if (ThemeDark.IsChecked == true) Settings.Theme = ThemeChoice.Dark;
        else if (ThemeAuto.IsChecked == true) Settings.Theme = ThemeChoice.Auto;
    }

    private void OnMotionChanged(object? sender, RoutedEventArgs e)
    {
        if (_loading) return;
        Settings.ReduceMotion = ReduceMotion.IsChecked == true;
        // Уже открытые окна должны перестать двигаться СЕЙЧАС, а не после
        // перезапуска: настройку меняют именно потому, что движение мешает
        // прямо сейчас.
        Motion.Apply();
    }

    /// <summary>Показывает файл в файловом менеджере. Именно показывает, а не
    /// открывает: settings.txt и app.log — обычные текстовые файлы, и чем их
    /// открыть, решает система, а не мы. Файла может ещё не быть — тогда
    /// показываем каталог, потому что «ничего не произошло» по нажатию
    /// кнопки выглядит как поломка.</summary>
    private static void Show(string path)
    {
        try
        {
            var target = File.Exists(path) ? Path.GetDirectoryName(path) ?? path : path;
            if (!Directory.Exists(target))
            {
                target = Path.GetDirectoryName(path);
                if (target is null || !Directory.Exists(target)) return;
            }
            using var process = new System.Diagnostics.Process();
            process.StartInfo = new System.Diagnostics.ProcessStartInfo(target)
            {
                UseShellExecute = true,
            };
            process.Start();
        }
        catch (Exception e) { Program.Log("settings-show", e); }
    }

    private void OnShowSettingsFile(object? sender, RoutedEventArgs e) => Show(Settings.Path);

    private void OnShowLogFile(object? sender, RoutedEventArgs e) => Show(Program.LogPath);

    private void OnClose(object? sender, RoutedEventArgs e) => Close();
}
