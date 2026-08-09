using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;

using Avalonia.Media;
using Avalonia.Platform.Storage;

namespace Gitfs.App;

public sealed record MountRequest(string RepositoryPath, string MountPoint,
    IReadOnlyCollection<string> Views, Gitfs.Vfs.MountOptions Options);

public partial class MountDialog : Window
{
    /// <summary>Точка монтирования буквой доступна только на Windows;
    /// иначе это папка. Раньше пустой список букв давал диалог с навсегда
    /// серой кнопкой и без объяснений (находка ревью).</summary>
    private readonly bool _usesDriveLetters = OperatingSystem.IsWindows();

    public MountDialog()
    {
        InitializeComponent();
        try
        {
            var letters = _usesDriveLetters ? MountService.FreeDriveLetters() : [];
            LetterBox.ItemsSource = letters.Select(c => c + ":").ToList();
            if (letters.Count > 0) LetterBox.SelectedIndex = 0;
            LetterBox.IsVisible = _usesDriveLetters && letters.Count > 0;
            FolderBox.IsVisible = !LetterBox.IsVisible;

            LettersHint.Text = LetterBox.IsVisible
                ? "free: " + string.Join(' ', letters.Take(6))
                : _usesDriveLetters
                    ? "every drive letter is taken — free one, or mount into a folder"
                    : "on this platform a mount point is a folder";
            if (!LetterBox.IsVisible)
                FolderBox.Text = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    "mnt", "gitfs");
        }
        catch (Exception e)
        {
            Program.Log("mount-dialog-init", e);
            LettersHint.Text = "could not enumerate drives: " + e.Message;
        }

        PolicyBox.ItemsSource = new[] { "native", "portable" };
        PolicyBox.SelectedIndex = 0;

