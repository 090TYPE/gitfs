using System.Reflection;
using Fsp;
using Gitfs.Mount.WinFsp;
using Gitfs.Vfs;

namespace Gitfs.Mount.WinFsp.Tests;

/// <summary>Таблица §12 проверяется без драйвера: трансляция кодов —
/// чистая функция, и она обязана быть полной.</summary>
public class ErrorTranslationTests
{
    [Theory]
    [InlineData(GitfsError.None, FileSystemBase.STATUS_SUCCESS)]
    [InlineData(GitfsError.NotFound, FileSystemBase.STATUS_OBJECT_NAME_NOT_FOUND)]
    [InlineData(GitfsError.AmbiguousPrefix, FileSystemBase.STATUS_OBJECT_NAME_NOT_FOUND)]
    [InlineData(GitfsError.NotADirectory, FileSystemBase.STATUS_NOT_A_DIRECTORY)]
    [InlineData(GitfsError.CorruptObject, FileSystemBase.STATUS_DEVICE_DATA_ERROR)]
    [InlineData(GitfsError.IoError, FileSystemBase.STATUS_IO_DEVICE_ERROR)]
    [InlineData(GitfsError.AccessDenied, FileSystemBase.STATUS_ACCESS_DENIED)]
    [InlineData(GitfsError.NotSupported, FileSystemBase.STATUS_NOT_SUPPORTED)]
    [InlineData(GitfsError.TooLarge, FileSystemBase.STATUS_FILE_TOO_LARGE)]
    [InlineData(GitfsError.IsADirectory, FileSystemBase.STATUS_DIRECTORY_NOT_EMPTY)]
    public void Every_error_maps_to_its_ntstatus(GitfsError error, int expected)
    {
        Assert.Equal(expected, GitfsFileSystem.Translate(error));
    }

    [Fact]
    public void Every_error_of_the_boundary_is_compared_with_a_case_here()
    {
        // Прежняя версия требовала лишь «не STATUS_SUCCESS» — чему
        // catch-all `_ => STATUS_IO_DEVICE_ERROR` удовлетворяет всегда,
        // то есть новый код ошибки она бы и не заметила. Сверяем по
        // именам: у каждого значения обязана быть своя строка выше.
        var asserted = typeof(ErrorTranslationTests)
            .GetMethod(nameof(Every_error_maps_to_its_ntstatus))!
            .GetCustomAttributes<Xunit.InlineDataAttribute>()
            .Select(a => (GitfsError)a.GetData(null!).First()[0]!)
            .ToHashSet();

        var missing = Enum.GetValues<GitfsError>().Where(e => !asserted.Contains(e)).ToList();
        Assert.True(missing.Count == 0,
            "these would silently become STATUS_IO_DEVICE_ERROR: " + string.Join(", ", missing));
    }

    [Fact]
    public void Only_io_error_and_unknown_share_the_io_status()
    {
        var ioMapped = Enum.GetValues<GitfsError>()
            .Where(e => GitfsFileSystem.Translate(e) == FileSystemBase.STATUS_IO_DEVICE_ERROR)
            .ToArray();
        Assert.Equal(new[] { GitfsError.IoError }, ioMapped);
    }
}
