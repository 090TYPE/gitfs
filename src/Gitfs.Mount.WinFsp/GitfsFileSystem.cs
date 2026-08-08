using System.Runtime.InteropServices;
using Fsp;
using Gitfs.Vfs;
using FspFileInfo = Fsp.Interop.FileInfo;
using FspVolumeInfo = Fsp.Interop.VolumeInfo;

namespace Gitfs.Mount.WinFsp;

/// <summary>Адаптер WinFsp: переводит вызовы ОС в IMountTarget и коды ошибок
/// в NTSTATUS (спека §12).
///
/// Правило границы: необработанное исключение в колбэке файловой системы
/// вешает Проводник целиком, поэтому каждый колбэк — внешний try/catch
/// с трансляцией в код и записью в лог.</summary>
public sealed class GitfsFileSystem : FileSystemBase
{
    private const uint FileAttributeDirectory = 0x10;
    private const uint FileAttributeReadonly = 0x01;
    private const uint FileAttributeReparsePoint = 0x400;
    private const uint FileAttributeNormal = 0x80;

    private readonly IMountTarget _target;
    private readonly Action<string> _log;

    public GitfsFileSystem(IMountTarget target, Action<string>? log = null)
    {
        _target = target;
        _log = log ?? (_ => { });
    }

    // ---------- том ----------

    public override int Init(object host0)
    {
        var host = (FileSystemHost)host0;
        var info = _target.GetVolumeInfo();
        host.SectorSize = 4096;
        host.SectorsPerAllocationUnit = 1;
        host.MaxComponentLength = 255;
        host.FileInfoTimeout = 1000;
        // Проводник и большинство программ ожидают именно такой том:
        // регистронезависимый поиск при сохранении регистра имён (§12)
        host.CaseSensitiveSearch = false;
        host.CasePreservedNames = true;
        host.UnicodeOnDisk = true;
        host.PersistentAcls = false;
        host.ReparsePoints = false;
        host.NamedStreams = false;
        host.ExtendedAttributes = false;
        host.VolumeCreationTime = 0;
        host.VolumeSerialNumber = (uint)info.Label.GetHashCode();
        host.FileSystemName = "gitfs";
        return STATUS_SUCCESS;
    }

    public override int GetVolumeInfo(out FspVolumeInfo volumeInfo)
    {
        volumeInfo = default;
        try
        {
            var info = _target.GetVolumeInfo();
            volumeInfo.TotalSize = (ulong)info.TotalBytes;
            volumeInfo.FreeSize = (ulong)info.FreeBytes;
            volumeInfo.SetVolumeLabel(info.Label);
            return STATUS_SUCCESS;
        }
        catch (Exception e)
        {
            return Fail("GetVolumeInfo", e);
        }
    }

    // ---------- разрешение имён ----------

    public override int GetSecurityByName(string fileName, out uint fileAttributes,
        ref byte[] securityDescriptor)
    {
        fileAttributes = 0;
        try
        {
            var result = _target.Lookup(Normalize(fileName));
            if (!result.TryGet(out var node)) return Translate(result.Error);
            fileAttributes = Attributes(node);
            return STATUS_SUCCESS;
        }
        catch (Exception e)
        {
            return Fail("GetSecurityByName", e);
        }
    }

    // ---------- открытие и чтение ----------

    private const uint FileWriteData = 0x0002;
    private const uint FileAppendData = 0x0004;
    private const uint GenericWrite = 0x40000000;
    private const uint Delete = 0x00010000;

    public override int Open(string fileName, uint createOptions, uint grantedAccess,
        out object? fileNode, out object? fileDesc, out FspFileInfo fileInfo,
        out string? normalizedName)
    {
        fileNode = null;
        fileDesc = null;
        fileInfo = default;
        normalizedName = null;
        try
        {
            var path = Normalize(fileName);
            // приложения (Word, Excel, IDE) просят запись даже для просмотра —
            // отдаём им overlay-хендл, а не отказ (спека §10)
            var wantsWrite = (grantedAccess & (FileWriteData | FileAppendData | GenericWrite)) != 0;
            var opened = _target.Open(path, wantsWrite ? OpenMode.Write : OpenMode.Read);
            if (!opened.TryGet(out var handle) && wantsWrite)
                opened = _target.Open(path, OpenMode.Read); // том смонтирован read-only
            if (!opened.TryGet(out handle)) return Translate(opened.Error);
            fileDesc = handle;
            fileInfo = ToFspInfo(handle.Info);
            return STATUS_SUCCESS;
        }
        catch (Exception e)
        {
            return Fail("Open", e);
        }
    }

