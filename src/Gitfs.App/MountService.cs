using Gitfs.Diagnostics;
using Gitfs.Vfs;
using Gitfs.Vfs.Overlay;
using Gitfs.Vfs.Views;
#if GITFS_WINFSP
using Gitfs.Mount.WinFsp;
#endif

namespace Gitfs.App;

public sealed record MountEntry(string Repository, string Path, string MountPoint,
    string Views, DateTimeOffset Since)
{
    public string Uptime
    {
        get
        {
            var span = DateTimeOffset.UtcNow - Since;
            return span.TotalHours >= 1
                ? $"{(int)span.TotalHours}h {span.Minutes:00}m"
                : $"{span.Minutes}m";
        }
    }
}

/// <summary>Монтирования процесса. На платформах без адаптера файловой
/// системы (Linux/macOS до вехи M6) CanMount = false: дерево можно
/// просматривать в приложении, но том не создаётся.</summary>
public sealed class MountService
{
    public static MountService Instance { get; } = new();

    private readonly List<MountEntry> _entries = new();
    private readonly Dictionary<string, IDisposable> _live = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<MountEntry> Entries => _entries;

#if GITFS_WINFSP
    public bool CanMount => OperatingSystem.IsWindows();
    public string? MountBlockedReason => CanMount ? null
        : "the filesystem adapter for this platform is not built yet";
#else
    public bool CanMount => false;
    public string? MountBlockedReason =>
        "the FUSE adapter is milestone M6 — mounting is Windows-only for now";
#endif

    public static VirtualTree BuildTree(IReadOnlyCollection<string> views)
    {
        var names = NamePolicy.For(NamePolicyKind.Native);
        var list = new List<IView>();
        if (views.Contains("branches")) list.Add(new BranchesView(names));
        if (views.Contains("tags")) list.Add(new TagsView(names));
        if (views.Contains("commits")) list.Add(new CommitsView(names));
        if (views.Contains("dates")) list.Add(new DatesView(names));
        if (views.Contains("history")) list.Add(new HistoryView(names));
        return new VirtualTree(list);
    }

    public static string? ResolveGitDir(string repoPath) => Doctor.ResolveGitDir(repoPath);

    public static IReadOnlyList<Check> Diagnose(string? repoPath) => Doctor.Run(repoPath);

    public static IReadOnlyList<char> FreeDriveLetters()
    {
        if (!OperatingSystem.IsWindows()) return [];
        var used = DriveInfo.GetDrives().Select(d => d.Name[0]).ToHashSet();
        return "GHIJKLMNOPQRSTUVWXYZ".Where(c => !used.Contains(c)).ToList();
    }

    /// <summary>Монтирует репозиторий. Бросает с человеческим текстом,
    /// если платформа не поддерживает монтирование или драйвер недоступен.</summary>
    public MountEntry Mount(string repoPath, string mountPoint, IReadOnlyCollection<string> views)
    {
        if (!CanMount) throw new InvalidOperationException(MountBlockedReason!);
        var gitDir = ResolveGitDir(repoPath)
            ?? throw new InvalidOperationException($"no .git directory in {repoPath}");

#if GITFS_WINFSP
        var manager = new SnapshotManager(gitDir);
        var overlay = OverlayStore.Create();
        var target = new VfsMountTarget(manager, BuildTree(views),
            new DirectoryInfo(repoPath).Name, readOnly: false, overlay: overlay);
        var mount = GitfsMount.Mount(target, mountPoint, readOnly: false);
        _live[mountPoint] = mount;
#endif
        var entry = new MountEntry(new DirectoryInfo(repoPath).Name, repoPath, mountPoint,
            string.Join(' ', new[] { "branches", "tags", "commits", "dates", "history" }
                .Select(v => views.Contains(v) ? v[0].ToString() : "·")),
            DateTimeOffset.UtcNow);
        _entries.Add(entry);
        return entry;
    }

    public void Unmount(MountEntry entry)
    {
        if (_live.Remove(entry.MountPoint, out var mount)) mount.Dispose();
        _entries.Remove(entry);
    }

    public void UnmountAll()
    {
        foreach (var mount in _live.Values) mount.Dispose();
        _live.Clear();
        _entries.Clear();
    }
}
