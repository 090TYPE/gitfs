using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Gitfs.Vfs;

namespace Gitfs.Mount.Fuse;

/// <summary>Трансляция колбэков FUSE в вызовы IMountTarget. Ровно то же
/// место в стеке, что GitfsFileSystem у WinFsp, и ровно та же дисциплина:
/// ни одно исключение не покидает колбэк (для ядра это не исключение, а
/// зависший поток), любой отказ становится кодом -errno.</summary>
internal sealed unsafe class GitfsFuseFileSystem : IDisposable
{
    private readonly IMountTarget _target;
    private readonly bool _readOnly;
    private readonly Action<string>? _log;

    /// <summary>Хендлы адресуются числом: fuse_file_info.fh — это 64 бита,
    /// в которые указатель на управляемый объект не положишь без
    /// закрепления. Номер + словарь дешевле и безопаснее GCHandle на
    /// каждый открытый файл.</summary>
    private readonly object _handleGate = new();
    private readonly Dictionary<ulong, FileHandle> _handles = new();
    private ulong _nextHandle = 1;

    public GitfsFuseFileSystem(IMountTarget target, bool readOnly, Action<string>? log)
    {
        _target = target;
        _readOnly = readOnly;
        _log = log;
    }

    // ---------- таблица операций ----------

    /// <summary>Заполняет struct fuse_operations по проверенным смещениям.
    /// Не заполненные поля остаются нулями — libfuse понимает это как
    /// «операция не поддерживается», что для нас верно.</summary>
    public static void FillOperations(byte* ops)
    {
        new Span<byte>(ops, FuseAbi.OperationsSize).Clear();
        Set(ops, FuseAbi.OpGetattr, (delegate* unmanaged<byte*, byte*, byte*, int>)&Callbacks.Getattr);
        Set(ops, FuseAbi.OpReaddir, (delegate* unmanaged<byte*, void*, IntPtr, long, byte*, int, int>)&Callbacks.Readdir);
        Set(ops, FuseAbi.OpOpen, (delegate* unmanaged<byte*, byte*, int>)&Callbacks.Open);
        Set(ops, FuseAbi.OpRead, (delegate* unmanaged<byte*, byte*, nuint, long, byte*, int>)&Callbacks.Read);
        Set(ops, FuseAbi.OpWrite, (delegate* unmanaged<byte*, byte*, nuint, long, byte*, int>)&Callbacks.Write);
        Set(ops, FuseAbi.OpRelease, (delegate* unmanaged<byte*, byte*, int>)&Callbacks.Release);
        Set(ops, FuseAbi.OpFlush, (delegate* unmanaged<byte*, byte*, int>)&Callbacks.Flush);
        Set(ops, FuseAbi.OpStatfs, (delegate* unmanaged<byte*, byte*, int>)&Callbacks.Statfs);
        Set(ops, FuseAbi.OpCreate, (delegate* unmanaged<byte*, uint, byte*, int>)&Callbacks.Create);
        Set(ops, FuseAbi.OpTruncate, (delegate* unmanaged<byte*, long, byte*, int>)&Callbacks.Truncate);
        Set(ops, FuseAbi.OpUnlink, (delegate* unmanaged<byte*, int>)&Callbacks.Unlink);
        // Word и его собратья трогают метки времени и права; отказ по этим
        // вызовам обрывает сохранение, поэтому они принимаются молча —
        // изменить в репозитории они всё равно ничего не могут
        Set(ops, FuseAbi.OpUtimens, (delegate* unmanaged<byte*, byte*, byte*, int>)&Callbacks.Utimens);
        Set(ops, FuseAbi.OpChmod, (delegate* unmanaged<byte*, uint, byte*, int>)&Callbacks.Chmod);
    }

    private static void Set(byte* ops, int offset, void* function) =>
        *(IntPtr*)(ops + offset) = (IntPtr)function;

    // ---------- операции ----------

    public int Getattr(string path, byte* stat)
    {
        var result = _target.Lookup(path);
        if (!result.TryGet(out var node)) return -Translate(result.Error);
        WriteStat(stat, node);
        return 0;
    }

