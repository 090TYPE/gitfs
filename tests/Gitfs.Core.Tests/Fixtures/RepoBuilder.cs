using System.Diagnostics;

namespace Gitfs.Core.Tests.Fixtures;

/// <summary>Собирает настоящий git-репозиторий во временной папке.
/// Фиксированные автор/дата — SHA детерминированы между запусками.
/// gc отключён: объекты остаются loose (packfiles — фикстуры плана M1b).</summary>
public sealed class RepoBuilder : IDisposable
{
    public string Root { get; }
    public string GitDir => Path.Combine(Root, ".git");

    private static readonly (string, string)[] Env =
    {
        ("GIT_AUTHOR_NAME", "Fixture"),
        ("GIT_AUTHOR_EMAIL", "fixture@gitfs.test"),
        ("GIT_AUTHOR_DATE", "2026-01-01T12:00:00 +0000"),
        ("GIT_COMMITTER_NAME", "Fixture"),
        ("GIT_COMMITTER_EMAIL", "fixture@gitfs.test"),
        ("GIT_COMMITTER_DATE", "2026-01-01T12:00:00 +0000"),
        ("GIT_CONFIG_NOSYSTEM", "1"),
    };

    /// <summary>Имена уже созданных фикстур. Dispose сносит свой каталог
    /// рекурсивно, поэтому два экземпляра с одинаковым именем — это не
    /// «маловероятно», а порча: близнец удаляет объекты живого репозитория
    /// прямо посреди работы, и git падает на чтении того, что сам записал.
    /// Ровно так это и выглядело на раннере CI — 44 объекта вместо сотни и
    /// оборванная связь до родителя. Пусть лучше будет громкая ошибка
    /// здесь, чем расследование там.</summary>
    private static readonly HashSet<string> Taken = new(StringComparer.Ordinal);

    /// <summary>Кто и когда сносил каталоги в этом процессе. Единственное,
    /// что удаляет их, — Dispose; если git спотыкается об исчезнувший
    /// каталог, ответ обязан быть здесь.</summary>
    private static readonly List<string> Disposals = new();
    private bool _disposed;

    public RepoBuilder()
    {
        // Полный GUID и номер процесса вместо восьми знаков: тридцати двух
        // бит уникальности недостаточно, когда цена совпадения — молча
        // испорченный репозиторий.
        Root = Path.Combine(Path.GetTempPath(),
            $"gitfs-fixture-{Environment.ProcessId}-{Guid.NewGuid():N}");
        lock (Taken)
        {
            if (!Taken.Add(Root))
                throw new InvalidOperationException(
                    $"two fixtures claimed the same directory: {Root}");
        }
        Directory.CreateDirectory(Root);
        Run("init", "-b", "main");
        Run("config", "gc.auto", "0");
        Run("config", "core.autocrlf", "false");
        // Рефлога у фикстуры быть не должно. Он нужен для интерактивной
        // работы, ни один тест его не читает, а `git repack -a -d` обходит
        // его при подсчёте достижимости — и на раннере CI это дало
        // «reflog of HEAD references pruned commits» с падением всего
        // прогона. Фикстура обязана зависеть только от того, что сама
        // создала.
        Run("config", "core.logAllRefUpdates", "false");
    }

    public string Run(params string[] args) =>
        System.Text.Encoding.UTF8.GetString(RunBytes(args));

    public byte[] RunBytes(params string[] args)
    {
        var psi = new ProcessStartInfo("git")
        {
            WorkingDirectory = Root,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var (k, v) in Env) psi.Environment[k] = v;
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var p = Process.Start(psi)!;
        using var ms = new MemoryStream();
        p.StandardOutput.BaseStream.CopyTo(ms);
        var err = p.StandardError.ReadToEnd();
        p.WaitForExit();
        if (p.ExitCode != 0)
        {
            var stdout = System.Text.Encoding.UTF8.GetString(ms.ToArray());
            throw new InvalidOperationException(
                $"git {string.Join(' ', args)} failed ({p.ExitCode}): {err} {stdout}".Trim()
                + Diagnose());
        }
        return ms.ToArray();
    }

