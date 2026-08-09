using System.Text.RegularExpressions;

namespace Gitfs.App.Tests;

/// <summary>Умолчания диалога монтирования против макета
/// (docs/design/ui/index.html и state.adv в ui.js).
///
/// Умолчание — это решение, принятое ЗА пользователя: подавляющее
/// большинство никогда не откроет «Advanced». Расхождение с макетом здесь
/// не косметика, а другой продукт: 96 МБ вместо 256 — вчетверо больше
/// переоткрытий пакетов на большом репозитории.
///
/// Проверяется РАЗМЕТКА, а не объект: Avalonia в этих тестах не поднимается
/// (нет headless-платформы), и значение по умолчанию живёт именно в .axaml.
/// Тест грубоват, зато падает ровно тогда, когда кто-то трогает умолчание.</summary>
public class DialogDefaultsTests
{
    private static string Markup()
    {
        var dir = AppContext.BaseDirectory;
        for (var i = 0; i < 8 && dir is not null; i++)
        {
            var candidate = Path.Combine(dir, "src", "Gitfs.App", "MountDialog.axaml");
            if (File.Exists(candidate)) return File.ReadAllText(candidate);
            dir = Path.GetDirectoryName(dir);
        }
        throw new FileNotFoundException("MountDialog.axaml not found above " + AppContext.BaseDirectory);
    }

    /// <summary>Элемент по x:Name целиком, до закрывающей угловой скобки
    /// открывающего тега — вместе со всеми атрибутами.</summary>
    private static string Element(string name)
    {
        var markup = Markup();
        var match = Regex.Match(markup, @"<[A-Za-z]+[^>]*x:Name=""" + name + @"""[^>]*>");
        Assert.True(match.Success, $"{name} исчез из разметки диалога");
        return match.Value;
    }

    private static bool Checked(string name) => Element(name).Contains(@"IsChecked=""True""");

    [Fact]
    public void The_cache_starts_at_the_budget_the_brief_names()
    {
        // бриф §4.3: «кэш (256 МБ)»; ui.js: cacheMb: 256
        Assert.Contains(@"Text=""256""", Element("CacheBox"));
        Assert.Equal(256, Gitfs.Vfs.MountOptions.Default.CacheMegabytes);
    }

    [Fact]
    public void The_limits_and_the_blob_ceiling_match_the_mockup()
    {
        Assert.Contains(@"Text=""200""", Element("CommitLimitBox"));
        Assert.Contains(@"Text=""500""", Element("HistoryLimitBox"));
        Assert.Contains(@"Text=""8""", Element("MaxBlobBox"));
    }

    /// <summary>Четыре вьюхи из пяти. dates/ в макете снята: она показывает ТЕ
    /// ЖЕ коммиты, что commits/, только разложенные по дням, и включённая по
    /// умолчанию удваивает дерево, ничего не добавляя.</summary>
    [Fact]
    public void Dates_is_the_one_view_that_starts_unchecked()
    {
        Assert.True(Checked("ViewBranches"));
        Assert.True(Checked("ViewTags"));
        Assert.True(Checked("ViewCommits"));
        Assert.False(Checked("ViewDates"));
        Assert.True(Checked("ViewHistory"));
    }

    /// <summary>Том по умолчанию только на чтение (макет, index.html:246).
    /// Запись сюда не доходит до репозитория — она уходит в песочницу, которая
    /// исчезнет вместе с томом; человек, уверенный в обратном, теряет работу
    /// молча.</summary>
    [Fact]
    public void The_volume_starts_read_only()
    {
        Assert.True(Checked("ReadOnlyBox"));
    }

    /// <summary>А здесь мы от макета ОТСТУПАЕМ, и тест закрепляет отступление,
    /// чтобы оно не выглядело недоделкой. В макете галка стоит; но бриф §4.2
    /// требует показывать осиротевшую песочницу как деградацию — и продукт,
    /// который создаёт её сам при каждом размонтировании, жаловался бы на
    /// себя. Умолчание — убирать за собой.</summary>
    [Fact]
    public void Keeping_the_overlay_is_a_deliberate_deviation_from_the_mockup()
    {
        Assert.False(Checked("KeepOverlayBox"));
    }

    /// <summary>Подпись у read-only объясняет, КОГДА снимать галку. Пока она
    /// была выключена, текст доказывал обратное («Word и Excel пишут рядом») —
    /// довод против того состояния, в котором чекбокс теперь стоит.</summary>
    [Fact]
    public void The_read_only_hint_tells_you_when_to_turn_it_off()
    {
        var markup = Markup();
        Assert.Contains("uncheck for Word and Excel", markup);
    }
}
