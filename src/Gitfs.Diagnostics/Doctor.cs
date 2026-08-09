using System.Runtime.Versioning;

namespace Gitfs.Diagnostics;

public enum CheckStatus { Ok, Warn, Fail }

/// <summary>Строка отчёта doctor. Макет дизайн-отдела: колонка статуса —
/// слово, колонка имени 22 знака, дальше значение; каждый fail несёт одну
/// строку «что сделать» и ссылку. Цвет несёт ТОЛЬКО статус — уберите цвет,
/// и таблица останется читаемой.</summary>
public sealed record Check(CheckStatus Status, string Name, string Value,
    string? Fix = null, string? Link = null);

/// <summary>Проверки среды. Ничего не мутирует: только смотрит.</summary>
public static class Doctor
{
    public const string WinFspRegistryKey = @"SOFTWARE\WOW6432Node\WinFsp";
    public const string WinFspDownload = "winfsp.dev/rel";

    public static IReadOnlyList<Check> Run(string? repoPath)
    {
        // Каждая платформа проверяет СВОЙ драйвер и молчит про чужой:
        // строка «winfsp: не нужен на этой платформе» под Linux не несла
        // никакой информации и занимала место там, где должен был стоять
        // единственный важный вопрос — есть ли libfuse3.
        var checks = new List<Check> { CheckDriver(), CheckGit() };
        if (OperatingSystem.IsWindows()) checks.Add(CheckDriveLetters());
        if (repoPath is not null) checks.AddRange(CheckRepository(repoPath));
        checks.Add(CheckMountedVolumes());
        checks.Add(CheckOverlays());
        return checks;
    }

    private static Check CheckDriver()
    {
        if (OperatingSystem.IsWindows()) return CheckWinFsp();
        if (OperatingSystem.IsLinux()) return CheckFuse();
        return new Check(CheckStatus.Fail, "adapter", "none for this platform",
            "macOS needs macFUSE, which gitfs does not carry yet; gitfs tree still works",
            "docs.gitfs.dev/e/no-adapter");
    }

    [SupportedOSPlatform("windows")]
    private static Check CheckWinFsp()
    {
        var version = ReadWinFspVersion();
        return version is null
            ? new Check(CheckStatus.Fail, "winfsp", "not installed",
                $"gitfs needs it to create a volume; install from {WinFspDownload}",
                "docs.gitfs.dev/e/winfsp-missing")
            : new Check(CheckStatus.Ok, "winfsp", version);
    }

    /// <summary>Смысл doctor в том, чтобы сказать «не хватает вот этого» ДО
    /// попытки смонтировать. Под Linux он этого не делал вовсе: показывал
    /// «winfsp не нужен» и молчал про libfuse3 — а без неё том не создаётся.
    /// Спрашиваем ровно то, что потребуется адаптеру: саму библиотеку,
    /// устройство и помощника, через которого монтирует непривилегированный
    /// пользователь.</summary>
    private static Check CheckFuse()
    {
        var missing = new List<string>();

        var loaded = false;
        foreach (var candidate in new[] { "libfuse3.so.3", "libfuse3.so", "libfuse.so.3" })
        {
            if (!System.Runtime.InteropServices.NativeLibrary.TryLoad(candidate, out var handle))
                continue;
            loaded = true;
            System.Runtime.InteropServices.NativeLibrary.Free(handle);
            break;
        }
        if (!loaded) missing.Add("libfuse3");

        // /dev/fuse — то, что открывает ядро; в контейнере его может не быть
        // даже когда библиотека установлена
        if (!File.Exists("/dev/fuse")) missing.Add("/dev/fuse");

        // без fusermount3 монтировать может только root
        var helper = new[] { "/usr/bin/fusermount3", "/bin/fusermount3", "/usr/local/bin/fusermount3" }
            .FirstOrDefault(File.Exists);
        if (helper is null && Environment.GetEnvironmentVariable("USER") != "root")
            missing.Add("fusermount3");

        if (missing.Count > 0)
            return new Check(CheckStatus.Fail, "fuse", "missing: " + string.Join(", ", missing),
                "install the fuse3 package from your distribution "
                + "(apt install fuse3 · dnf install fuse3 · apk add fuse3)",
                "docs.gitfs.dev/e/fuse-missing");

        return new Check(CheckStatus.Ok, "fuse", "libfuse3 + /dev/fuse");
    }

    [SupportedOSPlatform("windows")]
    private static string? ReadWinFspVersion()
    {
        using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(WinFspRegistryKey);
        if (key is null) return null;
        var dir = key.GetValue("InstallDir") as string;
        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return null;
        var dll = Path.Combine(dir, "bin", "winfsp-x64.dll");
        return File.Exists(dll)
            ? System.Diagnostics.FileVersionInfo.GetVersionInfo(dll).FileVersion ?? "installed"
            : "installed";
    }

