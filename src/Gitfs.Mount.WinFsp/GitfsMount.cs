using Fsp;
using Gitfs.Vfs;

namespace Gitfs.Mount.WinFsp;

/// <summary>Живое монтирование: держит хост WinFsp и цель, снимает том
/// при Dispose. Всё, что нужно CLI, — конструктор и Dispose.</summary>
public sealed class GitfsMount : IDisposable
{
    private readonly FileSystemHost _host;
    private readonly IMountTarget _target;
    private bool _mounted;

    public string MountPoint { get; }

    private GitfsMount(FileSystemHost host, IMountTarget target, string mountPoint)
    {
        _host = host;
        _target = target;
        MountPoint = mountPoint;
        _mounted = true;
    }

    /// <param name="mountPoint">Буква вида "G:" или путь к пустой папке.</param>
    /// <summary>Точка монтирования проверяется ДО того, как попадёт в
    /// драйвер. Без этой проверки `gitfs mount . 1:` не выдавал ошибку — он
    /// ронял процесс: FspFileSystemSetMountPointEx уходил в нарушение
    /// доступа, а AccessViolationException в .NET не ловится, так что до
    /// сообщения дело не доходило вовсе. Пользователь видел «Fatal error.
    /// Attempted to read or write protected memory».
    ///
    /// Занятую букву тоже не пропускаем: попытка встать поверх системного
    /// диска подвешивает вызов, и снаружи это выглядит как зависший gitfs
    /// (найдено собственным тестом, подвесившим CI на сорок минут).</summary>
    private static void ValidateMountPoint(string mountPoint)
    {
        if (string.IsNullOrWhiteSpace(mountPoint))
            throw new MountException("a mount point is required, for example G:");

        // Буква диска: ровно «X:», и X — латинская буква
        if (mountPoint.Length == 2 && mountPoint[1] == ':')
        {
            var letter = char.ToUpperInvariant(mountPoint[0]);
            if (letter is < 'A' or > 'Z')
                throw new MountException(
                    $"'{mountPoint}' is not a drive letter; use something like G:");
            if (OperatingSystem.IsWindows()
                && DriveInfo.GetDrives().Any(d => char.ToUpperInvariant(d.Name[0]) == letter))
                throw new MountException(
                    $"{mountPoint} is already in use; gitfs doctor lists the free letters");
            return;
        }

        // Иначе — каталог: WinFsp умеет и так, но он должен существовать
        // и быть пустым
        if (!Directory.Exists(mountPoint))
            throw new MountException(
                $"'{mountPoint}' is neither a drive letter like G: nor an existing directory");
        if (Directory.EnumerateFileSystemEntries(mountPoint).Any())
            throw new MountException($"{mountPoint} is not empty; a mount point must be empty");
    }

    /// <exception cref="MountException">Драйвер отсутствует или точка занята.</exception>
    public static GitfsMount Mount(IMountTarget target, string mountPoint,
        Action<string>? log = null, bool readOnly = true)
    {
        ValidateMountPoint(mountPoint);

        var fs = new GitfsFileSystem(target, log, readOnly);
        var host = new FileSystemHost(fs);
        int status;
        try
        {
            status = host.Mount(mountPoint, null, false, 0);
        }
        // «не установлен» — только один из способов не загрузиться. Драйвер
        // может стоять, а версия его библиотеки не совпасть с ожидаемой —
        // и тогда совет «поставьте WinFsp» уводит в сторону от настоящей
        // причины, которую знает сама загрузка. Показываем её.
        catch (DllNotFoundException e)
        {
            throw new MountException(
                $"WinFsp did not load: {e.Message.TrimEnd('.')}. "
                + "Install or repair it from winfsp.dev/rel.", e);
        }
        catch (TypeInitializationException e)
        {
            var reason = (e.InnerException ?? e).Message.TrimEnd('.');
            throw new MountException(
                $"WinFsp did not load: {reason}. Run gitfs doctor to compare versions.", e);
        }
        if (status != FileSystemBase.STATUS_SUCCESS)
            throw new MountException($"WinFsp refused to mount {mountPoint} (status 0x{status:X8}).");
        return new GitfsMount(host, target, mountPoint);
    }

    public void Dispose()
    {
        if (_mounted)
        {
            _host.Unmount();
            _mounted = false;
        }
        _target.Dispose();
    }
}

public sealed class MountException : Exception
{
    public MountException(string message, Exception? inner = null) : base(message, inner) { }
}
