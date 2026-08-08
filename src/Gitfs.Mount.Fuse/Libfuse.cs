using System.Runtime.InteropServices;

namespace Gitfs.Mount.Fuse;

/// <summary>Прямой P/Invoke к libfuse3. Своя привязка, а не чужой пакет —
/// по той же причине, что и свой ридер git в M1: доступные .NET-биндинги
/// FUSE не обновлялись с эпохи netcoreapp3, а нужны нам сорок сигнатур,
/// которые проще написать, чем поддерживать чужую зависимость.
///
/// Используется высокоуровневый API (пути, а не иноды): он принимает ровно
/// то, что принимает IMountTarget, и не требует собственной таблицы имён.</summary>
internal static unsafe partial class Libfuse
{
    private const string Library = "fuse3";

    /// <summary>Имя библиотеки различается по дистрибутивам, а SONAME
    /// (libfuse3.so.3) есть везде, где стоит рантайм-пакет; голое
    /// libfuse3.so приходит только с -dev. Пробуем по очереди, чтобы
    /// пользователю не приходилось ставить пакет для разработки.</summary>
    static Libfuse()
    {
        NativeLibrary.SetDllImportResolver(typeof(Libfuse).Assembly, (name, assembly, path) =>
        {
            if (name != Library) return IntPtr.Zero;
            foreach (var candidate in new[] { "libfuse3.so.3", "libfuse3.so", "libfuse.so.3" })
                if (NativeLibrary.TryLoad(candidate, assembly, path, out var handle))
                    return handle;
            return IntPtr.Zero;
        });
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct FuseArgs
    {
        public int Argc;
        public byte** Argv;
        public int Allocated;
    }

    [LibraryImport(Library, EntryPoint = "fuse_new")]
    public static partial IntPtr New(ref FuseArgs args, void* ops, nuint opsSize, IntPtr privateData);

    [LibraryImport(Library, EntryPoint = "fuse_mount", StringMarshalling = StringMarshalling.Utf8)]
    public static partial int Mount(IntPtr fuse, string mountPoint);

    [LibraryImport(Library, EntryPoint = "fuse_loop")]
    public static partial int Loop(IntPtr fuse);

    /// <summary>Просит цикл завершиться. Сам по себе не будит цикл, ждущий
    /// на /dev/fuse: разбудить его умеет только обращение к точке
    /// монтирования либо fuse_unmount (см. GitfsFuseMount.Dispose).</summary>
    [LibraryImport(Library, EntryPoint = "fuse_exit")]
    public static partial void Exit(IntPtr fuse);

    [LibraryImport(Library, EntryPoint = "fuse_unmount")]
    public static partial void Unmount(IntPtr fuse);

    [LibraryImport(Library, EntryPoint = "fuse_destroy")]
    public static partial void Destroy(IntPtr fuse);

    [LibraryImport(Library, EntryPoint = "fuse_get_context")]
    public static partial IntPtr GetContext();

    [LibraryImport(Library, EntryPoint = "fuse_version")]
    public static partial int Version();

    /// <summary>Экземпляр адаптера для текущего вызова: в одном процессе может
    /// быть смонтировано несколько томов, поэтому статическое поле не подошло
    /// бы. private_data кладётся при fuse_new и возвращается здесь.</summary>
    public static IntPtr CurrentPrivateData()
    {
        var context = GetContext();
        return context == IntPtr.Zero
            ? IntPtr.Zero
            : *(IntPtr*)((byte*)context + FuseAbi.ContextPrivateData);
    }
}
