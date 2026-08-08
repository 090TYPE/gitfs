using Gitfs.Mount.WinFsp;

namespace Gitfs.Mount.WinFsp.Tests;

/// <summary>Точка монтирования проверяется до драйвера — и это не
/// придирчивость. `gitfs mount . 1:` не выдавал ошибку, а РОНЯЛ процесс:
/// FspFileSystemSetMountPointEx уходил в нарушение доступа, которое в .NET
/// не ловится вовсе, и пользователь видел «Fatal error. Attempted to read or
/// write protected memory». А попытка встать на занятую букву не
/// возвращалась вообще — этим один из собственных тестов подвесил CI на
/// сорок минут.
///
/// Проверяется без драйвера: до host.Mount дело не доходит.</summary>
public class MountPointValidationTests
{
    private static Exception? Mount(string mountPoint) =>
        Record.Exception(() => GitfsMount.Mount(new StubTarget(), mountPoint));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void A_missing_mount_point_is_refused(string mountPoint)
    {
        var e = Assert.IsType<MountException>(Mount(mountPoint));
        Assert.Contains("required", e.Message);
    }

    [Theory]
    [InlineData("1:")]   // ровно это и роняло процесс
    [InlineData("!:")]
    [InlineData("::")]
    [InlineData("я:")]   // не латиница
    public void Something_that_looks_like_a_drive_letter_but_is_not_is_refused(string mountPoint)
    {
        var e = Assert.IsType<MountException>(Mount(mountPoint));
        Assert.Contains("drive letter", e.Message);
    }

    [Fact]
    public void A_letter_that_is_already_taken_is_refused_rather_than_hanging()
    {
        if (!OperatingSystem.IsWindows()) return;
        var taken = DriveInfo.GetDrives()[0].Name[..2];   // «C:»
        var e = Assert.IsType<MountException>(Mount(taken));
        Assert.Contains("already in use", e.Message);
    }

    [Fact]
    public void A_path_that_is_not_a_directory_is_refused()
    {
        var e = Assert.IsType<MountException>(
            Mount(Path.Combine(Path.GetTempPath(), "gitfs-no-such-" + Guid.NewGuid().ToString("N")[..8])));
        Assert.Contains("directory", e.Message);
    }

    [Fact]
    public void A_directory_that_is_not_empty_is_refused()
    {
        var dir = Path.Combine(Path.GetTempPath(), "gitfs-busy-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "occupied.txt"), "x");
        try
        {
            var e = Assert.IsType<MountException>(Mount(dir));
            Assert.Contains("not empty", e.Message);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void Every_refusal_names_the_mount_point_or_says_what_is_wanted()
    {
        // сообщение обязано дать пользователю следующий шаг, а не
        // констатировать неудачу
        foreach (var bad in new[] { "", "1:", "::" })
        {
            var e = Assert.IsType<MountException>(Mount(bad));
            Assert.False(string.IsNullOrWhiteSpace(e.Message));
            Assert.True(e.Message.Contains(bad, StringComparison.Ordinal) || bad.Length == 0,
                $"the message for '{bad}' does not name it: {e.Message}");
        }
    }

    /// <summary>Цель нужна только чтобы дойти до проверки: до неё
    /// GitfsMount ничего с ней не делает.</summary>
    private sealed class StubTarget : Gitfs.Vfs.IMountTarget
    {
        public Gitfs.Vfs.GitfsResult<Gitfs.Vfs.NodeInfo> Lookup(string path) =>
            Gitfs.Vfs.GitfsResult<Gitfs.Vfs.NodeInfo>.Fail(Gitfs.Vfs.GitfsError.NotFound);
        public Gitfs.Vfs.GitfsResult<IEnumerable<Gitfs.Vfs.DirEntry>> List(string path) =>
            Gitfs.Vfs.GitfsResult<IEnumerable<Gitfs.Vfs.DirEntry>>.Fail(Gitfs.Vfs.GitfsError.NotFound);
        public Gitfs.Vfs.GitfsResult<Gitfs.Vfs.FileHandle> Open(string path, Gitfs.Vfs.OpenMode mode) =>
            Gitfs.Vfs.GitfsResult<Gitfs.Vfs.FileHandle>.Fail(Gitfs.Vfs.GitfsError.NotFound);
        public Gitfs.Vfs.GitfsResult<int> Read(Gitfs.Vfs.FileHandle handle, long offset, Span<byte> buffer) =>
            Gitfs.Vfs.GitfsResult<int>.Fail(Gitfs.Vfs.GitfsError.NotSupported);
        public Gitfs.Vfs.GitfsResult<int> Write(Gitfs.Vfs.FileHandle handle, long offset, ReadOnlySpan<byte> data) =>
            Gitfs.Vfs.GitfsResult<int>.Fail(Gitfs.Vfs.GitfsError.NotSupported);
        public Gitfs.Vfs.GitfsResult<Gitfs.Vfs.Unit> SetLength(Gitfs.Vfs.FileHandle handle, long length) =>
            Gitfs.Vfs.GitfsResult<Gitfs.Vfs.Unit>.Fail(Gitfs.Vfs.GitfsError.NotSupported);
        public Gitfs.Vfs.GitfsResult<Gitfs.Vfs.Unit> Delete(string path) =>
            Gitfs.Vfs.GitfsResult<Gitfs.Vfs.Unit>.Fail(Gitfs.Vfs.GitfsError.NotSupported);
        public Gitfs.Vfs.GitfsResult<Gitfs.Vfs.Unit> Close(Gitfs.Vfs.FileHandle handle) =>
            Gitfs.Vfs.GitfsResult<Gitfs.Vfs.Unit>.Ok(Gitfs.Vfs.Unit.Value);
        public Gitfs.Vfs.VolumeInfo GetVolumeInfo() => new("gitfs: stub", 0, 0);
        public void Dispose() { }
    }
}