    public override int Write(object fileNode, object fileDesc, IntPtr buffer, ulong offset,
        uint length, bool writeToEndOfFile, bool constrainedIo,
        out uint bytesTransferred, out FspFileInfo fileInfo)
    {
        bytesTransferred = 0;
        fileInfo = default;
        try
        {
            if (fileDesc is not FileHandle handle) return STATUS_INVALID_PARAMETER;
            var target = writeToEndOfFile ? (ulong)handle.Info.Size : offset;
            var data = new byte[length];
            Marshal.Copy(buffer, data, 0, (int)length);
            var written = _target.Write(handle, (long)target, data);
            if (!written.TryGet(out var count)) return Translate(written.Error);
            bytesTransferred = (uint)count;
            fileInfo = ToFspInfo(handle.Info);
            return STATUS_SUCCESS;
        }
        catch (Exception e)
        {
            return Fail("Write", e);
        }
    }

    public override int SetFileSize(object fileNode, object fileDesc, ulong newSize,
        bool setAllocationSize, out FspFileInfo fileInfo)
    {
        fileInfo = default;
        try
        {
            if (fileDesc is not FileHandle handle) return STATUS_INVALID_PARAMETER;
            if (setAllocationSize) { fileInfo = ToFspInfo(handle.Info); return STATUS_SUCCESS; }
            var result = _target.SetLength(handle, (long)newSize);
            if (!result.IsOk) return Translate(result.Error);
            fileInfo = ToFspInfo(handle.Info);
            return STATUS_SUCCESS;
        }
        catch (Exception e)
        {
            return Fail("SetFileSize", e);
        }
    }

    public override int CanDelete(object fileNode, object fileDesc, string fileName)
    {
        try
        {
            // проверка возможности: настоящее скрытие делает Cleanup
            var lookup = _target.Lookup(Normalize(fileName));
            return lookup.IsOk ? STATUS_SUCCESS : Translate(lookup.Error);
        }
        catch (Exception e)
        {
            return Fail("CanDelete", e);
        }
    }

    public override void Cleanup(object fileNode, object fileDesc, string fileName, uint flags)
    {
        try
        {
            if ((flags & CleanupDelete) == 0) return;
            var result = _target.Delete(Normalize(fileName));
            if (!result.IsOk) _log($"Delete refused for {fileName}: {result.Error}");
        }
        catch (Exception e)
        {
            _log($"Cleanup failed: {e.GetType().Name}: {e.Message}");
        }
    }

    public override void Close(object fileNode, object fileDesc)
    {
        try
        {
            if (fileDesc is FileHandle handle) _target.Close(handle);
        }
        catch (Exception e)
        {
            _log($"Close failed: {e.GetType().Name}: {e.Message}");
        }
    }

    public override int Read(object fileNode, object fileDesc, IntPtr buffer,
        ulong offset, uint length, out uint bytesTransferred)
    {
        bytesTransferred = 0;
        try
        {
            if (fileDesc is not FileHandle handle) return STATUS_INVALID_PARAMETER;
            if (handle.IsDirectory) return STATUS_FILE_IS_A_DIRECTORY;
            if (offset >= (ulong)handle.Info.Size) return STATUS_END_OF_FILE;

            var want = (int)Math.Min(length, (ulong)handle.Info.Size - offset);
            var scratch = new byte[want];
            var read = _target.Read(handle, (long)offset, scratch);
            if (!read.TryGet(out var count)) return Translate(read.Error);
            if (count == 0) return STATUS_END_OF_FILE;

            Marshal.Copy(scratch, 0, buffer, count);
            bytesTransferred = (uint)count;
            return STATUS_SUCCESS;
        }
        catch (Exception e)
        {
            return Fail("Read", e);
        }
    }

    public override int GetFileInfo(object fileNode, object fileDesc, out FspFileInfo fileInfo)
    {
        fileInfo = default;
        try
        {
            if (fileDesc is not FileHandle handle) return STATUS_INVALID_PARAMETER;
            fileInfo = ToFspInfo(handle.Info);
            return STATUS_SUCCESS;
        }
        catch (Exception e)
        {
            return Fail("GetFileInfo", e);
        }
    }

