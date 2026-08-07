using System.IO.Compression;

namespace Gitfs.Core.Objects;

/// <summary>Читает loose-объекты: .git/objects/aa/bbbb… — zlib-поток
/// с телом "&lt;type&gt; &lt;size&gt;\0&lt;content&gt;". Заголовок разбирается побайтово,
/// чтобы поток остался спозиционированным ровно на начале содержимого.</summary>
public sealed class LooseObjectReader
{
    private const int MaxHeaderLength = 32; // "commit 18446744073709551615\0"

    private readonly string _objectsDir;

    public LooseObjectReader(string gitDir) =>
        _objectsDir = Path.Combine(gitDir, "objects");

    private string PathFor(in ObjectId id)
    {
        var hex = id.ToString();
        return Path.Combine(_objectsDir, hex[..2], hex[2..]);
    }

    public bool Contains(in ObjectId id) => File.Exists(PathFor(id));

    public bool TryGetHeader(in ObjectId id, out GitObjectType type, out long size)
    {
        using var stream = TryOpenStream(id, out type, out size);
        return stream is not null;
    }

    /// <summary>Открывает распакованное тело объекта (без заголовка).
    /// null — объекта нет среди loose. Вызывающий обязан Dispose.</summary>
    public Stream? TryOpenStream(in ObjectId id, out GitObjectType type, out long size)
    {
        type = default;
        size = 0;
        var path = PathFor(id);
        FileStream file;
        try
        {
            file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }

        var zlib = new ZLibStream(file, CompressionMode.Decompress); // владеет file
        if (TryParseHeader(zlib, out type, out size)) return zlib;
        zlib.Dispose();
        throw new InvalidDataException($"corrupt loose object header: {id}");
    }

    public byte[] ReadAll(in ObjectId id, long maxBytes) =>
        TryReadAll(id, maxBytes)
        ?? throw new FileNotFoundException($"loose object not found: {id}");

    /// <summary>null — объекта нет среди loose. Одна операция без Contains+Read:
    /// git gc может убрать loose-файл между двумя вызовами.</summary>
    public byte[]? TryReadAll(in ObjectId id, long maxBytes)
    {
        using var stream = TryOpenStream(id, out _, out var size);
        if (stream is null) return null;
        if (size > maxBytes)
            throw new InvalidDataException($"object {id} is {size} bytes, over the {maxBytes} limit");
        var buffer = new byte[size];
        var read = 0;
        while (read < buffer.Length)
        {
            var n = stream.Read(buffer, read, buffer.Length - read);
            if (n == 0) throw new InvalidDataException($"object {id} truncated at {read}/{size}");
            read += n;
        }
        if (stream.ReadByte() != -1)
            throw new InvalidDataException($"object {id} longer than declared {size}");
        return buffer;
    }

    private static bool TryParseHeader(Stream stream, out GitObjectType type, out long size)
    {
        type = default;
        size = 0;
        Span<byte> header = stackalloc byte[MaxHeaderLength];
        var len = 0;
        while (len < MaxHeaderLength)
        {
            var b = stream.ReadByte();
            if (b < 0) return false;
            if (b == 0) break;                    // NUL — конец заголовка
            header[len++] = (byte)b;
        }
        if (len == MaxHeaderLength) return false; // NUL не встретился

        var text = System.Text.Encoding.ASCII.GetString(header[..len]);
        var space = text.IndexOf(' ');
        if (space < 0 || !long.TryParse(text[(space + 1)..], out size) || size < 0)
            return false;
        type = text[..space] switch
        {
            "commit" => GitObjectType.Commit,
            "tree" => GitObjectType.Tree,
            "blob" => GitObjectType.Blob,
            "tag" => GitObjectType.Tag,
            _ => default,
        };
        return type != default;
    }
}
