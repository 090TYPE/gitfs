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

/// <summary>Настройки приложения: тема и движение. Обе доходят до экрана
/// «Настройки» (бриф §4.2) и обе действительно что-то меняют — настройка,
/// которая ничего не делает, хуже отсутствующей.
///
/// Хранится строками «ключ = значение» в обычном файле рядом с журналом: его
/// можно открыть, прочитать и поправить руками. Ошибки чтения и записи не
/// выходят наружу — приложение, которое не запустилось из-за настроек,
/// потеряло больше, чем эти настройки стоят.
///
/// Файл перечитывается целиком и переписывается целиком: настроек две, и
/// разбирать частичные обновления было бы дороже, чем сам файл.</summary>
public static class Settings
{
    public static string Path { get; set; } = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "gitfs", "settings.txt");

    private static ThemeChoice? _theme;
    private static bool? _reduceMotion;

    public static ThemeChoice Theme
    {
        get
        {
            if (_theme is { } value) return value;
            Load();
            return _theme!.Value;
        }
        set
        {
            _theme = value;
            Save();
            Apply(value);
        }
    }

    /// <summary>Не двигать то, что можно не двигать. Анимация у нас ровно
    /// одна — пульс у больного тома, — но выключатель ей нужен: движение на
    /// краю зрения мешает читать, а кому-то от него физически плохо. Система
    /// про своё «reduce motion» кроссплатформенно не рассказывает, поэтому
    /// спрашиваем человека, а не угадываем.</summary>
    public static bool ReduceMotion
    {
        get
        {
            if (_reduceMotion is { } value) return value;
            Load();
            return _reduceMotion!.Value;
        }
        set
        {
            _reduceMotion = value;
            Save();
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

    private static void Load()
    {
        // Умолчания ставятся ДО чтения: файла может не быть, он может быть
        // повреждён, и в обоих случаях значения обязаны оказаться заданными,
        // иначе следующее обращение снова полезет в файл.
        _theme ??= ThemeChoice.Auto;
        _reduceMotion ??= false;

        try
        {
            if (!File.Exists(Path)) return;
            foreach (var line in File.ReadLines(Path))
            {
                var parts = line.Split('=', 2);
                if (parts.Length != 2) continue;
                var value = parts[1].Trim().ToLowerInvariant();
                switch (parts[0].Trim())
                {
                    case "theme":
                        _theme = value switch
                        {
                            "light" => ThemeChoice.Light,
                            "dark" => ThemeChoice.Dark,
                            _ => ThemeChoice.Auto,
                        };
                        break;
                    case "reduce-motion":
                        _reduceMotion = value is "yes" or "true" or "1" or "on";
                        break;
                }
            }
        }
        catch (Exception e) { Program.Log("settings-read", e); }
    }

    private static void Save()
    {
        try
        {
            var dir = System.IO.Path.GetDirectoryName(Path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(Path, string.Join(Environment.NewLine, new[]
            {
                "# gitfs settings",
                "theme = " + Token(_theme ?? ThemeChoice.Auto),
                "reduce-motion = " + (_reduceMotion == true ? "yes" : "no"),
                "",
            }));
        }
        catch (Exception e) { Program.Log("settings-write", e); }
    }

    /// <summary>Только для тестов: сбрасывает запомненное, чтобы следующее
    /// обращение перечитало файл.</summary>
    internal static void Forget()
    {
        _theme = null;
        _reduceMotion = null;
    }
}
