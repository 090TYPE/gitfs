using System.Text.RegularExpressions;

namespace Gitfs.App.Tests;

/// <summary>Один цвет — одно написание.
///
/// Акцент жил в трёх местах и в двух разных значениях: токен темы #9184d9,
/// литерал растеризатора #9184D9 и значок трея (0x8B,0x80,0xDE) — цвет,
/// которого нет в рампе макета вообще. Заметить это глазом нельзя: значок в
/// трее 16 px, иконки вьюх — в Проводнике, окно — на экране; рядом они не
/// оказываются никогда.
///
/// Код теперь ходит через Brand, но разметка темы — отдельный файл, и
/// связать их может только проверка. Она читает Theme.axaml как данные:
/// поднять Avalonia в этих тестах нечем (headless-платформы нет).</summary>
public class ThemeTokensTests
{
    private static string ThemeMarkup()
    {
        var dir = AppContext.BaseDirectory;
        for (var i = 0; i < 8 && dir is not null; i++)
        {
            var candidate = Path.Combine(dir, "src", "Gitfs.App", "Theme.axaml");
            if (File.Exists(candidate)) return File.ReadAllText(candidate);
            dir = Path.GetDirectoryName(dir);
        }
        throw new FileNotFoundException("Theme.axaml not found above " + AppContext.BaseDirectory);
    }

    /// <summary>Цвет роли из словаря нужной темы. Одно и то же имя лежит в
    /// обоих словарях с РАЗНЫМИ значениями, поэтому искать по всему файлу
    /// нельзя — вернётся тот, что выше.</summary>
    private static string Token(string variant, string key)
    {
        var markup = ThemeMarkup();
        var dict = Regex.Match(markup,
            "<ResourceDictionary x:Key=\"" + variant + "\">(.*?)</ResourceDictionary>",
            RegexOptions.Singleline);
        Assert.True(dict.Success, $"словарь темы {variant} исчез из Theme.axaml");

        var token = Regex.Match(dict.Groups[1].Value,
            "<Color x:Key=\"" + key + "\">(#[0-9a-fA-F]{6})</Color>");
        Assert.True(token.Success, $"{key} исчез из словаря {variant}");
        return token.Groups[1].Value.ToLowerInvariant();
    }

    public static IEnumerable<object[]> DarkRoles => new[]
    {
        new object[] { "GfsAccent", Brand.AccentHex },
        new object[] { "GfsAccentInk", Brand.AccentInkHex },
        new object[] { "GfsText", Brand.TextHex },
        new object[] { "GfsTextMuted", Brand.TextMutedHex },
        new object[] { "GfsTextFaint", Brand.TextFaintHex },
        new object[] { "GfsStrike", Brand.StrikeHex },
        new object[] { "GfsOk", Brand.OkHex },
        new object[] { "GfsWarn", Brand.WarnHex },
        new object[] { "GfsErr", Brand.ErrHex },
    };

    [Theory]
    [MemberData(nameof(DarkRoles))]
    public void The_code_and_the_theme_spell_each_role_the_same(string key, string inCode)
    {
        Assert.Equal(inCode.ToLowerInvariant(), Token("Dark", key));
    }

    /// <summary>Байты значка трея считаются из того же hex, а не пишутся
    /// рядом руками — ровно этим способом акцент и разъехался.</summary>
    [Fact]
    public void The_raster_bytes_are_the_same_colour_as_the_hex()
    {
        Assert.Equal((0x91, 0x84, 0xd9), Brand.Accent);
        Assert.Equal(Brand.Bytes(Brand.WarnHex), Brand.Warn);
        Assert.Equal(Brand.Bytes(Brand.ErrHex), Brand.Err);
    }

    /// <summary>Литеральных цветов вне Brand в коде приложения быть не должно:
    /// каждый такой — будущее расхождение с темой.</summary>
    [Fact]
    public void No_source_file_but_Brand_spells_a_colour_by_hand()
    {
        var dir = AppContext.BaseDirectory;
        string? root = null;
        for (var i = 0; i < 8 && dir is not null; i++)
        {
            if (Directory.Exists(Path.Combine(dir, "src", "Gitfs.App"))) { root = Path.Combine(dir, "src", "Gitfs.App"); break; }
            dir = Path.GetDirectoryName(dir);
        }
        Assert.NotNull(root);

        var offenders = new List<string>();
        foreach (var file in Directory.EnumerateFiles(root!, "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                || file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
                || Path.GetFileName(file) == "Brand.cs") continue;

            foreach (var (line, number) in File.ReadLines(file).Select((l, n) => (l, n + 1)))
            {
                if (line.TrimStart().StartsWith("//") || line.TrimStart().StartsWith("///")) continue;
                if (Regex.IsMatch(line, "\"#[0-9a-fA-F]{6}\""))
                    offenders.Add($"{Path.GetFileName(file)}:{number}  {line.Trim()}");
            }
        }

        Assert.True(offenders.Count == 0,
            "цвет написан мимо Brand:\n" + string.Join("\n", offenders));
    }
}
