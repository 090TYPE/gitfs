using Gitfs.App;
using Gitfs.Diagnostics;

namespace Gitfs.App.Tests;

/// <summary>Состояние тома и то, как оно доходит до значка в трее.
///
/// Главное здесь — ОТКУДА берётся деградация. Бриф §4.1 называет ею
/// переоткрытие пакетов и осиротевшую песочницу, то есть состояние САМОГО
/// ТОМА. Раньше треугольник зажигало любое предупреждение doctor — например
/// «диск почти полон», к томам отношения не имеющее. Значок, загорающийся не
/// по делу, перестают замечать, и он молчит тогда, когда нужен.</summary>
public class MountHealthTests
{
    private static Check[] Ok() => new[] { new Check(CheckStatus.Ok, "git", "2.43") };
    private static Check[] Warned() => new[]
    {
        new Check(CheckStatus.Ok, "git", "2.43"),
        new Check(CheckStatus.Warn, "disk", "97% full"),
    };

    [Fact]
    public void A_healthy_volume_is_just_mounted()
    {
        Assert.Equal(TrayState.Mounted, TrayBadge.StateOf(2, Ok(), anyDegraded: false));
    }

    [Fact]
    public void A_volume_whose_packs_were_reopened_shows_degraded()
    {
        Assert.Equal(TrayState.Degraded, TrayBadge.StateOf(2, Ok(), anyDegraded: true));
    }

    /// <summary>Именно та подмена, ради которой всё и переписано: посторонний
    /// warn окружения НЕ обязан гасить нормальные тома в треугольник.</summary>
    [Fact]
    public void An_unrelated_environment_warning_does_not_degrade_healthy_volumes()
    {
        Assert.Equal(TrayState.Mounted, TrayBadge.StateOf(2, Warned(), anyDegraded: false));
    }

    [Fact]
    public void Without_any_volumes_an_environment_warning_still_shows()
    {
        // томов нет, значит деградировать нечему — но сказать о проблеме
        // окружения по-прежнему надо, иначе значок молчит вовсе
        Assert.Equal(TrayState.Degraded, TrayBadge.StateOf(0, Warned(), anyDegraded: false));
    }

    [Fact]
    public void A_driver_failure_outranks_a_degraded_volume()
    {
        var broken = new[] { new Check(CheckStatus.Fail, "winfsp", "not installed") };
        Assert.Equal(TrayState.Error, TrayBadge.StateOf(2, broken, anyDegraded: true));
    }

    // ---------- запись о монтировании ----------

    [Fact]
    public void A_fresh_entry_is_healthy_and_says_so_in_words()
    {
        var entry = new MountEntry("repo", "/src/repo", "G:", "b t c d h", DateTimeOffset.UtcNow);
        Assert.Equal(MountHealth.Ok, entry.Health);
        Assert.Equal("ok", entry.HealthWord);
        Assert.Equal(0, entry.Reopens);
    }

    /// <summary>Состояние читается СЛОВОМ, а не только цветом кружка: том,
    /// отличимый лишь по оттенку, молчит для тех, кто оттенок не различает.</summary>
    [Fact]
    public void A_degraded_entry_has_a_word_of_its_own()
    {
        var entry = new MountEntry("repo", "/src/repo", "G:", "b t c d h", DateTimeOffset.UtcNow)
        {
            Health = MountHealth.Reopening,
            Reopens = 3,
        };
        Assert.Equal("reopening", entry.HealthWord);
        Assert.NotEqual("ok", entry.HealthWord);
    }

    // ---------- журнал считает переоткрытия ----------

    [Fact]
    public void The_log_counts_pack_reopens_and_nothing_else()
    {
        var log = new Gitfs.Vfs.MountLog();
        Assert.Equal(0, log.Reopens);

        log.Add("mount", "started");
        log.Add("truncated", "history/f.txt stopped at 2");
        Assert.Equal(0, log.Reopens);   // это не переоткрытия

        log.Add("pack-reopened", "objects reopened");
        log.Add("pack-reopened", "objects reopened");
        Assert.Equal(2, log.Reopens);
    }
/// <summary>Индикатор состояния стоит у КАЖДОЙ строки списка томов
    /// (бриф §4.2). Раньше здоровый том не имел никакого, и пустое место
    /// означало разом «всё хорошо» и «строка не дорисовалась».
    ///
    /// Меню лотка — текст, цвета в нём нет: состояния обязаны отличаться
    /// формой знака.</summary>
    [Fact]
    public void Every_mount_in_the_tray_menu_carries_a_state_indicator()
    {
        var seen = new List<string>();
        foreach (var health in Enum.GetValues<MountHealth>())
        {
            var mark = App.Indicator(health);
            Assert.False(string.IsNullOrWhiteSpace(mark),
                $"{health} остался без индикатора");
            seen.Add(mark);
        }
        Assert.Equal(seen.Count, seen.Distinct().Count());
    }
}
