using System.Text;

namespace Gitfs.Diagnostics;

/// <summary>Отрисовка отчёта doctor по макету дизайн-отдела.
/// ANSI: ok — green 2, warn — yellow 3, fail — red 1. Больше цветов нет.
/// NO_COLOR и пайп: статус остаётся словом в первой колонке, ширины не меняются.</summary>
public static class Report
{
    private const int NameColumn = 22;

    /// <summary>Совместимость: решение о цвете теперь общее для всего CLI и
    /// живёт в <see cref="Ansi"/>. Здесь остаётся переключатель, потому что
    /// им пользуются тесты.</summary>
    public static bool ColorEnabled
    {
        get => Ansi.Enabled;
        set { Ansi.Enabled = value; Ansi.EnabledForError = value; }
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
            sb.Append(Ansi.For(c.Status, Word(c.Status))).Append(' ');
            sb.Append(c.Name.PadRight(NameColumn)).Append(' ');
            sb.AppendLine(c.Value);
            // Пояснение приглушено: это подсказка к главному, а не главное.
            if (c.Fix is not null) sb.AppendLine(Ansi.Dim("     → " + c.Fix));
            if (c.Link is not null) sb.AppendLine(Ansi.Dim("       " + c.Link));
        }
        var ok = checks.Count(c => c.Status == CheckStatus.Ok);
        var warn = checks.Count(c => c.Status == CheckStatus.Warn);
        var fail = checks.Count(c => c.Status == CheckStatus.Fail);
        sb.AppendLine();
        sb.AppendLine(Ansi.Dim(
            $"{ok} ok · {warn} warning{(warn == 1 ? "" : "s")} · {fail} failure{(fail == 1 ? "" : "s")}"));
        return sb.ToString();
    }

    /// <summary>Код возврата: 1 при любом fail — чтобы doctor годился для CI.</summary>
    public static int ExitCode(IReadOnlyList<Check> checks) =>
        checks.Any(c => c.Status == CheckStatus.Fail) ? 1 : 0;
}
