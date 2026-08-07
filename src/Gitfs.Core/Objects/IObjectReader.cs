namespace Gitfs.Core.Objects;

/// <summary>Контракт доступа к объектам (спека §6.1). TryGetHeader отделён от
/// чтения тела: GetAttr/Lookup вызываются на порядок чаще Read, и им нужен
/// только размер.</summary>
public interface IObjectReader
{
    bool TryGetHeader(in ObjectId id, out GitObjectType type, out long size);

    /// <summary>Распакованное тело объекта. Для packed-дельт в v1 —
    /// материализация (долг M1b, записан). Отсутствующий объект —
    /// FileNotFoundException.</summary>
    Stream OpenStream(in ObjectId id);

    byte[] ReadAll(in ObjectId id, long maxBytes);
}
