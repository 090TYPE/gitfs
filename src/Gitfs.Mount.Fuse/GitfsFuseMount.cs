using System.Runtime.InteropServices;
using Gitfs.Vfs;

namespace Gitfs.Mount.Fuse;

public sealed class FuseMountException : Exception
{
    public FuseMountException(string message, Exception? inner = null) : base(message, inner) { }
}

/// <summary>Смонтированный том на Linux. Ровня GitfsMount из адаптера WinFsp:
/// смонтировать, крутить цикл в фоне, снять по Dispose.
///
/// Взяты fuse_new + fuse_mount + fuse_loop, а не fuse_main: последний
/// блокирует поток и сам разбирает argv, что годится для утилиты и не
/// годится для приложения с кнопкой «Смонтировать».</summary>
public sealed unsafe class GitfsFuseMount : IDisposable
{
    private readonly IntPtr _fuse;
    private readonly GitfsFuseFileSystem _fs;
    private readonly GCHandle _self;
    private readonly IntPtr _operations;
    private readonly Thread _loop;
    private readonly string _mountPoint;
    private volatile bool _disposed;

    private GitfsFuseMount(IntPtr fuse, GitfsFuseFileSystem fs, GCHandle self,
        IntPtr operations, string mountPoint)
    {
        _fuse = fuse;
        _fs = fs;
        _self = self;
        _operations = operations;
        _mountPoint = mountPoint;
        _loop = new Thread(() => Libfuse.Loop(_fuse))
        {
            IsBackground = true,
            Name = $"gitfs-fuse {mountPoint}",
        };
        _loop.Start();
    }

    public string MountPoint => _mountPoint;

    /// <summary>Монтирует том в каталог. Каталог должен существовать и быть
    /// пустым — это требование FUSE, а не наше, но сообщение об этом должно
    /// быть человеческим.</summary>
    /// <exception cref="FuseMountException">libfuse недоступна или отказала.</exception>
    public static GitfsFuseMount Mount(IMountTarget target, string mountPoint,
        Action<string>? log = null, bool readOnly = true)
    {
        if (!OperatingSystem.IsLinux())
            throw new FuseMountException("the FUSE adapter runs on Linux only");
        if (!Directory.Exists(mountPoint))
            throw new FuseMountException($"{mountPoint} does not exist; create the directory first");

        try { _ = Libfuse.Version(); }
        catch (DllNotFoundException e)
        {
            throw new FuseMountException(
                "libfuse3 is not installed; install fuse3 from your distribution", e);
        }
        catch (EntryPointNotFoundException e)
        {
            throw new FuseMountException("the installed libfuse is too old; gitfs needs FUSE 3", e);
        }

        var fs = new GitfsFuseFileSystem(target, readOnly, log);
        var self = GCHandle.Alloc(fs);
        var operations = Marshal.AllocHGlobal(FuseAbi.OperationsSize);
        GitfsFuseFileSystem.FillOperations((byte*)operations);

        // Ключи монтирования (§11): права считает ядро по нашим st_mode;
        // fsname задаёт то, что покажет df и mount.
        var options = new List<string> { "gitfs", "-o", "default_permissions", "-o", "fsname=gitfs" };
        if (readOnly) { options.Add("-o"); options.Add("ro"); }

        var argv = AllocArgv(options);
        try
        {
            var args = new Libfuse.FuseArgs { Argc = options.Count, Argv = argv, Allocated = 0 };
            var fuse = Libfuse.New(ref args, (void*)operations, (nuint)FuseAbi.OperationsSize,
                GCHandle.ToIntPtr(self));
            if (fuse == IntPtr.Zero)
                throw Failed(self, operations, fs, "libfuse refused the mount options");

            if (Libfuse.Mount(fuse, mountPoint) != 0)
            {
                Libfuse.Destroy(fuse);
                throw Failed(self, operations, fs,
                    $"could not mount at {mountPoint}: it must be an existing, "
                    + "unused directory, and /dev/fuse must be accessible");
            }
            return new GitfsFuseMount(fuse, fs, self, operations, mountPoint);
        }
        finally { FreeArgv(argv, options.Count); }
    }

    private static FuseMountException Failed(GCHandle self, IntPtr operations,
        GitfsFuseFileSystem fs, string message)
    {
        self.Free();
        Marshal.FreeHGlobal(operations);
        fs.Dispose();
        return new FuseMountException(message);
    }

    private static byte** AllocArgv(List<string> values)
    {
        var argv = (byte**)Marshal.AllocHGlobal(IntPtr.Size * values.Count);
        for (var i = 0; i < values.Count; i++)
            argv[i] = (byte*)Marshal.StringToHGlobalAnsi(values[i]);
        return argv;
    }

    private static void FreeArgv(byte** argv, int count)
    {
        for (var i = 0; i < count; i++) Marshal.FreeHGlobal((IntPtr)argv[i]);
        Marshal.FreeHGlobal((IntPtr)argv);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        // Порядок важен: fuse_exit только поднимает флаг, а разбудить цикл,
        // спящий на /dev/fuse, умеет лишь размонтирование. Сначала exit,
        // чтобы цикл не подхватил новую операцию, затем unmount.
        Libfuse.Exit(_fuse);
        Libfuse.Unmount(_fuse);
        _loop.Join(TimeSpan.FromSeconds(5));
        Libfuse.Destroy(_fuse);

        _fs.Dispose();
        if (_self.IsAllocated) _self.Free();
        Marshal.FreeHGlobal(_operations);
    }
}