        try { RepoBox.Text = Directory.GetCurrentDirectory(); }
        catch (Exception) { RepoBox.Text = ""; } // текущий каталог мог исчезнуть
        UpdatePreview();
    }

    private string? MountPoint => LetterBox.IsVisible
        ? LetterBox.SelectedItem as string
        : string.IsNullOrWhiteSpace(FolderBox.Text) ? null : FolderBox.Text;


    private List<string> SelectedViews()
    {
        var views = new List<string>();
        if (ViewBranches.IsChecked == true) views.Add("branches");
        if (ViewTags.IsChecked == true) views.Add("tags");
        if (ViewCommits.IsChecked == true) views.Add("commits");
        if (ViewDates.IsChecked == true) views.Add("dates");
        if (ViewHistory.IsChecked == true) views.Add("history");
        return views;
    }

    /// <summary>Живое дерево: снятая вьюха гаснет и зачёркивается, а не
    /// исчезает — выбор остаётся обратимым визуально (правило макета).</summary>
    private void UpdatePreview()
    {
        TreePreview.Children.Clear();
        var mount = MountPoint ?? "G:";
        var selected = SelectedViews();

        void Line(string text, int indent, bool enabled, bool accent = false)
        {
            TreePreview.Children.Add(new TextBlock
            {
                Text = new string(' ', indent) + text,
                FontFamily = new FontFamily("Cascadia Mono, Consolas, monospace"),
                FontSize = 12,
                Foreground = new SolidColorBrush(Color.Parse(
                    !enabled ? "#595d6c" : accent ? "#d2cefd" : "#cfd3e5")),
                TextDecorations = enabled ? null : TextDecorations.Strikethrough,
            });
        }

        Line(mount + "\\", 0, true);
        foreach (var view in new[] { "branches", "tags", "commits", "dates", "history" })
            Line(view + "\\", 2, selected.Contains(view), view == "history");

        if (selected.Contains("history"))
        {
            Line("src\\", 4, true);
            Line("Program.cs\\", 6, true, accent: true);
            Line("0001-a3f9c21.cs", 8, true);
            Line("0002-8b04e77.cs", 8, true);
            Line("latest.cs", 8, true, accent: true);
        }

        var repoOk = !string.IsNullOrWhiteSpace(RepoBox.Text)
                     && MountService.ResolveGitDir(RepoBox.Text!) is not null;
        RepoProblem.IsVisible = !repoOk && !string.IsNullOrWhiteSpace(RepoBox.Text);
        RepoProblem.Text = "No .git directory here. Pick the repository root.";

        // Настройки проверяются здесь же: неверное число гасит кнопку и
        // называет причину, а не всплывает исключением после нажатия.
        var options = ReadOptions();
        var problem = options.Validate();
        AdvancedProblem.IsVisible = problem is not null;
        AdvancedProblem.Text = problem;
        if (problem is not null) AdvancedBox.IsExpanded = true;

        MountAction.IsEnabled = repoOk && selected.Count > 0 && MountPoint is not null
                                && problem is null;
        MountAction.Content = MountPoint is { } point ? $"Mount to {point}" : "Mount";

        // Подвал показывает то, что реально выбрано, а не постоянную надпись
        var flags = new List<string> { $"{selected.Count} view{(selected.Count == 1 ? "" : "s")}" };
        flags.Add(options.ReadOnly ? "read-only" : "writes go to a sandbox");
        if (options.KeepOverlay) flags.Add("overlay kept");
        if (options.NamePolicy == Gitfs.Vfs.NamePolicyKind.Portable) flags.Add("portable names");
        if (options.HistoryRef is { } r) flags.Add("history from " + r);
        FooterFlags.Text = string.Join(" · ", flags);
    }

    /// <summary>Собирает настройки из полей Advanced. Пустое или нечисловое
    /// поле означает «оставить как есть» — диалог не должен наказывать за
    /// недописанное число, пока пользователь его набирает.</summary>
    private Gitfs.Vfs.MountOptions ReadOptions()
    {
        static int Number(TextBox box, int fallback) =>
            int.TryParse(box.Text?.Trim(), out var value) ? value : fallback;

        return new Gitfs.Vfs.MountOptions
        {
            HistoryRef = string.IsNullOrWhiteSpace(HistoryRefBox.Text) ? null : HistoryRefBox.Text!.Trim(),
            CommitLimit = Number(CommitLimitBox, 200),
            HistoryLimit = Number(HistoryLimitBox, 500),
            CacheMegabytes = Number(CacheBox, 96),
            MaxCachedBlobMegabytes = Number(MaxBlobBox, 8),
            NamePolicy = PolicyBox.SelectedIndex == 1
                ? Gitfs.Vfs.NamePolicyKind.Portable
                : Gitfs.Vfs.NamePolicyKind.Native,
            ReadOnly = ReadOnlyBox.IsChecked == true,
            KeepOverlay = KeepOverlayBox.IsChecked == true,
        };
    }

    private void OnAdvancedChanged(object? sender, RoutedEventArgs e) => UpdatePreview();
    private void OnAdvancedChanged(object? sender, TextChangedEventArgs e) => UpdatePreview();
    private void OnAdvancedChanged(object? sender, SelectionChangedEventArgs e) => UpdatePreview();

    private void OnViewToggled(object? sender, RoutedEventArgs e) => UpdatePreview();
    private void OnLetterChanged(object? sender, SelectionChangedEventArgs e) => UpdatePreview();
    private void OnRepoChanged(object? sender, TextChangedEventArgs e) => UpdatePreview();

    private void OnFolderChanged(object? sender, TextChangedEventArgs e) => UpdatePreview();

    private void OnBrowse(object? sender, RoutedEventArgs e) => _ = BrowseAsync();

    /// <summary>Диалог выбора папки может отсутствовать (Linux без портала)
    /// или бросить COM-исключение: раньше это роняло процесс (находка ревью).</summary>
    private async Task BrowseAsync()
    {
        try
        {
            var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = "Pick a git repository",
                AllowMultiple = false,
            });
            if (folders.Count == 0) return;
            var path = folders[0].TryGetLocalPath();
            if (path is null)
            {
                RepoProblem.IsVisible = true;
                RepoProblem.Text = "That location is not a local folder.";
                return;
            }
            RepoBox.Text = path;
            UpdatePreview();
        }
        catch (Exception ex)
        {
            Program.Log("browse", ex);
            RepoProblem.IsVisible = true;
            RepoProblem.Text = "Folder picker unavailable — type the path instead.";
        }
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(null);

    private void OnMount(object? sender, RoutedEventArgs e)
    {
        var views = SelectedViews();
        if (views.Count == 0 || MountPoint is not { } mountPoint) return;
        var options = ReadOptions();
        if (options.Validate() is not null) return; // кнопка и так серая; это второй рубеж
        Close(new MountRequest(RepoBox.Text!, mountPoint, views, options));
    }
}
