namespace Gitfs.Vfs;

public enum OpenMode
{
    Read = 0,
    Write = 1,
}

/// <summary>Сведения о томе для адаптера (спека §3.4): метка
/// «gitfs: &lt;имя репозитория&gt;», ёмкость — суммарный размер .git/objects,
/// свободного места нет.</summary>
public readonly record struct VolumeInfo(string Label, long TotalBytes, long FreeBytes);

/// <summary>Граница переносимости (спека §11). Всё ниже неё не знает о
/// файловой системе и проверяется без монтирования; выше — только адаптеры.
///
/// Контракт:
/// — методы не бросают исключений, результат всегда GitfsResult;
/// — Read может вернуть меньше запрошенного, но никогда не читает за
///   пределами размера, объявленного в Lookup;
/// — хендл владеет разрешённым узлом и, при наличии, потоком; после Close
///   использование хендла — ошибка программиста, а не пользователя;
/// — List возвращает ленивую последовательность: commits/ не материализуется.</summary>
public interface IMountTarget : IDisposable
{
    GitfsResult<NodeInfo> Lookup(string path);
    GitfsResult<IEnumerable<DirEntry>> List(string path);
    GitfsResult<FileHandle> Open(string path, OpenMode mode);
    GitfsResult<int> Read(FileHandle handle, long offset, Span<byte> buffer);
    GitfsResult<int> Write(FileHandle handle, long offset, ReadOnlySpan<byte> data);
    /// <summary>Усечение/расширение: перезапись файла обязана отбросить хвост
    /// прежнего содержимого.</summary>
    GitfsResult<Unit> SetLength(FileHandle handle, long length);
    /// <summary>Скрывает узел надгробием песочницы; репозиторий не меняется.</summary>
    GitfsResult<Unit> Delete(string path);
    GitfsResult<Unit> Close(FileHandle handle);
    VolumeInfo GetVolumeInfo();
}