    /// <summary>Состояние фикстуры на момент отказа git. Нужно потому, что
    /// на раннере CI появились падения, которые не воспроизводятся ни на
    /// одной доступной машине: git не мог прочитать объект, который сам же
    /// только что записал. Догадки тут бесполезны — пусть следующий отказ
    /// придёт с фактами: цел ли каталог, сколько объектов на диске, и что
    /// об этом думает сам git.</summary>
    private string Diagnose()
    {
        var sb = new System.Text.StringBuilder();
        sb.Append("\n  --- fixture state ---");
        sb.Append("\n  root: ").Append(Root)
          .Append(Directory.Exists(Root) ? " (exists)" : " (GONE)");
        try
        {
            var objects = Path.Combine(GitDir, "objects");
            if (Directory.Exists(objects))
            {
                var loose = Directory.EnumerateFiles(objects, "*", SearchOption.AllDirectories).Count();
                sb.Append("\n  objects on disk: ").Append(loose);
            }
            else sb.Append("\n  objects/: MISSING");

            var drive = new DriveInfo(Path.GetPathRoot(Root) ?? "/");
            sb.Append("\n  free space: ").Append(drive.AvailableFreeSpace >> 20).Append(" MB");
            sb.Append("\n  temp root: ").Append(Path.GetTempPath());
            sb.Append("\n  sibling fixtures: ").Append(
                Directory.EnumerateDirectories(Path.GetTempPath(), "gitfs-fixture-*").Count());
            lock (Taken) sb.Append("\n  fixtures created by this process: ").Append(Taken.Count);
            sb.Append("\n  this fixture disposed already: ").Append(_disposed);
            lock (Disposals)
            {
                sb.Append("\n  recent disposals (").Append(Disposals.Count).Append(" total):");
                foreach (var d in Disposals.TakeLast(5)) sb.Append("\n    ").Append(d);
            }
            // Что именно исчезло: git спотыкается об отсутствующий каталог,
            // значит важно, какие из них ещё на месте
            foreach (var probe in new[] { ".git", ".git/objects", ".git/objects/pack", ".git/refs" })
                sb.Append("\n  ").Append(probe).Append(": ")
                  .Append(Directory.Exists(Path.Combine(Root, probe)) ? "ok" : "MISSING");
            // Loose против packed: если объекты не потеряны, а упакованы,
            // значит их кто-то паковал — а паковать в фикстуре должен
            // только явный Repack
            sb.Append("\n  count-objects: ").Append(Quiet("count-objects", "-v"));
            sb.Append("\n  packs: ").Append(string.Join(" ",
                Directory.Exists(Path.Combine(GitDir, "objects", "pack"))
                    ? Directory.EnumerateFiles(Path.Combine(GitDir, "objects", "pack"))
                        .Select(Path.GetFileName)
                    : ["(no pack dir)"]));
            sb.Append("\n  git: ").Append(Quiet("--version"));
            sb.Append("\n  fsck: ").Append(Quiet("fsck", "--connectivity-only"));
        }
        catch (Exception e) { sb.Append("\n  (diagnosis failed: ").Append(e.Message).Append(')'); }
        return sb.ToString();
    }

    /// <summary>git без броска исключения — для диагностики, где отказ
    /// самой диагностики не должен подменять исходную ошибку.</summary>
    private string Quiet(params string[] args)
    {
        try
        {
            var psi = new ProcessStartInfo("git")
            {
                WorkingDirectory = Root,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            foreach (var (k, v) in Env) psi.Environment[k] = v;
            foreach (var a in args) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi)!;
            var output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
            p.WaitForExit();
            return $"exit {p.ExitCode} {output.Replace('\n', ' ').Trim()}";
        }
        catch (Exception e) { return "could not run: " + e.Message; }
    }

