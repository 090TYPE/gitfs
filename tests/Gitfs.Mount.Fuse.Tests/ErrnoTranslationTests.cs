using System.Reflection;
using Gitfs.Vfs;

namespace Gitfs.Mount.Fuse.Tests;

/// <summary>Перевод ошибок границы в коды POSIX. Проверяется без
/// монтирования — как и таблица NTSTATUS у адаптера WinFsp.
///
/// Цена ошибки здесь не абстрактная: неверный код меняет поведение
/// пользовательских программ. ENOENT вместо EACCES заставит установщик
/// молча создать файл заново, EIO вместо ENOTSUP — показать «диск сбоит».</summary>
public class ErrnoTranslationTests
{
    [Theory]
    [InlineData(GitfsError.None, 0)]
    [InlineData(GitfsError.NotFound, 2)]        // ENOENT
    [InlineData(GitfsError.NotADirectory, 20)]  // ENOTDIR
    [InlineData(GitfsError.AmbiguousPrefix, 22)] // EINVAL
    [InlineData(GitfsError.CorruptObject, 5)]   // EIO
    [InlineData(GitfsError.IoError, 5)]         // EIO
    [InlineData(GitfsError.AccessDenied, 13)]   // EACCES
    [InlineData(GitfsError.NotSupported, 95)]   // ENOTSUP
    [InlineData(GitfsError.TooLarge, 28)]       // ENOSPC
    [InlineData(GitfsError.IsADirectory, 39)]   // ENOTEMPTY — «rm -rf» покажет
                                                // это как «Directory not empty»
    public void Error_maps_to_the_posix_code(GitfsError error, int expected) =>
        Assert.Equal(expected, GitfsFuseFileSystem.Translate(error));

    [Fact]
    public void Every_error_of_the_boundary_is_compared_with_a_case_here()
    {
        // Прежняя версия проверяла `code > 0` — чему catch-all
        // `_ => Errno.IO` удовлетворяет ВСЕГДА, то есть заявленная цель
        // теста (заметить новый GitfsError без собственного перевода) была
        // ровно тем, чего он не мог обнаружить.
        //
        // Сверяем по ИМЕНАМ: каждое значение перечисления обязано иметь
        // свою строку в InlineData выше. Добавили ошибку — тест краснеет,
        // пока для неё не выбран код, и молчаливое превращение в EIO
        // становится невозможным.
        var asserted = typeof(ErrnoTranslationTests)
            .GetMethod(nameof(Error_maps_to_the_posix_code))!
            .GetCustomAttributes<Xunit.InlineDataAttribute>()
            .Select(a => (GitfsError)a.GetData(null!).First()[0]!)
            .ToHashSet();

        var missing = Enum.GetValues<GitfsError>().Where(e => !asserted.Contains(e)).ToList();
        Assert.True(missing.Count == 0,
            "these boundary errors have no case of their own and would silently become EIO: "
            + string.Join(", ", missing));
    }

    [Fact]
    public void No_two_distinct_errors_collapse_onto_the_same_code_by_accident()
    {
        // Совпадения бывают осмысленными — CorruptObject и IoError оба EIO,
        // потому что для ядра это одно и то же. Перечисляем их явно, чтобы
        // случайное схлопывание нового кода в чужой было видно.
        var expected = new[]
        {
            new[] { GitfsError.CorruptObject, GitfsError.IoError },
        };

        var byCode = Enum.GetValues<GitfsError>()
            .Where(e => e != GitfsError.None)
            .GroupBy(GitfsFuseFileSystem.Translate)
            .Where(g => g.Count() > 1)
            .Select(g => g.OrderBy(e => e.ToString()).ToArray())
            .ToList();

        foreach (var group in byCode)
        {
            var known = expected.Any(x => x.OrderBy(e => e.ToString()).SequenceEqual(group));
            Assert.True(known,
                "these share an errno without an explicit decision: " + string.Join(", ", group));
        }
    }

    [Fact]
    public void Ambiguous_prefix_is_not_reported_as_missing()
    {
        // разные ситуации: «такого коммита нет» и «под префикс подходит
        // несколько». Свести их к ENOENT — соврать пользователю
        Assert.NotEqual(GitfsFuseFileSystem.Translate(GitfsError.NotFound),
            GitfsFuseFileSystem.Translate(GitfsError.AmbiguousPrefix));
    }

    [Fact]
    public void Corrupt_object_is_an_io_error_not_a_missing_file()
    {
        // битый объект — это отказ носителя, а не отсутствие файла;
        // иначе инструменты «починят» историю, создав файл заново
        Assert.Equal(5, GitfsFuseFileSystem.Translate(GitfsError.CorruptObject));
    }
}
