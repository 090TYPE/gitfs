namespace Gitfs.Mount.Fuse;

/// <summary>Фактическая раскладка структур libfuse3 и ядра на linux-x86_64.
///
/// Эти числа НЕ выведены из документации и не восстановлены по памяти: их
/// напечатал компилятор на целевой платформе (tools/fuse-abi-probe.c), вывод
/// лежит фикстурой рядом с тестами, а тест сверяет каждое поле отсюда с ней.
/// Ошибка в смещении не даёт ошибки компиляции — она даёт вызов не той
/// функции или порчу чужого поля, поэтому источником истины здесь может быть
/// только сам ABI.
///
/// Наглядно, почему это не педантизм: в struct stat поле st_nlink лежит
/// ПЕРЕД st_mode (16 против 24), а fh в fuse_file_info — на 16, а не на 8.
/// Оба «очевидных» порядка неверны.</summary>
internal static class FuseAbi
{
    /// <summary>Разрядность и версия, для которых снята раскладка.</summary>
    public const int ProbedFuseVersion = 314;

    // struct fuse_operations — таблица указателей на наши функции
    public const int OperationsSize = 336;
    public const int OpGetattr = 0;
    public const int OpReadlink = 8;
    public const int OpMknod = 16;
    public const int OpMkdir = 24;
    public const int OpUnlink = 32;
    public const int OpRmdir = 40;
    public const int OpSymlink = 48;
    public const int OpRename = 56;
    public const int OpLink = 64;
    public const int OpChmod = 72;
    public const int OpChown = 80;
    public const int OpTruncate = 88;
    public const int OpOpen = 96;
    public const int OpRead = 104;
    public const int OpWrite = 112;
    public const int OpStatfs = 120;
    public const int OpFlush = 128;
    public const int OpRelease = 136;
    public const int OpFsync = 144;
    public const int OpOpendir = 184;
    public const int OpReaddir = 192;
    public const int OpReleasedir = 200;
    public const int OpInit = 216;
    public const int OpDestroy = 224;
    public const int OpAccess = 232;
    public const int OpCreate = 240;
    public const int OpUtimens = 256;

    // struct stat
    public const int StatSize = 144;
    public const int StatIno = 8;
    public const int StatNlink = 16;
    public const int StatMode = 24;
    public const int StatUid = 28;
    public const int StatGid = 32;
    public const int StatSizeField = 48;
    public const int StatBlksize = 56;
    public const int StatBlocks = 64;
    public const int StatAtim = 72;
    public const int StatMtim = 88;
    public const int StatCtim = 104;

    // struct fuse_file_info
    public const int FileInfoSize = 40;
    public const int FileInfoFlags = 0;
    public const int FileInfoFh = 16;

    // struct statvfs
    public const int StatvfsSize = 112;
    public const int StatvfsBsize = 0;
    public const int StatvfsFrsize = 8;
    public const int StatvfsBlocks = 16;
    public const int StatvfsBfree = 24;
    public const int StatvfsBavail = 32;
    public const int StatvfsFiles = 40;
    public const int StatvfsFfree = 48;
    public const int StatvfsNamemax = 80;

    // struct fuse_args
    public const int ArgsSize = 24;
    public const int ArgsArgc = 0;
    public const int ArgsArgv = 8;
    public const int ArgsAllocated = 16;

    // struct fuse_context — через него колбэк находит свой том
    public const int ContextSize = 40;
    public const int ContextPrivateData = 24;

    // struct timespec
    public const int TimespecSize = 16;
    public const int TimespecSec = 0;
    public const int TimespecNsec = 8;
}

/// <summary>Биты st_mode. Значения зафиксированы POSIX и одинаковы на всех
/// Linux-архитектурах, в отличие от смещений выше.</summary>
internal static class FileMode
{
    public const uint Directory = 0x4000;   // S_IFDIR
    public const uint Regular = 0x8000;     // S_IFREG
    public const uint Symlink = 0xA000;     // S_IFLNK

    public const uint ReadOwner = 0x100, WriteOwner = 0x80, ExecOwner = 0x40;
    public const uint ReadGroup = 0x20, ExecGroup = 0x8;
    public const uint ReadOther = 0x4, ExecOther = 0x1;

    public const uint FileReadOnly = ReadOwner | ReadGroup | ReadOther;                // 0444
    public const uint FileReadWrite = FileReadOnly | WriteOwner;                       // 0644
    public const uint DirReadOnly = ReadOwner | ExecOwner | ReadGroup | ExecGroup
                                    | ReadOther | ExecOther;                           // 0555
    public const uint DirReadWrite = DirReadOnly | WriteOwner;                         // 0755
}

/// <summary>Коды ошибок, которые адаптер возвращает ядру. Возвращаются со
/// знаком минус: таково соглашение высокоуровневого API libfuse.</summary>
internal static class Errno
{
    public const int NoEnt = 2;      // ENOENT
    public const int IO = 5;         // EIO
    public const int BadF = 9;       // EBADF
    public const int Access = 13;    // EACCES
    public const int Exist = 17;     // EEXIST
    public const int NotDir = 20;    // ENOTDIR
    public const int IsDir = 21;     // EISDIR
    public const int Inval = 22;     // EINVAL
    public const int NoSpc = 28;     // ENOSPC
    public const int Rofs = 30;      // EROFS
    public const int NameTooLong = 36; // ENAMETOOLONG
    public const int NoSys = 38;     // ENOSYS
    public const int NotEmpty = 39;  // ENOTEMPTY
    public const int NotSup = 95;    // ENOTSUP
}
