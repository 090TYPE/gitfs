namespace Gitfs.Core.Refs;

/// <summary>Ссылка: имя, цель и — для аннотированных тегов из packed-refs —
/// разыменованный коммит (строки "^&lt;sha&gt;").</summary>
public sealed record RefEntry(string Name, ObjectId Target, ObjectId? Peeled);

/// <summary>Однократный снимок ссылок репозитория: packed-refs, затем loose
/// поверх (loose всегда новее), затем HEAD. Иммутабелен — это будущая
/// составляющая RepoSnapshot (спека §8).</summary>
public sealed class RefStore
{
    private readonly Dictionary<string, RefEntry> _refs;

    public string? HeadSymref { get; }
    public ObjectId? HeadTarget { get; }
    public IReadOnlyDictionary<string, RefEntry> All => _refs;

    private RefStore(Dictionary<string, RefEntry> refs, string? headSymref, ObjectId? headTarget)
    {
        _refs = refs;
        HeadSymref = headSymref;
        HeadTarget = headTarget;
    }

    public bool TryResolve(string name, out RefEntry entry) =>
        _refs.TryGetValue(name, out entry!);

    public static RefStore Load(string gitDir)
    {
        var refs = new Dictionary<string, RefEntry>(StringComparer.Ordinal);

        LoadPackedRefs(Path.Combine(gitDir, "packed-refs"), refs);
        LoadLooseRefs(gitDir, refs);

        string? headSymref = null;
        ObjectId? headTarget = null;
        var headPath = Path.Combine(gitDir, "HEAD");
        if (File.Exists(headPath))
        {
            var head = File.ReadAllText(headPath).Trim();
            if (head.StartsWith("ref: ", StringComparison.Ordinal))
            {
                headSymref = head[5..].Trim();
                if (refs.TryGetValue(headSymref, out var target)) headTarget = target.Target;
            }
            else if (ObjectId.TryParse(head, out var detached))
            {
                headTarget = detached; // detached HEAD
            }
        }
        return new RefStore(refs, headSymref, headTarget);
    }

    private static void LoadPackedRefs(string path, Dictionary<string, RefEntry> refs)
    {
        if (!File.Exists(path)) return;
        string? lastName = null;
        foreach (var line in File.ReadLines(path))
        {
            if (line.Length == 0 || line[0] == '#') continue;
            if (line[0] == '^')
            {
                // peel-строка относится к предыдущей ссылке (аннотированный тег)
                if (lastName is not null && ObjectId.TryParse(line.AsSpan(1).Trim(), out var peeled))
                    refs[lastName] = refs[lastName] with { Peeled = peeled };
                continue;
            }
            var space = line.IndexOf(' ');
            if (space < 0) continue;
            if (!ObjectId.TryParse(line.AsSpan(0, space), out var target)) continue;
            var name = line[(space + 1)..].Trim();
            refs[name] = new RefEntry(name, target, null);
            lastName = name;
        }
    }

    private static void LoadLooseRefs(string gitDir, Dictionary<string, RefEntry> refs)
    {
        var refsRoot = Path.Combine(gitDir, "refs");
        if (!Directory.Exists(refsRoot)) return;
        foreach (var file in Directory.EnumerateFiles(refsRoot, "*", SearchOption.AllDirectories))
        {
            // *.lock — транзиентные файлы git во время записи ссылки; в них
            // лежит валидный sha, и без фильтра появлялась ветка «main.lock»
            if (file.EndsWith(".lock", StringComparison.OrdinalIgnoreCase)) continue;
            var name = Path.GetRelativePath(gitDir, file).Replace('\\', '/');
            var content = File.ReadAllText(file).Trim();
            if (ObjectId.TryParse(content, out var target))
                refs[name] = new RefEntry(name, target, null); // loose поверх packed
            // "ref:"-симрефы вне HEAD в v1 не поддерживаются (git их почти не создаёт)
        }
    }
}
