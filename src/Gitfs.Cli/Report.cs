using System.Text;

namespace Gitfs.Cli;

/// <summary>Отрисовка отчёта doctor по макету дизайн-отдела.
/// ANSI: ok — green 2, warn — yellow 3, fail — red 1. Больше цветов нет.
/// NO_COLOR и пайп: статус остаётся словом в первой колонке, ширины не меняются.</summary>
public static class Report
{
    private const int NameColumn = 22;

    public static bool ColorEnabled { get; set; } = DetectColor();

    private static bool DetectColor()
    {
        if (Environment.GetEnvironmentVariable("NO_COLOR") is not null) return false;
        if (Console.IsOutputRedirected) return false;
        return true;
    }

    private static string Paint(string text, CheckStatus status)
    {
        if (!ColorEnabled) return text;
        var code = status switch
        {
            CheckStatus.Ok => "32",
            CheckStatus.Warn => "33",
            _ => "31",
        };
        return $"[{code}m{text}[0m";
    }

    private static string Word(CheckStatus s) => s switch
    {
        CheckStatus.Ok => "ok  ",
        CheckStatus.Warn => "warn",
        _ => "fail",
    };

    public static string Render(IReadOnlyList<Check> checks)
    {
        var sb = new StringBuilder();
        foreach (var c in checks)
        {
            sb.Append(Paint(Word(c.Status), c.Status)).Append(' ');
            sb.Append(c.Name.PadRight(NameColumn)).Append(' ');
            sb.AppendLine(c.Value);
            if (c.Fix is not null) sb.Append("     → ").AppendLine(c.Fix);
            if (c.Link is not null) sb.Append("       ").AppendLine(c.Link);
        }
        var ok = checks.Count(c => c.Status == CheckStatus.Ok);
        var warn = checks.Count(c => c.Status == CheckStatus.Warn);
        var fail = checks.Count(c => c.Status == CheckStatus.Fail);
        sb.AppendLine();
        sb.AppendLine($"{ok} ok · {warn} warning{(warn == 1 ? "" : "s")} · {fail} failure{(fail == 1 ? "" : "s")}");
        return sb.ToString();
    }

    /// <summary>Код возврата: 1 при любом fail — чтобы doctor годился для CI.</summary>
    public static int ExitCode(IReadOnlyList<Check> checks) =>
        checks.Any(c => c.Status == CheckStatus.Fail) ? 1 : 0;
}
