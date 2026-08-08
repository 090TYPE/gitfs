namespace Gitfs.Vfs;

/// <summary>Коды ошибок границы (спека §11). NotADirectory добавлен по итогам
/// ревью M2: ReadDirectory на файле обязан быть ошибкой, а не пустотой.</summary>
public enum GitfsError
{
    None = 0,
    NotFound,
    NotADirectory,
    AmbiguousPrefix,
    CorruptObject,
    IoError,
    AccessDenied,
    NotSupported,
    TooLarge,
}

/// <summary>Пустой результат для операций без значения.</summary>
public readonly struct Unit
{
    public static readonly Unit Value;
}

/// <summary>Результат операции границы: методы IMountTarget НЕ бросают
/// исключений — результат всегда код (спека §11).</summary>
public readonly struct GitfsResult<T>
{
    public GitfsError Error { get; }
    private readonly T _value;

    private GitfsResult(T value, GitfsError error)
    {
        _value = value;
        Error = error;
    }

    public bool IsOk => Error == GitfsError.None;

    public T Value => IsOk
        ? _value
        : throw new InvalidOperationException($"result carries error {Error}");

    public static GitfsResult<T> Ok(T value) => new(value, GitfsError.None);
    public static GitfsResult<T> Fail(GitfsError error) => new(default!, error);

    public bool TryGet(out T value)
    {
        value = _value;
        return IsOk;
    }
}
