using Gitfs.App;
using Gitfs.Diagnostics;

namespace Gitfs.App.Tests;

/// <summary>Экран первого запуска (макет 05). Проверяется решение «показывать
/// или нет» и отметка о том, что его уже видели: рисование окна закрыто
/// снимком под Xvfb, а вот условие показа — обычная логика, и именно в ней
/// живёт приветствие, которое нельзя пройти.</summary>
public class FirstRunTests : IDisposable
{
    private readonly string _marker = Path.Combine(Path.GetTempPath(),
        "gitfs-firstrun-" + Guid.NewGuid().ToString("N"), "done");
    private readonly string _original = FirstRunMarker.Path;

    public FirstRunTests() => FirstRunMarker.Path = _marker;

    public void Dispose()
    {
        FirstRunMarker.Path = _original;
        try { Directory.Delete(Path.GetDirectoryName(_marker)!, recursive: true); }
        catch (Exception) { }
    }

    private static Check[] Healthy() => new[]
    {
        new Check(CheckStatus.Ok, "winfsp", "2.1"),
        new Check(CheckStatus.Ok, "git", "2.46"),
    };

    [Fact]
    public void The_very_first_launch_shows_the_screen()
    {
        Assert.True(FirstRunWindow.ShouldShow(Healthy()));
    }

    /// <summary>Второй запуск при здоровой среде проходит мимо. Приветствие,
    /// которое нельзя пройти навсегда, — это препятствие, а не помощь.</summary>
    [Fact]
    public void Once_it_has_been_seen_a_healthy_environment_goes_straight_in()
    {
        FirstRunMarker.Remember();
        Assert.True(FirstRunMarker.Seen());
        Assert.False(FirstRunWindow.ShouldShow(Healthy()));
    }

    /// <summary>Но отказ возвращает экран в любой момент: том всё равно не
    /// создастся, и молча показать пустой менеджер — значит заставить
    /// человека выяснять причину самому.</summary>
    [Fact]
    public void A_blocking_failure_brings_the_screen_back_even_later()
    {
        FirstRunMarker.Remember();
        var broken = new[]
        {
            new Check(CheckStatus.Ok, "git", "2.46"),
            new Check(CheckStatus.Fail, "winfsp", "not installed"),
        };
        Assert.True(FirstRunWindow.ShouldShow(broken));
    }

    [Fact]
    public void A_warning_alone_does_not_bring_the_screen_back()
    {
        // предупреждение — не отказ: том создаётся, и перекрывать работу
        // приветствием из-за него нельзя
        FirstRunMarker.Remember();
        var warned = new[]
        {
            new Check(CheckStatus.Warn, "git", "not found"),
            new Check(CheckStatus.Ok, "winfsp", "2.1"),
        };
        Assert.False(FirstRunWindow.ShouldShow(warned));
    }

    [Fact]
    public void The_marker_survives_a_restart()
    {
        Assert.False(FirstRunMarker.Seen());
        FirstRunMarker.Remember();
        Assert.True(File.Exists(_marker));
        Assert.True(FirstRunMarker.Seen());
    }

    [Fact]
    public void A_marker_that_cannot_be_written_does_not_break_anything()
    {
        // каталог занят файлом с тем же именем — записать нельзя
        var blocked = Path.Combine(Path.GetTempPath(), "gitfs-firstrun-blocked-" + Guid.NewGuid());
        File.WriteAllText(blocked, "not a directory");
        FirstRunMarker.Path = Path.Combine(blocked, "done");
        try
        {
            FirstRunMarker.Remember();          // не бросает
            Assert.False(FirstRunMarker.Seen()); // и честно говорит, что не записалось
        }
        finally
        {
            FirstRunMarker.Path = _marker;
            File.Delete(blocked);
        }
    }
}