    public void WriteFile(string relativePath, string content)
    {
        var full = Path.Combine(Root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
    }

    public string CommitAll(string message)
    {
        Run("add", "-A");
        Run("commit", "-m", message);
        return Run("rev-parse", "HEAD").Trim();
    }

    public void Branch(string name) => Run("branch", name);

    public void Checkout(string name) => Run("checkout", "-q", name);

    /// <summary>Merge-коммит с двумя родителями (first-parent — текущая ветка).</summary>
    public string Merge(string branch)
    {
        Run("merge", "--no-ff", "-m", $"merge {branch}", branch);
        return Run("rev-parse", "HEAD").Trim();
    }

    /// <summary>Запуск с переопределением переменных окружения — нужен, чтобы
    /// делать коммиты с разными датами и таймзонами.</summary>
    public string RunWithEnv((string Key, string Value)[] overrides, params string[] args)
    {
        var psi = new ProcessStartInfo("git")
        {
            WorkingDirectory = Root,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var (k, v) in Env) psi.Environment[k] = v;
        foreach (var (k, v) in overrides) psi.Environment[k] = v;
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var p = Process.Start(psi)!;
        var stdout = p.StandardOutput.ReadToEnd();
        var err = p.StandardError.ReadToEnd();
        p.WaitForExit();
        if (p.ExitCode != 0)
            throw new InvalidOperationException($"git {string.Join(' ', args)} failed ({p.ExitCode}): {err} {stdout}".Trim());
        return stdout;
    }

    public string RunWithInput(byte[] input, params string[] args)
    {
        var psi = new ProcessStartInfo("git")
        {
            WorkingDirectory = Root,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var (k, v) in Env) psi.Environment[k] = v;
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        p.StandardInput.BaseStream.Write(input);
        p.StandardInput.Close();
        var stdout = p.StandardOutput.ReadToEnd();
        var err = p.StandardError.ReadToEnd();
        p.WaitForExit();
        if (p.ExitCode != 0)
            throw new InvalidOperationException($"git {string.Join(' ', args)} failed ({p.ExitCode}): {err}");
        return stdout;
    }

    public void Tag(string name, bool annotated = false, string? message = null)
    {
        if (annotated) Run("tag", "-a", name, "-m", message ?? name);
        else Run("tag", name);
    }

    /// <summary>Упаковывает все объекты в один pack (loose исчезают).</summary>
    public void Repack() => Run("repack", "-a", "-d");

    public string[] IndexFiles() =>
        Directory.GetFiles(Path.Combine(GitDir, "objects", "pack"), "*.idx");

    public sealed record PackEntry(string Sha, string Type, long Size, long Offset, int Depth);

    /// <summary>Разбор `git verify-pack -v`: эталон смещений и глубин дельт.</summary>
    public IReadOnlyList<PackEntry> VerifyPack(string idxPath)
    {
        var entries = new List<PackEntry>();
        foreach (var line in Run("verify-pack", "-v", idxPath).Split('\n'))
        {
            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 5 || parts[0].Length != 40) continue;
            if (parts[1] is not ("commit" or "tree" or "blob" or "tag")) continue;
            entries.Add(new PackEntry(parts[0], parts[1], long.Parse(parts[2]),
                long.Parse(parts[4]), parts.Length >= 7 ? int.Parse(parts[5]) : 0));
        }
        return entries;
    }

    /// <summary>Все объекты репозитория: sha + тип, эталон — сам git.</summary>
    public IEnumerable<(string Sha, string Type)> AllObjects()
    {
        var shas = Run("rev-list", "--objects", "--all")
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Split(' ')[0])
            .Distinct();
        foreach (var sha in shas)
            yield return (sha, Run("cat-file", "-t", sha).Trim());
    }

    public void Dispose()
    {
        lock (Disposals)
        {
            Disposals.Add($"{DateTime.UtcNow:HH:mm:ss.fff} {Root}");
            _disposed = true;
        }
        // git помечает loose-объекты read-only — Windows не даст удалить без снятия атрибута
        try
        {
            foreach (var f in Directory.EnumerateFiles(Root, "*", SearchOption.AllDirectories))
                File.SetAttributes(f, FileAttributes.Normal);
            Directory.Delete(Root, recursive: true);
        }
        catch (IOException) { /* фикстура во временной папке — ОС приберёт */ }
        catch (UnauthorizedAccessException) { }
    }
}
