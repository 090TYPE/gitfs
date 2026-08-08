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
    public void Every_error_maps_to_its_ntstatus(GitfsError error, int expected)
    {
        Assert.Equal(expected, GitfsFileSystem.Translate(error));
    }

    [Fact]
    public void Translation_table_covers_the_whole_enum()
    {
        // новый код ошибки не должен молча получить IoError по умолчанию
        foreach (GitfsError error in Enum.GetValues<GitfsError>())
        {
            var status = GitfsFileSystem.Translate(error);
            if (error == GitfsError.None)
            {
                Assert.Equal(FileSystemBase.STATUS_SUCCESS, status);
                continue;
            }
            Assert.NotEqual(FileSystemBase.STATUS_SUCCESS, status);
        }
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
