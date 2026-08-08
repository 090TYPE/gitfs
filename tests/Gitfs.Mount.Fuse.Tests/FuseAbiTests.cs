using System.Reflection;
using Gitfs.Mount.Fuse;

namespace Gitfs.Mount.Fuse.Tests;

/// <summary>Сверяет константы раскладки с тем, что напечатал компилятор на
/// целевой платформе (tools/fuse-abi-probe.c). Ошибка в смещении не даёт
/// ошибки компиляции — она даёт вызов не той функции ядром, поэтому эталоном
/// здесь может быть только сам ABI, а не наше представление о нём.
///
/// Фикстура снята на linux-x86_64. Появится другая архитектура — появится
/// вторая фикстура и выбор по RuntimeInformation.ProcessArchitecture;
/// сейчас её нет, и тест об этом честно говорит.</summary>
public class FuseAbiTests
{
    private static readonly IReadOnlyDictionary<string, long> Probe = LoadProbe();

    private static IReadOnlyDictionary<string, long> LoadProbe()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "fixtures", "fuse-abi.txt");
        var map = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var line in File.ReadAllLines(path))
        {
            if (line.StartsWith('#')) continue;
            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            // sizeof struct fuse_operations 336 / offset struct stat.st_mode 24
            if (parts.Length < 3 || parts[0] is not ("sizeof" or "offset")) continue;
            var name = string.Join(' ', parts[1..^1]);
            map[$"{parts[0]} {name}"] = long.Parse(parts[^1]);
        }
        return map;
    }

    private static long Expected(string key)
    {
        Assert.True(Probe.ContainsKey(key), $"fixture has no entry for {key}");
        return Probe[key];
    }

    [Theory]
    // порядок в struct fuse_operations: любое смещение мимо — вызов не той
    // операции; libfuse не сообщит об этом никак
    [InlineData("offset struct fuse_operations.getattr", FuseAbi.OpGetattr)]
    [InlineData("offset struct fuse_operations.readlink", FuseAbi.OpReadlink)]
    [InlineData("offset struct fuse_operations.mknod", FuseAbi.OpMknod)]
    [InlineData("offset struct fuse_operations.mkdir", FuseAbi.OpMkdir)]
    [InlineData("offset struct fuse_operations.unlink", FuseAbi.OpUnlink)]
    [InlineData("offset struct fuse_operations.rmdir", FuseAbi.OpRmdir)]
    [InlineData("offset struct fuse_operations.symlink", FuseAbi.OpSymlink)]
    [InlineData("offset struct fuse_operations.rename", FuseAbi.OpRename)]
    [InlineData("offset struct fuse_operations.link", FuseAbi.OpLink)]
    [InlineData("offset struct fuse_operations.chmod", FuseAbi.OpChmod)]
    [InlineData("offset struct fuse_operations.chown", FuseAbi.OpChown)]
    [InlineData("offset struct fuse_operations.truncate", FuseAbi.OpTruncate)]
    [InlineData("offset struct fuse_operations.open", FuseAbi.OpOpen)]
    [InlineData("offset struct fuse_operations.read", FuseAbi.OpRead)]
    [InlineData("offset struct fuse_operations.write", FuseAbi.OpWrite)]
    [InlineData("offset struct fuse_operations.statfs", FuseAbi.OpStatfs)]
    [InlineData("offset struct fuse_operations.flush", FuseAbi.OpFlush)]
    [InlineData("offset struct fuse_operations.release", FuseAbi.OpRelease)]
    [InlineData("offset struct fuse_operations.fsync", FuseAbi.OpFsync)]
    [InlineData("offset struct fuse_operations.opendir", FuseAbi.OpOpendir)]
    [InlineData("offset struct fuse_operations.readdir", FuseAbi.OpReaddir)]
    [InlineData("offset struct fuse_operations.releasedir", FuseAbi.OpReleasedir)]
    [InlineData("offset struct fuse_operations.init", FuseAbi.OpInit)]
    [InlineData("offset struct fuse_operations.destroy", FuseAbi.OpDestroy)]
    [InlineData("offset struct fuse_operations.access", FuseAbi.OpAccess)]
    [InlineData("offset struct fuse_operations.create", FuseAbi.OpCreate)]
    [InlineData("offset struct fuse_operations.utimens", FuseAbi.OpUtimens)]
    [InlineData("sizeof struct fuse_operations", FuseAbi.OperationsSize)]
    // struct stat: st_nlink лежит ПЕРЕД st_mode — «очевидный» порядок неверен
    [InlineData("offset struct stat.st_ino", FuseAbi.StatIno)]
    [InlineData("offset struct stat.st_nlink", FuseAbi.StatNlink)]
    [InlineData("offset struct stat.st_mode", FuseAbi.StatMode)]
    [InlineData("offset struct stat.st_uid", FuseAbi.StatUid)]
    [InlineData("offset struct stat.st_gid", FuseAbi.StatGid)]
    [InlineData("offset struct stat.st_size", FuseAbi.StatSizeField)]
    [InlineData("offset struct stat.st_blksize", FuseAbi.StatBlksize)]
    [InlineData("offset struct stat.st_blocks", FuseAbi.StatBlocks)]
    [InlineData("offset struct stat.st_atim", FuseAbi.StatAtim)]
    [InlineData("offset struct stat.st_mtim", FuseAbi.StatMtim)]
    [InlineData("offset struct stat.st_ctim", FuseAbi.StatCtim)]
    [InlineData("sizeof struct stat", FuseAbi.StatSize)]
    // fuse_file_info: fh на 16, а не на 8 — перед ним битовое поле
    [InlineData("offset struct fuse_file_info.flags", FuseAbi.FileInfoFlags)]
    [InlineData("offset struct fuse_file_info.fh", FuseAbi.FileInfoFh)]
    [InlineData("sizeof struct fuse_file_info", FuseAbi.FileInfoSize)]
    [InlineData("offset struct statvfs.f_bsize", FuseAbi.StatvfsBsize)]
    [InlineData("offset struct statvfs.f_frsize", FuseAbi.StatvfsFrsize)]
    [InlineData("offset struct statvfs.f_blocks", FuseAbi.StatvfsBlocks)]
    [InlineData("offset struct statvfs.f_bfree", FuseAbi.StatvfsBfree)]
    [InlineData("offset struct statvfs.f_bavail", FuseAbi.StatvfsBavail)]
    [InlineData("offset struct statvfs.f_files", FuseAbi.StatvfsFiles)]
    [InlineData("offset struct statvfs.f_ffree", FuseAbi.StatvfsFfree)]
    [InlineData("offset struct statvfs.f_namemax", FuseAbi.StatvfsNamemax)]
    [InlineData("sizeof struct statvfs", FuseAbi.StatvfsSize)]
    [InlineData("offset struct fuse_args.argc", FuseAbi.ArgsArgc)]
    [InlineData("offset struct fuse_args.argv", FuseAbi.ArgsArgv)]
    [InlineData("offset struct fuse_args.allocated", FuseAbi.ArgsAllocated)]
    [InlineData("sizeof struct fuse_args", FuseAbi.ArgsSize)]
    // через private_data колбэк находит свой том; ошибка здесь — чужая память
    [InlineData("offset struct fuse_context.private_data", FuseAbi.ContextPrivateData)]
    [InlineData("sizeof struct fuse_context", FuseAbi.ContextSize)]
    [InlineData("offset struct timespec.tv_sec", FuseAbi.TimespecSec)]
    [InlineData("offset struct timespec.tv_nsec", FuseAbi.TimespecNsec)]
    [InlineData("sizeof struct timespec", FuseAbi.TimespecSize)]
    public void Constant_matches_what_the_compiler_reported(string key, int ours) =>
        Assert.Equal(Expected(key), ours);

    [Fact]
    public void Fixture_was_taken_from_fuse_3()
    {
        var line = File.ReadAllLines(
                Path.Combine(AppContext.BaseDirectory, "fixtures", "fuse-abi.txt"))
            .First(l => l.StartsWith("fuse_version "));
        var version = int.Parse(line.Split(' ')[1]);
        Assert.InRange(version, 300, 399);
        Assert.Equal(FuseAbi.ProbedFuseVersion, version);
    }

    [Fact]
    public void Fixture_was_taken_on_a_64_bit_target()
    {
        // раскладка stat и statvfs на 32 битах другая; фикстура одна,
        // и она обязана заявлять, для чего снята
        var line = File.ReadAllLines(
                Path.Combine(AppContext.BaseDirectory, "fixtures", "fuse-abi.txt"))
            .First(l => l.StartsWith("arch "));
        Assert.Equal("arch 64", line.Trim());
    }

    [Fact]
    public void The_fixture_matches_the_architecture_this_test_is_running_on()
    {
        // «arch 64» — это заявление фикстуры о себе, и сверять его с самой
        // фикстурой бессмысленно: под linux-arm64 всё зеленело, хотя
        // раскладка там совсем другая (stat 128 байт, st_mode на 16).
        // Сверяться надо с настоящей архитектурой процесса.
        if (!OperatingSystem.IsLinux()) return;   // на Windows адаптер не грузится
        Assert.Equal(System.Runtime.InteropServices.Architecture.X64,
            System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture);
    }

    [Fact]
    public void Every_line_of_the_probe_is_compared_with_a_constant()
    {
        // Защита от «расширил пробу, забыл сверить». Сравнивать по ИМЕНАМ
        // полей, а не по значениям: нулевое смещение у пяти разных структур,
        // и проверка по значению зеленела бы, ничего не проверяя.
        var asserted = typeof(FuseAbiTests)
            .GetMethod(nameof(Constant_matches_what_the_compiler_reported))!
            .GetCustomAttributes<InlineDataAttribute>()
            .Select(a => (string)a.GetData(null!).First()[0]!)
            .ToHashSet(StringComparer.Ordinal);

        var unchecked_ = Probe.Keys.Where(k => !asserted.Contains(k)).OrderBy(k => k).ToList();
        Assert.True(unchecked_.Count == 0,
            "the probe reports these, and nothing compares them: "
            + string.Join(", ", unchecked_));
    }
}