    /// <summary>Запуск git ограничен таймаутом: внешний процесс не должен
    /// уметь подвесить вызывающего (в GUI это замораживало окно).</summary>
    private static Check CheckGit()
    {
        const int timeoutMs = 5000;
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo("git", "--version")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var p = System.Diagnostics.Process.Start(psi)!;
            var reader = p.StandardOutput.ReadToEndAsync();
            if (!p.WaitForExit(timeoutMs))
            {
                try { p.Kill(entireProcessTree: true); } catch (Exception) { /* уже умер */ }
                return new Check(CheckStatus.Warn, "git", "did not respond",
                    "gitfs reads repositories directly; git is only needed for tests");
            }
            var output = reader.GetAwaiter().GetResult().Trim();
            return p.ExitCode == 0
                ? new Check(CheckStatus.Ok, "git", output.Replace("git version ", ""))
                : new Check(CheckStatus.Warn, "git", "not usable",
                    "gitfs reads repositories directly; git is only needed for tests");
        }
        catch (Exception)
        {
            return new Check(CheckStatus.Warn, "git", "not found",
                "gitfs reads repositories directly; git is only needed for tests");
        }
    }

    [SupportedOSPlatform("windows")]
    private static Check CheckDriveLetters()
    {
        var used = DriveInfo.GetDrives().Select(d => d.Name[0]).ToHashSet();
        var free = "GHIJKLMNOPQRSTUVWXYZ".Where(c => !used.Contains(c)).Take(5).ToArray();
        return free.Length == 0
            ? new Check(CheckStatus.Fail, "drive letters", "none free",
                "free a letter, or mount into a folder: gitfs mount <repo> C:\\mnt\\gitfs")
            : new Check(CheckStatus.Ok, "drive letters", string.Join(' ', free) + " free");
    }

    private static IEnumerable<Check> CheckRepository(string repoPath)
    {
        var gitDir = ResolveGitDir(repoPath);
        if (gitDir is null)
        {
            yield return new Check(CheckStatus.Fail, "repository", $"no .git in {repoPath}",
                "point gitfs at a repository root, or run git init there first",
                "docs.gitfs.dev/e/not-a-repo");
            yield break;
        }
        yield return new Check(CheckStatus.Ok, "repository", gitDir);

        // формат хеша: sha256-репозитории отвергаются с внятной ошибкой (§6.8)
        var configPath = Path.Combine(gitDir, "config");
        var config = File.Exists(configPath) ? File.ReadAllText(configPath) : "";
        yield return config.Contains("objectformat = sha256", StringComparison.OrdinalIgnoreCase)
            ? new Check(CheckStatus.Fail, "hash format", "sha256",
                "gitfs v1 supports sha1 repositories only", "docs.gitfs.dev/e/sha256")
            : new Check(CheckStatus.Ok, "hash format", "sha1");

        yield return File.Exists(Path.Combine(gitDir, "shallow"))
            ? new Check(CheckStatus.Warn, "history", "shallow clone",
                "history is truncated; run git fetch --unshallow for the full tree")
            : new Check(CheckStatus.Ok, "history", "complete");

        var accel = new List<string>();
        if (File.Exists(Path.Combine(gitDir, "objects", "info", "commit-graph"))) accel.Add("commit-graph");
        if (File.Exists(Path.Combine(gitDir, "objects", "pack", "multi-pack-index"))) accel.Add("multi-pack-index");
        yield return accel.Count > 0
            ? new Check(CheckStatus.Ok, "accelerators", string.Join(" + ", accel))
            : new Check(CheckStatus.Warn, "accelerators", "none",
                "run git commit-graph write --reachable to speed up dates/ and history/");

        yield return CheckLfs(repoPath, gitDir);
    }

    /// <summary>Git LFS (спека §3.5 и §13). Pointer-файл отдаётся КАК ЕСТЬ,
    /// без гидрации: gitfs не ходит в сеть и не запускает фильтры. Молчать об
    /// этом нельзя — человек откроет на томе .psd и увидит три строки текста,
    /// решив, что сломан gitfs, а не что файл лежит в LFS.
    ///
    /// Спрашиваем .gitattributes рабочего дерева: именно он говорит, какие
    /// пути отданы фильтру lfs. Читать сами блобы ради этого не нужно.</summary>
    private static Check CheckLfs(string repoPath, string gitDir)
    {
        var attributes = Path.Combine(repoPath, ".gitattributes");
        var tracked = new List<string>();
        try
        {
            if (File.Exists(attributes))
            {
                foreach (var line in File.ReadLines(attributes))
                {
                    var text = line.Trim();
                    if (text.Length == 0 || text[0] == '#') continue;
                    if (!text.Contains("filter=lfs", StringComparison.OrdinalIgnoreCase)) continue;
                    var pattern = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)[0];
                    if (tracked.Count < 4) tracked.Add(pattern);
                    else if (tracked.Count == 4) tracked.Add("…");
                }
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }

        if (tracked.Count == 0)
        {
            // Нет .gitattributes — но LFS мог быть настроен и раньше; тогда в
            // .git остаётся его каталог. Об этом стоит сказать мягче.
            return Directory.Exists(Path.Combine(gitDir, "lfs"))
                ? new Check(CheckStatus.Warn, "git lfs", "used by this repository",
                    "gitfs serves pointer files as they are, without downloading the content",
                    "docs.gitfs.dev/e/lfs")
                : new Check(CheckStatus.Ok, "git lfs", "not used");
        }

        return new Check(CheckStatus.Warn, "git lfs", string.Join(" ", tracked),
            "those paths appear on the volume as pointer files, not as their content",
            "docs.gitfs.dev/e/lfs");
    }

    /// <summary>Что gitfs держит ПРЯМО СЕЙЧАС (макет 04: строки driver/cache/
    /// packs). Раньше doctor отвечал только на вопрос «смогу ли я
    /// смонтировать» и ни слова не говорил о том, что уже смонтировано, —
    /// а половина обращений к диагностике случается именно тогда, когда том
    /// уже работает и ведёт себя странно.
    ///
    /// Переоткрытие пакетов — предупреждение, а не отказ: том жив, просто
    /// репозиторий пересобрался под ним.</summary>
    private static Check CheckMountedVolumes()
    {
        var live = Vfs.Overlay.OverlayStore.FindLive();
        if (live.Count == 0) return new Check(CheckStatus.Ok, "volumes", "none mounted");

        var names = string.Join(" ", live.Select(m => m.MountPoint));
        // Переоткрытия видно из журнала тома: он лежит на самом томе, и
        // читать его — то же самое, что открыть .gitfs/log.txt глазами.
        var reopening = new List<string>();
        foreach (var mount in live)
        {
            try
            {
                var log = Path.Combine(mount.MountPoint, ".gitfs", "log.txt");
                if (!File.Exists(log)) continue;
                if (File.ReadLines(log).Any(l => l.Contains("pack-reopened", StringComparison.Ordinal)))
                    reopening.Add(mount.MountPoint);
            }
            catch (IOException) { }              // том мог сниматься прямо сейчас
            catch (UnauthorizedAccessException) { }
        }

        return reopening.Count == 0
            ? new Check(CheckStatus.Ok, "volumes", $"{live.Count} mounted: {names}")
            : new Check(CheckStatus.Warn, "volumes",
                $"packs reopened on {string.Join(" ", reopening)}",
                "safe to keep working; git gc under a mounted volume causes this",
                "docs.gitfs.dev/e/pack-reopened");
    }

    /// <summary>Осиротевшие песочницы спрашиваем у самой песочницы: свой
    /// перечислитель здесь считал брошенным КАЖДЫЙ каталог, включая тот,
    /// в который прямо сейчас пишет смонтированный том, — и предлагал его
    /// удалить.</summary>
    private static Check CheckOverlays()
    {
        var orphans = Vfs.Overlay.OverlayStore.FindOrphans();
        return orphans.Count == 0
            ? new Check(CheckStatus.Ok, "overlay", "clean")
            : new Check(CheckStatus.Warn, "overlay",
                $"{orphans.Count} orphaned in {OverlayRoot()}",
                "remove them with gitfs purge", "docs.gitfs.dev/e/overlay-orphan");
    }

    public static string OverlayRoot() => Vfs.Overlay.OverlayStore.DefaultRoot();

    /// <summary>Каталог .git репозитория: обычный каталог или файл-указатель
    /// «gitdir: …» (worktree/submodule). null — не репозиторий.</summary>
    public static string? ResolveGitDir(string repoPath)
    {
        var candidate = Path.Combine(repoPath, ".git");
        if (Directory.Exists(candidate)) return Path.GetFullPath(candidate);
        if (File.Exists(candidate))
        {
            var text = File.ReadAllText(candidate).Trim();
            const string prefix = "gitdir:";
            if (text.StartsWith(prefix, StringComparison.Ordinal))
            {
                var target = text[prefix.Length..].Trim();
                var full = Path.IsPathRooted(target) ? target : Path.Combine(repoPath, target);
                if (Directory.Exists(full)) return Path.GetFullPath(full);
            }
        }
        // сам каталог .git (bare или переданный напрямую)
        if (File.Exists(Path.Combine(repoPath, "HEAD")) && Directory.Exists(Path.Combine(repoPath, "objects")))
            return Path.GetFullPath(repoPath);
        return null;
    }
}
