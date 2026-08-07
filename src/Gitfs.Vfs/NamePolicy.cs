using System.Text;

namespace Gitfs.Vfs;

public enum NamePolicyKind
{
    /// <summary>Правила текущей платформы: Windows — все три, Linux — ни одного,
    /// macOS — только коллизии регистра (спека §3.3).</summary>
    Native,
    /// <summary>Правила Windows всегда — дерево одинаково на всех ОС.</summary>
    Portable,
}

/// <summary>Отображаемое имя записи и её исходное имя в git.
/// Преобразования применяются только к отображению; чтение объекта
/// всегда идёт по исходному имени (спека §3.3).</summary>
public readonly record struct DisplayName(string Display, string GitName);

/// <summary>Три правила приведения git-имён к именам, допустимым в целевой ФС:
/// 1) недопустимые символы и '%' → %XX (обратимо; завершающие точка/пробел тоже);
/// 2) зарезервированные имена устройств → суффикс %RES к базе имени;
/// 3) коллизии регистра → ~2, ~3 в порядке обхода git-дерева (детерминирован).</summary>
public sealed class NamePolicy
{
    public static NamePolicy Windows { get; } = new(encodeInvalid: true, guardReserved: true, foldCase: true);
    public static NamePolicy Posix { get; } = new(encodeInvalid: false, guardReserved: false, foldCase: false);
    public static NamePolicy MacOs { get; } = new(encodeInvalid: false, guardReserved: false, foldCase: true);

    public static NamePolicy For(NamePolicyKind kind) => kind switch
    {
        NamePolicyKind.Portable => Windows,
        _ => OperatingSystem.IsWindows() ? Windows
           : OperatingSystem.IsMacOS() ? MacOs
           : Posix,
    };

    private static readonly HashSet<string> Reserved = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    private readonly bool _encodeInvalid;
    private readonly bool _guardReserved;
    private readonly bool _foldCase;

    private NamePolicy(bool encodeInvalid, bool guardReserved, bool foldCase)
    {
        _encodeInvalid = encodeInvalid;
        _guardReserved = guardReserved;
        _foldCase = foldCase;
    }

    public string EncodeName(string gitName)
    {
        var name = gitName;
        if (_encodeInvalid) name = EncodeChars(name);
        if (_guardReserved) name = GuardReserved(name);
        return name;
    }

    /// <summary>Листинг директории: EncodeName + суффиксы ~N для коллизий
    /// регистра. Порядок входа — порядок git-дерева, он же даёт детерминизм.</summary>
    public IReadOnlyList<DisplayName> EncodeListing(IEnumerable<string> gitNames)
    {
        var result = new List<DisplayName>();
        var seen = _foldCase ? new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) : null;
        foreach (var gitName in gitNames)
        {
            var display = EncodeName(gitName);
            if (seen is not null)
            {
                if (seen.TryGetValue(display, out var n))
                {
                    n++;
                    seen[display] = n;
                    display = $"{display}~{n}";
                }
                else
                {
                    seen[display] = 1;
                }
            }
            result.Add(new DisplayName(display, gitName));
        }
        return result;
    }

    private static bool IsInvalidChar(char c) =>
        c < 0x20 || c is '<' or '>' or ':' or '"' or '|' or '?' or '*' or '%';

    private static string EncodeChars(string name)
    {
        // завершающий хвост из точек/пробелов Windows отрезает молча — кодируем
        var tailStart = name.Length;
        while (tailStart > 0 && name[tailStart - 1] is '.' or ' ') tailStart--;

        StringBuilder? sb = null;
        for (var i = 0; i < name.Length; i++)
        {
            var c = name[i];
            var invalid = IsInvalidChar(c) || i >= tailStart;
            if (invalid && sb is null)
            {
                sb = new StringBuilder(name.Length + 8);
                sb.Append(name, 0, i);
            }
            if (sb is null) continue;
            if (invalid) sb.Append('%').Append(((int)c).ToString("X2"));
            else sb.Append(c);
        }
        return sb?.ToString() ?? name;
    }

    private static string GuardReserved(string name)
    {
        var dot = name.IndexOf('.');
        var baseName = dot < 0 ? name : name[..dot];
        if (!Reserved.Contains(baseName)) return name;
        return dot < 0 ? name + "%RES" : name[..dot] + "%RES" + name[dot..];
    }
}
