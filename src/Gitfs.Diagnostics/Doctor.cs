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
        var checks = new List<Check> { CheckWinFsp(), CheckGit() };
        if (OperatingSystem.IsWindows()) checks.Add(CheckDriveLetters());
        if (repoPath is not null) checks.AddRange(CheckRepository(repoPath));
        checks.Add(CheckOverlays());
        return checks;
    }

    private static Check CheckWinFsp()
    {
        if (!OperatingSystem.IsWindows())
            return new Check(CheckStatus.Ok, "winfsp", "not needed on this platform");
        var version = ReadWinFspVersion();
        return version is null
            ? new Check(CheckStatus.Fail, "winfsp", "not installed",
                $"gitfs needs it to create a volume; install from {WinFspDownload}",
                "docs.gitfs.dev/e/winfsp-missing")
            : new Check(CheckStatus.Ok, "winfsp", version);
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
                "free a letter, or mount into a folder with --mount-point")
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