    // ---------- перечисление ----------

    public override bool ReadDirectoryEntry(object fileNode, object fileDesc,
        string? pattern, string? marker, ref object? context,
        out string? fileName, out FspFileInfo fileInfo)
    {
        fileName = null;
        fileInfo = default;
        try
        {
            // контекст — курсор по материализованному листингу; WinFsp зовёт
            // этот колбэк по одной записи и ждёт false как признак конца
            if (context is not DirectoryCursor cursor)
            {
                if (fileDesc is not FileHandle handle || !handle.IsDirectory) return false;
                var listed = _target.List(handle.Path);
                if (!listed.TryGet(out var entries)) return false;
                cursor = new DirectoryCursor(entries.ToList(), marker);
                context = cursor;
            }
            if (!cursor.MoveNext(out var entry)) return false;
            fileName = entry.Name;
            fileInfo = ToFspInfo(entry.Info);
            return true;
        }
        catch (Exception e)
        {
            _log($"ReadDirectoryEntry failed: {e.GetType().Name}: {e.Message}");
            return false;
        }
    }

    private sealed class DirectoryCursor
    {
        private readonly List<DirEntry> _entries;
        private int _index;

        public DirectoryCursor(List<DirEntry> entries, string? marker)
        {
            _entries = entries;
            if (marker is not null)
            {
                // продолжение перечисления после указанной записи
                var at = entries.FindIndex(e =>
                    string.Equals(e.Name, marker, StringComparison.OrdinalIgnoreCase));
                _index = at < 0 ? entries.Count : at + 1;
            }
        }

        public bool MoveNext(out DirEntry entry)
        {
            if (_index >= _entries.Count)
            {
                entry = default;
                return false;
            }
            entry = _entries[_index++];
            return true;
        }
    }

    // ---------- трансляция ----------

    /// <summary>WinFsp даёт пути с обратными слэшами и ведущим «\».</summary>
    private static string Normalize(string fileName) =>
        string.IsNullOrEmpty(fileName) ? "/" : fileName.Replace('\\', '/');

    private static uint Attributes(in NodeInfo node) => node.Kind switch
    {
        NodeKind.Directory => FileAttributeDirectory | FileAttributeReadonly,
        NodeKind.Submodule => FileAttributeDirectory | FileAttributeReadonly,
        NodeKind.Symlink => FileAttributeNormal | FileAttributeReadonly,
        _ => FileAttributeNormal | FileAttributeReadonly,
    };

    private static FspFileInfo ToFspInfo(in NodeInfo node)
    {
        var time = (ulong)node.Timestamp.UtcDateTime.ToFileTimeUtc();
        return new FspFileInfo
        {
            FileAttributes = Attributes(node),
            FileSize = (ulong)node.Size,
            AllocationSize = (ulong)((node.Size + 4095) / 4096 * 4096),
            CreationTime = time,
            LastAccessTime = time,
            LastWriteTime = time,
            ChangeTime = time,
        };
    }

    /// <summary>Таблица §12: каждый код границы имеет свой NTSTATUS.</summary>
    internal static int Translate(GitfsError error) => error switch
    {
        GitfsError.None => STATUS_SUCCESS,
        GitfsError.NotFound => STATUS_OBJECT_NAME_NOT_FOUND,
        GitfsError.AmbiguousPrefix => STATUS_OBJECT_NAME_NOT_FOUND,
        GitfsError.NotADirectory => STATUS_NOT_A_DIRECTORY,
        GitfsError.CorruptObject => STATUS_DEVICE_DATA_ERROR,
        GitfsError.IoError => STATUS_IO_DEVICE_ERROR,
        GitfsError.AccessDenied => STATUS_ACCESS_DENIED,
        GitfsError.NotSupported => STATUS_NOT_SUPPORTED,
        GitfsError.TooLarge => STATUS_FILE_TOO_LARGE,
        _ => STATUS_IO_DEVICE_ERROR,
    };

    private int Fail(string callback, Exception e)
    {
        _log($"{callback} failed: {e.GetType().Name}: {e.Message}");
        return STATUS_IO_DEVICE_ERROR;
    }
}
