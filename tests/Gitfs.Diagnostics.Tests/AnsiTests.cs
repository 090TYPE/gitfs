using Gitfs.Diagnostics;

namespace Gitfs.Diagnostics.Tests;

/// <summary>Цвет в терминале. Бриф §3 задаёт правило один раз на всю
/// программу: восемь цветов, NO_COLOR выключает, пайп выключает, статус
/// остаётся СЛОВОМ в первой колонке — то есть отчёт читается и без цвета.</summary>
public class AnsiTests : IDisposable
{
    private readonly bool _out = Ansi.Enabled;
    private readonly bool _err = Ansi.EnabledForError;

    public void Dispose()
    {
        Ansi.Enabled = _out;
        Ansi.EnabledForError = _err;
    }

    private const char Esc = '';

    [Fact]
    public void With_colour_off_nothing_but_the_text_comes_out()
    {
        Ansi.Enabled = false;
        foreach (var painted in new[]
                 { Ansi.Ok("x"), Ansi.Warn("x"), Ansi.Err("x"), Ansi.Dim("x"), Ansi.Accent("x") })
        {
            Assert.Equal("x", painted);
            Assert.DoesNotContain(Esc, painted);
        }
    }

    [Fact]
    public void With_colour_on_every_role_wraps_and_closes()
    {
        Ansi.Enabled = true;
        foreach (var painted in new[]
                 { Ansi.Ok("x"), Ansi.Warn("x"), Ansi.Err("x"), Ansi.Dim("x"), Ansi.Accent("x") })
        {
            Assert.StartsWith(Esc + "[", painted);
            // Незакрытая последовательность красит ВЕСЬ остальной терминал —
            // включая приглашение оболочки после выхода программы.
            Assert.EndsWith(Esc + "[0m", painted);
            Assert.Contains("x", painted);
        }
    }

    [Fact]
    public void The_roles_are_different_colours()
    {
        Ansi.Enabled = true;
        var seen = new[] { Ansi.Ok("x"), Ansi.Warn("x"), Ansi.Err("x") };
        Assert.Equal(3, seen.Distinct().Count());
    }

    /// <summary>Решение про stdout и stderr — РАЗНОЕ. doctor при монтировании
    /// печатается в поток ошибок, и общий флаг означал бы либо
    /// escape-последовательности в файле при `2>log`, либо потерю цвета на
    /// живом терминале при `>log`.</summary>
    [Fact]
    public void The_two_streams_decide_separately()
    {
        Ansi.Enabled = false;
        Ansi.EnabledForError = true;

        Assert.Equal("x", Ansi.Ok("x"));                       // stdout молчит
        Assert.Contains(Esc, Ansi.ErrStream(CheckStatus.Ok, "x"));   // stderr красит
        Assert.Contains(Esc, Ansi.DimErr("x"));

        Ansi.EnabledForError = false;
        Assert.Equal("x", Ansi.ErrStream(CheckStatus.Ok, "x"));
        Assert.Equal("x", Ansi.DimErr("x"));
    }

    [Fact]
    public void The_status_word_survives_without_colour()
    {
        // «статус остаётся словом в первой колонке, ширины не меняются»
        Ansi.Enabled = false;
        var plain = Report.Render(new[]
        {
            new Check(CheckStatus.Ok, "git", "2.43"),
            new Check(CheckStatus.Fail, "winfsp", "not installed", "install it", "docs"),
        });
        Assert.DoesNotContain(Esc, plain);
        Assert.Contains("ok  ", plain);
        Assert.Contains("fail", plain);
        Assert.Contains("→ install it", plain);
    }

    /// <summary>Смотрим на КАЖДУЮ строку отдельно. Первая версия проверяла
    /// «где-то в отчёте есть [2m» — и не падала, когда приглушение снимали с
    /// совета: его давала строка ссылки, которая рядом.</summary>
    [Fact]
    public void The_report_paints_the_status_and_dims_the_advice()
    {
        Ansi.Enabled = true;
        var lines = Report.Render(new[]
        {
            new Check(CheckStatus.Fail, "winfsp", "not installed", "install it", "docs"),
        }).Split('\n');

        var status = lines.First(l => l.Contains("winfsp"));
        Assert.Contains(Esc + "[31m", status);          // отказ — красный

        var advice = lines.First(l => l.Contains("install it"));
        Assert.Contains(Esc + "[2m", advice);           // совет — приглушённый

        var link = lines.First(l => l.Contains("docs"));
        Assert.Contains(Esc + "[2m", link);
    }

    [Fact]
    public void Widths_do_not_move_when_colour_turns_on()
    {
        var checks = new[]
        {
            new Check(CheckStatus.Ok, "git", "2.43"),
            new Check(CheckStatus.Warn, "history", "shallow clone"),
        };
        Ansi.Enabled = false;
        var plain = Report.Render(checks);
        Ansi.Enabled = true;
        var coloured = Strip(Report.Render(checks));
        Assert.Equal(plain, coloured);
    }

    private static string Strip(string text)
    {
        var sb = new System.Text.StringBuilder(text.Length);
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] != Esc) { sb.Append(text[i]); continue; }
            while (i < text.Length && text[i] != 'm') i++;
        }
        return sb.ToString();
    }
}
