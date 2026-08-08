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
        // RtlIsDosDeviceName_U распознаёт и надстрочные цифры U+00B9/B2/B3
        "COM¹", "COM²", "COM³", "LPT¹", "LPT²", "LPT³",
    };

    private readonly bool _encodeInvalid;
    private readonly bool _guardReserved;
    private readonly bool _foldCase;

    /// <summary>Политика свёртывает регистр — значит и РЕЗОЛВ путей обязан
    /// быть регистронезависимым: WinFsp передаёт имена в произвольном
    /// регистре (наблюдалось \BRANCHES\MAIN\FILE.TXT), а том объявлен
    /// case-insensitive. Уникальность гарантирует правило ~N.</summary>
    public bool FoldsCase => _foldCase;

    /// <summary>Различает политики в ключах кэшей: отображаемые имена —
    /// функция от дерева И политики, поэтому один OID даёт разные листинги
    /// под разными политиками.</summary>
    public string Tag => $"{(_encodeInvalid ? 'e' : '-')}{(_guardReserved ? 'r' : '-')}{(_foldCase ? 'f' : '-')}";

    public StringComparison Comparison =>
        _foldCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    public StringComparer Comparer =>
        _foldCase ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

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
    /// регистра. Порядок входа — порядок git-дерева, он же даёт детерминизм.
    /// Два прохода (ревью M2): сгенерированный суффикс обязан не столкнуться
    /// с НАСТОЯЩИМ именем дальше по списку (README, readme, readme~2 —
    /// без этого в листинге два readme~2 и настоящий файл затенён).
    /// Ключ сравнения нормализован NFC: на macOS NFD-имя «e&#769;» и NFC-имя «é» —
    /// один файл для ФС, коллизия должна детектироваться.</summary>
    public IReadOnlyList<DisplayName> EncodeListing(IEnumerable<string> gitNames)
    {
        var names = gitNames as IReadOnlyList<string> ?? gitNames.ToList();
        var result = new List<DisplayName>(names.Count);
        if (!_foldCase)
        {
            foreach (var n in names) result.Add(new DisplayName(EncodeName(n), n));
            return result;
        }

        static string Key(string s) => s.Normalize(System.Text.NormalizationForm.FormC);

        // проход 1: занять все «родные» имена, пометить дубликаты
        var encoded = new string[names.Count];
        var isDuplicate = new bool[names.Count];
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var firstSeen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < names.Count; i++)
        {
            encoded[i] = EncodeName(names[i]);
            var key = Key(encoded[i]);
            taken.Add(key);
            if (!firstSeen.Add(key)) isDuplicate[i] = true;
        }

        // проход 2: дубликатам — минимальный свободный ~N против ПОЛНОГО множества
        for (var i = 0; i < names.Count; i++)
        {
            var display = encoded[i];
            if (isDuplicate[i])
            {
                var n = 2;
                string candidate;
                do { candidate = $"{display}~{n++}"; } while (!taken.Add(Key(candidate)));
                display = candidate;
            }
            result.Add(new DisplayName(display, names[i]));
        }
        return result;
    }

    // Полный набор запрещённых печатных символов NTFS: < > : " / \ | ? *
    // (ревью M2: '\' был пропущен — 7 из 9). '/' в корректных git-деревьях
    // невозможен (TreeObject.Parse отвергает), здесь — оборона в глубину.
    private static bool IsInvalidChar(char c) =>
        c < 0x20 || c is '<' or '>' or ':' or '"' or '|' or '?' or '*' or '%' or '\\' or '/';

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
            if (invalid)
            {
                // Инвариант обратимости: кодируются только символы <= 0xFF,
                // ширина всегда ровно 2 hex-цифры. Расширение IsInvalidChar
                // на символ выше 0xFF без смены формата сломает декодер.
                System.Diagnostics.Debug.Assert(c <= 0xFF, "percent-encoding is 2 hex digits wide");
                sb.Append('%').Append(((int)c).ToString("X2"));
            }
            else sb.Append(c);
        }
        return sb?.ToString() ?? name;
    }

    private static string GuardReserved(string name)
    {
        var dot = name.IndexOf('.');
        // легаси-парсер Win32 игнорирует пробелы между базой и точкой:
        // "aux .txt" — это устройство AUX
        var baseName = (dot < 0 ? name : name[..dot]).TrimEnd(' ');
        if (!Reserved.Contains(baseName)) return name;
        return dot < 0 ? name + "%RES" : name[..dot] + "%RES" + name[dot..];
    }
}
