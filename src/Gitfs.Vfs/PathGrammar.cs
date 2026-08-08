namespace Gitfs.Vfs;

/// <summary>Разбор виртуальных путей (спека §4): нормализация разделителей,
/// отказ «.»/«..» на этапе разбора, наибольшее совпадение имени ссылки.</summary>
public static class PathGrammar
{
    /// <summary>null — путь синтаксически недопустим («.», «..», NUL).
    /// Пустой массив — корень.</summary>
    public static string[]? Split(string path)
    {
        var segments = path.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries);
        foreach (var s in segments)
            if (s is "." or ".." || s.Contains('\0'))
                return null;
        return segments;
    }

    /// <summary>Наибольшее совпадение по списку известных ссылок (спека §4):
    /// «feature/login/src/f.cs» при ветках {feature/login} → (feature/login, [src, f.cs]).
    /// null — ни один префикс не является полным именем ссылки.</summary>
    /// <summary>Возвращает КАНОНИЧЕСКОЕ имя ссылки из набора, а не то, что
    /// пришло в пути: при регистронезависимой политике WinFsp передаёт
    /// «BRANCHES/MAIN», а искать объекты нужно по «main».</summary>
    public static (string RefName, ArraySegment<string> Tail)? MatchLongestRef(
        HashSet<string> refNames, string[] segments)
    {
        for (var k = segments.Length; k >= 1; k--)
        {
            var candidate = string.Join('/', segments, 0, k);
            if (refNames.TryGetValue(candidate, out var canonical))
                return (canonical, new ArraySegment<string>(segments, k, segments.Length - k));
        }
        return null;
    }

    /// <summary>Сегменты — собственный префикс какой-то ссылки
    /// («feature» при ветке feature/login): промежуточная директория.</summary>
    public static bool IsRefPrefix(HashSet<string> refNames, string[] segments,
        StringComparison comparison = StringComparison.Ordinal)
    {
        var prefix = string.Join('/', segments) + "/";
        foreach (var name in refNames)
            if (name.StartsWith(prefix, comparison))
                return true;
        return false;
    }
}
