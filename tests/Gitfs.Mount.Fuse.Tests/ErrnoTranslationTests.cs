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
    public void Error_maps_to_the_posix_code(GitfsError error, int expected) =>
        Assert.Equal(expected, GitfsFuseFileSystem.Translate(error));

    [Fact]
    public void Every_error_of_the_boundary_has_a_translation()
    {
        // новое значение GitfsError не должно тихо превращаться в EIO:
        // отсутствующий перевод обязан быть замечен здесь, а не в отладке
        foreach (GitfsError error in Enum.GetValues<GitfsError>())
        {
            var code = GitfsFuseFileSystem.Translate(error);
            if (error == GitfsError.None) Assert.Equal(0, code);
            else Assert.True(code > 0, $"{error} has no translation");
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
