using System.Text;

namespace Gitfs.Core;

/// <summary>Автор/коммиттер: "Имя &lt;email&gt; unix-время ±HHMM".</summary>
public readonly struct Signature
{
    public string Name { get; }
    public string Email { get; }
    public DateTimeOffset When { get; }

    public Signature(string name, string email, DateTimeOffset when)
    {
        Name = name;
        Email = email;
        When = when;
    }

    public static Signature Parse(ReadOnlySpan<char> text)
    {
        var lt = text.LastIndexOf('<');
        var gt = text.LastIndexOf('>');
        if (lt < 0 || gt < lt)
            throw new InvalidDataException($"malformed signature: '{text}'");
        var name = text[..lt].TrimEnd().ToString();
        var email = text[(lt + 1)..gt].ToString();

        var rest = text[(gt + 1)..].Trim();
        var space = rest.IndexOf(' ');
        if (space < 0 || !long.TryParse(rest[..space], out var unix))
            throw new InvalidDataException($"malformed signature timestamp: '{text}'");
        var tz = rest[(space + 1)..];
        if (tz.Length != 5 || (tz[0] != '+' && tz[0] != '-'))
            throw new InvalidDataException($"malformed signature timezone: '{text}'");
        var offset = new TimeSpan(int.Parse(tz[1..3]), int.Parse(tz[3..5]), 0);
        if (tz[0] == '-') offset = -offset;

        return new Signature(name, email,
            DateTimeOffset.FromUnixTimeSeconds(unix).ToOffset(offset));
    }
}

/// <summary>Разобранный commit. Заголовки до пустой строки: tree, parent×N,
/// author, committer; незнакомые заголовки (gpgsig, encoding, mergetag) и их
/// continuation-строки (ведущий пробел) пропускаются — это делает парсер
/// устойчивым к подписанным коммитам. Сообщение — остаток байт-в-байт.</summary>
public sealed class CommitObject
{
    public ObjectId Id { get; }
    public ObjectId Tree { get; }
    public IReadOnlyList<ObjectId> Parents { get; }
    public Signature Author { get; }
    public Signature Committer { get; }
    public string Message { get; }

    private CommitObject(ObjectId id, ObjectId tree, List<ObjectId> parents,
        Signature author, Signature committer, string message)
    {
        Id = id;
        Tree = tree;
        Parents = parents;
        Author = author;
        Committer = committer;
        Message = message;
    }

    public static CommitObject Parse(ObjectId id, ReadOnlySpan<byte> data)
    {
        ObjectId? tree = null;
        var parents = new List<ObjectId>();
        Signature? author = null, committer = null;

        var pos = 0;
        while (pos < data.Length)
        {
            var nl = data[pos..].IndexOf((byte)'\n');
            if (nl < 0) throw new InvalidDataException($"commit {id}: header without newline");
            var line = data.Slice(pos, nl);
            pos += nl + 1;

            if (line.Length == 0) break;               // пустая строка — дальше сообщение
            if (line[0] == (byte)' ') continue;        // continuation (gpgsig и т.п.)

            var space = line.IndexOf((byte)' ');
            if (space < 0) throw new InvalidDataException($"commit {id}: malformed header");
            var key = line[..space];
            var value = Encoding.UTF8.GetString(line[(space + 1)..]);

            if (key.SequenceEqual("tree"u8)) tree = ObjectId.Parse(value);
            else if (key.SequenceEqual("parent"u8)) parents.Add(ObjectId.Parse(value));
            else if (key.SequenceEqual("author"u8)) author = Signature.Parse(value);
            else if (key.SequenceEqual("committer"u8)) committer = Signature.Parse(value);
            // прочие заголовки — молча пропускаем
        }

        if (tree is null || author is null || committer is null)
            throw new InvalidDataException($"commit {id}: missing tree/author/committer");

        var message = Encoding.UTF8.GetString(data[pos..]);
        return new CommitObject(id, tree.Value, parents, author.Value, committer.Value, message);
    }
}
