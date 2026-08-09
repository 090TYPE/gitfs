using Avalonia;
using Avalonia.Styling;

namespace Gitfs.App;

public enum ThemeChoice
{
    /// <summary>Как в системе. По умолчанию — потому что тема окна,
    /// не совпадающая с остальным рабочим столом, читается как поломка.</summary>
    Auto,
    Light,
    Dark,
}

/// <summary>Настройки приложения. Пока их ровно одна — тема, — но пункт
/// «Настройки» есть в меню трея по брифу §4.2, и заводить под него класс
/// сразу дешевле, чем прятать одну переменную в трёх местах.
///
/// Хранится строкой в обычном файле рядом с журналом: его можно открыть,
/// прочитать и поправить руками. Ошибки чтения и записи не выходят наружу —
/// приложение, которое не запустилось из-за настроек, потеряло больше, чем
/// эти настройки стоят.</summary>
public static class Settings
{
    public static string Path { get; set; } = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "gitfs", "settings.txt");

    private static ThemeChoice? _cached;

    public static ThemeChoice Theme
    {
        get
        {
            if (_cached is { } value) return value;
            _cached = Read();
            return _cached.Value;
        }
        set
        {
            _cached = value;
            Write(value);
            Apply(value);
        }
    }

    /// <summary>Ставит выбранную тему приложению. Auto — ThemeVariant.Default,
    /// то есть Avalonia сама следит за системой и переключает на лету.</summary>
    public static void Apply(ThemeChoice choice)
    {
        if (Application.Current is not { } app) return;
        app.RequestedThemeVariant = choice switch
        {
            ThemeChoice.Light => ThemeVariant.Light,
            ThemeChoice.Dark => ThemeVariant.Dark,
            _ => ThemeVariant.Default,
        };
    }

    /// <summary>Как тема записана в файле. Одно слово на выбор — файл
    /// открывают и правят руками, и «Follow the system» там читалось бы
    /// хуже, чем auto.</summary>
    private static string Token(ThemeChoice choice) => choice switch
    {
        ThemeChoice.Light => "light",
        ThemeChoice.Dark => "dark",
        _ => "auto",
    };

    private static ThemeChoice Read()
    {
        try
        {
            if (!File.Exists(Path)) return ThemeChoice.Auto;
            foreach (var line in File.ReadLines(Path))
            {
                var parts = line.Split('=', 2);
                if (parts.Length != 2 || parts[0].Trim() != "theme") continue;
                return parts[1].Trim().ToLowerInvariant() switch
                {
                    "light" => ThemeChoice.Light,
                    "dark" => ThemeChoice.Dark,
                    _ => ThemeChoice.Auto,
                };
            }
        }
        catch (Exception e) { Program.Log("settings-read", e); }
        return ThemeChoice.Auto;
    }

    private static void Write(ThemeChoice choice)
    {
        try
        {
            var dir = System.IO.Path.GetDirectoryName(Path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(Path,
                $"# gitfs settings{Environment.NewLine}theme = {Token(choice)}{Environment.NewLine}");
        }
        catch (Exception e) { Program.Log("settings-write", e); }
    }

    /// <summary>Только для тестов: сбрасывает запомненное значение, чтобы
    /// следующий вызов перечитал файл.</summary>
    internal static void Forget() => _cached = null;
}