    public int Readdir(string path, void* buffer, IntPtr filler)
    {
        var listed = _target.List(path);
        if (!listed.TryGet(out var entries)) return -Translate(listed.Error);

        var fill = (delegate* unmanaged<void*, byte*, byte*, long, int, int>)filler;
        // «.» и «..» ядро не подставляет за нас: без них ls показывает
        // каталог без родителя, а find отказывается спускаться
        if (FillName(fill, buffer, ".", null) != 0) return 0;
        if (FillName(fill, buffer, "..", null) != 0) return 0;

        var stat = stackalloc byte[FuseAbi.StatSize];
        foreach (var entry in entries)
        {
            WriteStat(stat, entry.Info);
            // ненулевой ответ filler означает «буфер полон»: ядро попросит
            // остаток следующим вызовом, дозаполнять нельзя
            if (FillName(fill, buffer, entry.Name, stat) != 0) break;
        }
        return 0;
    }

    private static int FillName(delegate* unmanaged<void*, byte*, byte*, long, int, int> fill,
        void* buffer, string name, byte* stat)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(name);
        fixed (byte* p = bytes)
        {
            var zeroTerminated = stackalloc byte[bytes.Length + 1];
            new Span<byte>(p, bytes.Length).CopyTo(new Span<byte>(zeroTerminated, bytes.Length));
            zeroTerminated[bytes.Length] = 0;
            return fill(buffer, zeroTerminated, stat, 0, 0);
        }
    }

    public int Open(string path, byte* fi)
    {
        var flags = *(int*)(fi + FuseAbi.FileInfoFlags);
        var wantsWrite = (flags & (OAccMode | OTrunc | OAppend)) is not OReadOnly;
        var mode = wantsWrite && !_readOnly ? OpenMode.Write : OpenMode.Read;

        var opened = _target.Open(path, mode);
        // том только на чтение: открыть на запись нельзя, но читать можно —
        // тот же компромисс, что в адаптере WinFsp
        if (!opened.TryGet(out var handle) && mode == OpenMode.Write)
            opened = _target.Open(path, OpenMode.Read);
        if (!opened.TryGet(out handle)) return -Translate(opened.Error);

        // O_TRUNC обязаны отработать МЫ. Ядро согласовало атомарное усечение
        // и отдельного setattr(size=0) не пришлёт — оно кладёт флаг сюда, в
        // open. Без этого `echo x > файл` дописывал в начало, оставляя хвост
        // прежнего содержимого: файл на 1073 байта после записи пяти
        // оставался на 1073 байта (найдено приёмкой на живом томе).
        if ((flags & OTrunc) != 0 && mode == OpenMode.Write)
        {
            var truncated = _target.SetLength(handle, 0);
            if (!truncated.TryGet(out _))
            {
                _target.Close(handle);
                return -Translate(truncated.Error);
            }
        }

        *(ulong*)(fi + FuseAbi.FileInfoFh) = Remember(handle);
        return 0;
    }

    public int Create(string path, byte* fi)
    {
        if (_readOnly) return -Errno.Rofs;
        var opened = _target.Open(path, OpenMode.Write);
        if (!opened.TryGet(out var handle)) return -Translate(opened.Error);
        *(ulong*)(fi + FuseAbi.FileInfoFh) = Remember(handle);
        return 0;
    }

    public int Read(string path, byte* buffer, nuint size, long offset, byte* fi)
    {
        if (!TryResolve(fi, path, OpenMode.Read, out var handle, out var borrowed, out var error))
            return -error;
        try
        {
            var span = new Span<byte>(buffer, checked((int)Math.Min(size, int.MaxValue)));
            var read = _target.Read(handle, offset, span);
            return read.TryGet(out var count) ? count : -Translate(read.Error);
        }
        finally { if (borrowed) _target.Close(handle); }
    }

    public int Write(string path, byte* buffer, nuint size, long offset, byte* fi)
    {
        if (_readOnly) return -Errno.Rofs;
        if (!TryResolve(fi, path, OpenMode.Write, out var handle, out var borrowed, out var error))
            return -error;
        try
        {
            var span = new ReadOnlySpan<byte>(buffer, checked((int)Math.Min(size, int.MaxValue)));
            var written = _target.Write(handle, offset, span);
            return written.TryGet(out var count) ? count : -Translate(written.Error);
        }
        finally { if (borrowed) _target.Close(handle); }
    }

    public int Truncate(string path, long length, byte* fi)
    {
        if (_readOnly) return -Errno.Rofs;
        if (!TryResolve(fi, path, OpenMode.Write, out var handle, out var borrowed, out var error))
            return -error;
        try
        {
            var result = _target.SetLength(handle, length);
            return result.TryGet(out _) ? 0 : -Translate(result.Error);
        }
        finally { if (borrowed) _target.Close(handle); }
    }

    public int Unlink(string path)
    {
        if (_readOnly) return -Errno.Rofs;
        var result = _target.Delete(path);
        return result.TryGet(out _) ? 0 : -Translate(result.Error);
    }

    public int Release(byte* fi)
    {
        var id = *(ulong*)(fi + FuseAbi.FileInfoFh);
        FileHandle? handle;
        lock (_handleGate)
        {
            if (!_handles.Remove(id, out handle)) return 0; // уже закрыт
        }
        _target.Close(handle);
        return 0;
    }

    public int Statfs(byte* stat)
    {
        var info = _target.GetVolumeInfo();
        new Span<byte>(stat, FuseAbi.StatvfsSize).Clear();
        const ulong blockSize = 4096;
        *(ulong*)(stat + FuseAbi.StatvfsBsize) = blockSize;
        *(ulong*)(stat + FuseAbi.StatvfsFrsize) = blockSize;
        *(ulong*)(stat + FuseAbi.StatvfsBlocks) = (ulong)info.TotalBytes / blockSize;
        *(ulong*)(stat + FuseAbi.StatvfsBfree) = (ulong)info.FreeBytes / blockSize;
        *(ulong*)(stat + FuseAbi.StatvfsBavail) = (ulong)info.FreeBytes / blockSize;
        *(ulong*)(stat + FuseAbi.StatvfsNamemax) = 255;
        return 0;
    }

    // ---------- хендлы ----------

    private ulong Remember(FileHandle handle)
    {
        lock (_handleGate)
        {
            var id = _nextHandle++;
            _handles[id] = handle;
            return id;
        }
    }

    /// <summary>Хендл из fuse_file_info, а если его там нет — открытый на
    /// время одной операции. Ядро вправе прислать read без предшествующего
    /// open (например, при чтении через readahead после сброса кэша).</summary>
    private bool TryResolve(byte* fi, string path, OpenMode mode,
        out FileHandle handle, out bool borrowed, out int error)
    {
        borrowed = false;
        error = 0;
        if (fi != null)
        {
            var id = *(ulong*)(fi + FuseAbi.FileInfoFh);
            lock (_handleGate)
            {
                if (id != 0 && _handles.TryGetValue(id, out handle!)) return true;
            }
        }
        var opened = _target.Open(path, mode);
        if (!opened.TryGet(out handle!))
        {
            error = Translate(opened.Error);
            return false;
        }
        borrowed = true;
        return true;
    }

    // ---------- перевод модели ----------

    private void WriteStat(byte* stat, NodeInfo node)
    {
        new Span<byte>(stat, FuseAbi.StatSize).Clear();
        var directory = node.Kind == NodeKind.Directory;
        var mode = directory
            ? FileMode.Directory | (_readOnly ? FileMode.DirReadOnly : FileMode.DirReadWrite)
            : FileMode.Regular | (_readOnly ? FileMode.FileReadOnly : FileMode.FileReadWrite);

        *(uint*)(stat + FuseAbi.StatMode) = mode;
        *(ulong*)(stat + FuseAbi.StatNlink) = directory ? 2UL : 1UL;
        *(uint*)(stat + FuseAbi.StatUid) = Uid;
        *(uint*)(stat + FuseAbi.StatGid) = Gid;
        *(long*)(stat + FuseAbi.StatSizeField) = node.Size;
        *(long*)(stat + FuseAbi.StatBlksize) = 4096;
        *(long*)(stat + FuseAbi.StatBlocks) = (node.Size + 511) / 512;

        var seconds = node.Timestamp.ToUnixTimeSeconds();
        foreach (var field in stackalloc[] { FuseAbi.StatAtim, FuseAbi.StatMtim, FuseAbi.StatCtim })
            *(long*)(stat + field + FuseAbi.TimespecSec) = seconds;
    }

    private static readonly uint Uid = (uint)Geteuid();
    private static readonly uint Gid = (uint)Getegid();

    [DllImport("libc", EntryPoint = "geteuid")] private static extern int Geteuid();
    [DllImport("libc", EntryPoint = "getegid")] private static extern int Getegid();

    /// <summary>Тот же перевод, что Translate(GitfsError) в адаптере WinFsp,
    /// только в коды POSIX. Возвращается положительное число; минус ставит
    /// вызывающий, чтобы знак нельзя было потерять по дороге.</summary>
    internal static int Translate(GitfsError error) => error switch
    {
        GitfsError.None => 0,
        GitfsError.NotFound => Errno.NoEnt,
        GitfsError.NotADirectory => Errno.NotDir,
        // неоднозначный префикс SHA — это не «нет такого файла»: файлов
        // подходит несколько, и выбор за пользователем
        GitfsError.AmbiguousPrefix => Errno.Inval,
        GitfsError.CorruptObject => Errno.IO,
        GitfsError.IoError => Errno.IO,
        GitfsError.AccessDenied => Errno.Access,
        GitfsError.NotSupported => Errno.NotSup,
        GitfsError.TooLarge => Errno.NoSpc,
        _ => Errno.IO,
    };

    private const int OAccMode = 3;
    private const int OReadOnly = 0;
    private const int OTrunc = 0x200;
    private const int OAppend = 0x400;

    internal void Log(string operation, Exception e) =>
        _log?.Invoke($"{operation}: {e.GetType().Name}: {e.Message}");

    public void Dispose()
    {
        List<FileHandle> live;
        lock (_handleGate)
        {
            live = _handles.Values.ToList();
            _handles.Clear();
        }
        foreach (var handle in live) _target.Close(handle);
        _target.Dispose();
    }

    // ---------- колбэки ----------

    /// <summary>Статические точки входа для ядра. Исключение, вылетевшее
    /// отсюда в нативный код, — это не исключение, а неопределённое
    /// поведение, поэтому ловится всё и всегда.</summary>
    private static class Callbacks
    {
        [UnmanagedCallersOnly]
        public static int Getattr(byte* path, byte* stat, byte* fi) =>
            Guard(nameof(Getattr), fs => fs.Getattr(Str(path), stat));

        [UnmanagedCallersOnly]
        public static int Readdir(byte* path, void* buffer, IntPtr filler, long offset,
            byte* fi, int flags) =>
            Guard(nameof(Readdir), fs => fs.Readdir(Str(path), buffer, filler));

        [UnmanagedCallersOnly]
        public static int Open(byte* path, byte* fi) =>
            Guard(nameof(Open), fs => fs.Open(Str(path), fi));

        [UnmanagedCallersOnly]
        public static int Create(byte* path, uint mode, byte* fi) =>
            Guard(nameof(Create), fs => fs.Create(Str(path), fi));

        [UnmanagedCallersOnly]
        public static int Read(byte* path, byte* buffer, nuint size, long offset, byte* fi) =>
            Guard(nameof(Read), fs => fs.Read(Str(path), buffer, size, offset, fi));

        [UnmanagedCallersOnly]
        public static int Write(byte* path, byte* buffer, nuint size, long offset, byte* fi) =>
            Guard(nameof(Write), fs => fs.Write(Str(path), buffer, size, offset, fi));

        [UnmanagedCallersOnly]
        public static int Truncate(byte* path, long length, byte* fi) =>
            Guard(nameof(Truncate), fs => fs.Truncate(Str(path), length, fi));

        [UnmanagedCallersOnly]
        public static int Unlink(byte* path) =>
            Guard(nameof(Unlink), fs => fs.Unlink(Str(path)));

        [UnmanagedCallersOnly]
        public static int Release(byte* path, byte* fi) =>
            Guard(nameof(Release), fs => fs.Release(fi));

        [UnmanagedCallersOnly]
        public static int Flush(byte* path, byte* fi) => 0;

        [UnmanagedCallersOnly]
        public static int Statfs(byte* path, byte* stat) =>
            Guard(nameof(Statfs), fs => fs.Statfs(stat));

        /// <summary>Метки времени и права принимаются и игнорируются: том
        /// их не хранит, но отказ обрывает сохранение из редакторов.</summary>
        [UnmanagedCallersOnly]
        public static int Utimens(byte* path, byte* times, byte* fi) => 0;

        [UnmanagedCallersOnly]
        public static int Chmod(byte* path, uint mode, byte* fi) => 0;
    }

    private static int Guard(string operation, Func<GitfsFuseFileSystem, int> body)
    {
        GitfsFuseFileSystem? fs = null;
        try
        {
            var handle = Libfuse.CurrentPrivateData();
            if (handle == IntPtr.Zero) return -Errno.IO;
            fs = (GitfsFuseFileSystem?)GCHandle.FromIntPtr(handle).Target;
            if (fs is null) return -Errno.IO;
            return body(fs);
        }
        catch (Exception e)
        {
            fs?.Log(operation, e);
            return -Errno.IO;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static string Str(byte* utf8) =>
        utf8 == null ? "/" : Marshal.PtrToStringUTF8((IntPtr)utf8) ?? "/";
}
