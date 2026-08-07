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

    public RepoBuilder()
    {
        Root = Path.Combine(Path.GetTempPath(), "gitfs-fixture-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(Root);
        Run("init", "-b", "main");
        Run("config", "gc.auto", "0");
        Run("config", "core.autocrlf", "false");
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
                $"git {string.Join(' ', args)} failed ({p.ExitCode}): {err} {stdout}".Trim());
        }
        return ms.ToArray();
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

    public void Tag(string name, bool annotated = false, string? message = null)
    {
        if (annotated) Run("tag", "-a", name, "-m", message ?? name);
        else Run("tag", name);
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
