using System.Runtime.InteropServices;

namespace Gitfs.Diagnostics;

/// <summary>Цвет в терминале: одно место на весь CLI.
///
/// Раньше ANSI жил только в отчёте doctor, и весь остальной вывод — таблица
/// list, строка успеха mount, каждое «fail …» — печатался голым. Правило
/// брифа §3 одно на всю программу, и держать его в одном файле дешевле, чем
/// вспоминать про него в каждом Console.WriteLine.
///
/// Ролей ровно пять: ok, warn, err, dim (второстепенное — строки «что
/// сделать» и ссылки), accent (заголовки таблиц). Больше не заводить:
/// палитра терминала у всех разная, и шестой оттенок где-нибудь сольётся
/// с фоном.</summary>
public static class Ansi
{
    /// <summary>Красить ли stdout. Считается один раз при первом обращении.</summary>
    public static bool Enabled { get; set; } = Detect(forError: false);

    /// <summary>Красить ли stderr. ОТДЕЛЬНО от stdout: doctor при монтировании
    /// печатается в поток ошибок, и решение по stdout там просто не про то —
    /// `gitfs mount … 2>log` получал бы escape-последовательности в файле,
    /// а `gitfs mount … >log` терял бы цвет на живом терминале.</summary>
    public static bool EnabledForError { get; set; } = Detect(forError: true);

    private static bool Detect(bool forError)
    {
        // NO_COLOR — договорённость, а не наша выдумка: no-color.org
        if (Environment.GetEnvironmentVariable("NO_COLOR") is not null) return false;
        if (forError ? Console.IsErrorRedirected : Console.IsOutputRedirected) return false;
        if (OperatingSystem.IsWindows() && !TryEnableWindowsVirtualTerminal(forError)) return false;
        return true;
    }

    // ---------- роли ----------

    public static string Ok(string text) => Paint(text, "32");
    public static string Warn(string text) => Paint(text, "33");
    public static string Err(string text) => Paint(text, "31");
    public static string Accent(string text) => Paint(text, "36");
    /// <summary>Приглушённое: строка «что сделать», ссылка, итоговая сводка.
    /// Макет отдаёт им класс dim — они пояснение к главному, а не главное.</summary>
    public static string Dim(string text) => Paint(text, "2");

    public static string For(CheckStatus status, string text) => status switch
    {
        CheckStatus.Ok => Ok(text),
        CheckStatus.Warn => Warn(text),
        _ => Err(text),
    };

    private static string Paint(string text, string code) =>
        Enabled ? $"[{code}m{text}[0m" : text;

    /// <summary>То же, но для потока ошибок.</summary>
    public static string ErrStream(CheckStatus status, string text)
    {
        if (!EnabledForError) return text;
        var code = status switch
        {
            CheckStatus.Ok => "32",
            CheckStatus.Warn => "33",
            _ => "31",
        };
        return $"[{code}m{text}[0m";
    }

    public static string DimErr(string text) =>
        EnabledForError ? $"[2m{text}[0m" : text;

    // ---------- Windows ----------

    private const int StdOutputHandle = -11;
    private const int StdErrorHandle = -12;
    private const uint EnableVirtualTerminalProcessing = 0x0004;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GetStdHandle(int nStdHandle);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetConsoleMode(IntPtr hConsoleHandle, out uint lpMode);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetConsoleMode(IntPtr hConsoleHandle, uint dwMode);

    /// <summary>Классический conhost НЕ понимает escape-последовательности,
    /// пока их не включат явно. Без этого `gitfs doctor` в старом окне
    /// печатал «←[32mok←[0m» вместо зелёного — вывод, который читать хуже,
    /// чем бесцветный. Не вышло включить — честно возвращаем false и
    /// печатаем без цвета.</summary>
    private static bool TryEnableWindowsVirtualTerminal(bool forError)
    {
        try
        {
            var handle = GetStdHandle(forError ? StdErrorHandle : StdOutputHandle);
            if (handle == IntPtr.Zero || handle == new IntPtr(-1)) return false;
            if (!GetConsoleMode(handle, out var mode)) return false;
            if ((mode & EnableVirtualTerminalProcessing) != 0) return true;
            return SetConsoleMode(handle, mode | EnableVirtualTerminalProcessing);
        }
        catch (DllNotFoundException) { return false; }
        catch (EntryPointNotFoundException) { return false; }
    }
}
