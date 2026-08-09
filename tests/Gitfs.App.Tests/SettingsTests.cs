using Gitfs.App;

namespace Gitfs.App.Tests;

/// <summary>Выбор темы. Бриф §7 требует обе темы, и по умолчанию окно должно
/// идти за системой: тема, не совпадающая с остальным рабочим столом,
/// читается как поломка, а не как выбор.</summary>
public class SettingsTests : IDisposable
{
    private readonly string _original = Settings.Path;
    private readonly string _dir = Path.Combine(Path.GetTempPath(),
        "gitfs-settings-" + Guid.NewGuid().ToString("N"));

    public SettingsTests()
    {
        Directory.CreateDirectory(_dir);
        Settings.Path = Path.Combine(_dir, "settings.txt");
        Settings.Forget();
    }

    public void Dispose()
    {
        Settings.Path = _original;
        Settings.Forget();
        try { Directory.Delete(_dir, recursive: true); } catch (Exception) { }
    }

    [Fact]
    public void Without_a_file_the_app_follows_the_system()
    {
        Assert.Equal(ThemeChoice.Auto, Settings.Theme);
    }

    [Fact]
    public void The_choice_survives_a_restart()
    {
        Settings.Theme = ThemeChoice.Light;
        Settings.Forget();                       // как будто запустились заново
        Assert.Equal(ThemeChoice.Light, Settings.Theme);

        Settings.Theme = ThemeChoice.Dark;
        Settings.Forget();
        Assert.Equal(ThemeChoice.Dark, Settings.Theme);
    }

    [Fact]
    public void The_file_is_readable_by_a_human()
    {
        // его можно открыть и поправить руками — для одной настройки это
        // ценнее формата, который читается только программой
        Settings.Theme = ThemeChoice.Light;
        var text = File.ReadAllText(Settings.Path);
        Assert.Contains("theme", text);
        Assert.Contains("light", text);
    }

    /// <summary>Две разные поломки, и обе обязаны кончаться «как в системе»:
    /// файл без строки темы вовсе и строка с непонятным значением. Первая
    /// версия проверяла только первую — а она до разбора значения даже не
    /// доходит, и порча самого разбора её не роняла.</summary>
    [Theory]
    [InlineData("this is not a settings file\n???\n")]
    [InlineData("theme = chartreuse\n")]
    [InlineData("theme =\n")]
    public void A_damaged_file_falls_back_to_following_the_system(string content)
    {
        File.WriteAllText(Settings.Path, content);
        Settings.Forget();
        Assert.Equal(ThemeChoice.Auto, Settings.Theme);
    }

    [Fact]
    public void An_unwritable_location_does_not_break_the_app()
    {
        var blocked = Path.Combine(_dir, "blocked");
        File.WriteAllText(blocked, "not a directory");
        Settings.Path = Path.Combine(blocked, "settings.txt");
        Settings.Forget();

        Settings.Theme = ThemeChoice.Dark;       // не бросает
        Assert.Equal(ThemeChoice.Dark, Settings.Theme);   // в памяти всё равно учтено
    }

    /// <summary>КАЖДЫЙ выбор доживает до перезапуска. Раньше проверялись два
    /// из трёх, и «как в системе» среди них не было — а это единственное
    /// значение, которое совпадает с запасным при любой ошибке чтения, и
    /// потерю записи такой проверкой не отличить от успеха.</summary>
    [Theory]
    [InlineData(ThemeChoice.Auto)]
    [InlineData(ThemeChoice.Light)]
    [InlineData(ThemeChoice.Dark)]
    public void Every_choice_survives_a_restart(ThemeChoice choice)
    {
        // сперва уводим файл в заведомо другое состояние, чтобы «как было»
        // не выдавало себя за «записалось»
        Settings.Theme = choice == ThemeChoice.Dark ? ThemeChoice.Light : ThemeChoice.Dark;
        Settings.Theme = choice;
        Settings.Forget();
        Assert.Equal(choice, Settings.Theme);
    }
}
