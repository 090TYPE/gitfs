using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
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

            // Выбор буквы предлагается только там, где буквы вообще есть.
            // Где их нет — выбора нет, и переключатель из двух вариантов, у
            // которого доступен один, только сбивает с толку.
            var lettersUsable = _usesDriveLetters && letters.Count > 0;
            MountKindBox.IsVisible = lettersUsable;
            KindLetter.IsChecked = lettersUsable;
            KindFolder.IsChecked = !lettersUsable;

            LettersHint.Text = lettersUsable
                ? "free: " + string.Join(' ', letters.Take(6))
                : _usesDriveLetters
                    ? "every drive letter is taken — free one, or mount into a folder"
                    : "on this platform a mount point is a folder";

            FolderBox.Text = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "mnt", "gitfs");
            ApplyMountKind();
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
        BuildRecentChips();
        UpdatePreview();
    }

    private string? MountPoint => LetterBox.IsVisible
        ? LetterBox.SelectedItem as string
        : string.IsNullOrWhiteSpace(FolderBox.Text) ? null : FolderBox.Text;

    private void ApplyMountKind()
    {
        var byLetter = KindLetter.IsChecked == true && MountKindBox.IsVisible;
        LetterBox.IsVisible = byLetter;
        FolderBox.IsVisible = !byLetter;
    }

    private void OnMountKindChanged(object? sender, RoutedEventArgs e)
    {
        // Событие приходит дважды — со снятого и с надетого переключателя;
        // обе ветки ведут в одно и то же состояние, поэтому это безвредно.
        ApplyMountKind();
        UpdatePreview();
    }

    /// <summary>Фишки недавних репозиториев (макет 03). Исчезнувший путь
    /// остаётся в списке, но гаснет и объясняется подсказкой: молча вычистить
    /// его — значит соврать о том, что человек монтировал.</summary>
    private void BuildRecentChips()
    {
        RecentChips.Children.Clear();
        List<RecentRepositories.Entry> entries;
        try { entries = RecentRepositories.Instance.Load().ToList(); }
        catch (Exception e) { Program.Log("recent-chips", e); return; }

        RecentChips.IsVisible = entries.Count > 0;
        foreach (var entry in entries)
        {
            var chip = new Button
            {
                Content = entry.Name,
                Classes = { "secondary" },
                Margin = new Thickness(0, 6, 6, 0),
                Padding = new Thickness(10, 3),
                FontSize = 12,
                Foreground = new SolidColorBrush(Color.Parse(
                    entry.StillARepository ? "#cfd3e5" : "#7c8194")),
            };
            ToolTip.SetTip(chip, entry.StillARepository
                ? entry.Path
                : entry.Path + "\nthis path is no longer a git repository");
            var path = entry.Path;
            chip.Click += (_, _) => { RepoBox.Text = path; UpdatePreview(); };
            RecentChips.Children.Add(chip);
        }
    }


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

        // Что станет с папкой, известно ЗАРАНЕЕ. Раньше кнопка была активна,
        // а монтирование падало строкой «каталога нет» — узнать об этом после
        // нажатия значит узнать слишком поздно.
        var folderProblem = FolderBox.IsVisible ? MountFolderNote(MountPoint) : null;
        FolderNote.IsVisible = folderProblem is not null;
        FolderNote.Text = folderProblem?.Text;
        FolderNote.Foreground = new SolidColorBrush(Color.Parse(
            folderProblem is { Blocking: true } ? "#E07B6D" : "#9397ab"));

        // Настройки проверяются здесь же: неверное число гасит кнопку и
        // называет причину, а не всплывает исключением после нажатия.
        var options = ReadOptions();
        var problem = options.Validate();
        AdvancedProblem.IsVisible = problem is not null;
        AdvancedProblem.Text = problem;
        if (problem is not null) AdvancedBox.IsExpanded = true;

        MountAction.IsEnabled = repoOk && selected.Count > 0 && MountPoint is not null
                                && problem is null && folderProblem is not { Blocking: true };
        MountAction.Content = MountPoint is { } point ? $"Mount to {point}" : "Mount";

        // Подвал показывает то, что реально выбрано, а не постоянную надпись
        var flags = new List<string> { $"{selected.Count} view{(selected.Count == 1 ? "" : "s")}" };
        flags.Add(options.ReadOnly ? "read-only" : "writes go to a sandbox");
        if (options.KeepOverlay) flags.Add("overlay kept");
        if (options.NamePolicy == Gitfs.Vfs.NamePolicyKind.Portable) flags.Add("portable names");
        if (options.HistoryRef is { } r) flags.Add("history from " + r);
        FooterFlags.Text = string.Join(" · ", flags);

        AdvancedSummary.Text = SummarizeAdvanced(options);
    }

    /// <summary>Что в Advanced отличается от умолчаний. Свёрнутая секция,
    /// внутри которой всё изменено, выглядит нетронутой — а решение
    /// «монтировать» принимается по тому, что видно.</summary>
    private static string SummarizeAdvanced(Gitfs.Vfs.MountOptions options)
    {
        var d = Gitfs.Vfs.MountOptions.Default;
        var changed = new List<string>();
        if (options.HistoryRef is { } r) changed.Add("history from " + r);
        if (options.CommitLimit != d.CommitLimit) changed.Add($"{options.CommitLimit} commits");
        if (options.HistoryLimit != d.HistoryLimit) changed.Add($"{options.HistoryLimit} versions");
        if (options.CacheMegabytes != d.CacheMegabytes) changed.Add($"{options.CacheMegabytes} MB cache");
        if (options.MaxCachedBlobMegabytes != d.MaxCachedBlobMegabytes)
            changed.Add($"{options.MaxCachedBlobMegabytes} MB per object");
        if (options.NamePolicy != d.NamePolicy) changed.Add("portable names");
        if (options.ReadOnly) changed.Add("read-only");
        if (options.KeepOverlay) changed.Add("overlay kept");
        return changed.Count == 0 ? "" : "· " + string.Join(", ", changed);
    }

    private sealed record FolderNoteText(string Text, bool Blocking);

    /// <summary>Что произойдёт с выбранной папкой. Отсутствующая — будет
    /// создана (это делает MountService, не адаптер); непустая — препятствие:
    /// том поверх неё спрячет содержимое до размонтирования.</summary>
    private static FolderNoteText? MountFolderNote(string? folder)
    {
        if (string.IsNullOrWhiteSpace(folder)) return null;
        try
        {
            if (File.Exists(folder))
                return new FolderNoteText("That is a file, not a folder.", true);
            if (!Directory.Exists(folder))
                return new FolderNoteText("This folder does not exist yet — gitfs will create it.", false);
            return Directory.EnumerateFileSystemEntries(folder).Any()
                ? new FolderNoteText(
                    "This folder is not empty. A volume would hide what is in it.", true)
                : null;
        }
        catch (Exception e)
        {
            return new FolderNoteText("Cannot inspect this folder: " + e.Message, true);
        }
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

    /// <summary>Раскрытая секция подтягивается в видимую часть. Без этого
    /// щелчок по «Advanced» открывал её ниже границы окна: визуально ничего
    /// не происходило, и это читалось как «кнопка не работает».</summary>
    private void OnAdvancedExpanded(object? sender, RoutedEventArgs e) =>
        Dispatcher.UIThread.Post(() => AdvancedBox.BringIntoView(),
            DispatcherPriority.Background);

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
