using System.Reflection;
using System.Text;

namespace Gitfs.Vfs;

/// <summary>Оформление корневых папок в Проводнике (бриф §2.2 — по нему это
/// ОСНОВНАЯ поверхность продукта: своих экранов у gitfs почти нет, главный
/// интерфейс — чужой).
///
/// Механика ровно такая, какой её задумал Windows: у папки атрибут SYSTEM,
/// внутри неё скрытый desktop.ini со ссылкой на .ico. Иконки лежат на самом
/// томе, в `.gitfs/icons/`, поэтому ссылка относительная и не зависит ни от
/// буквы диска, ни от того, куда установлен gitfs.
///
/// ТОЛЬКО НА WINDOWS. На Linux ни один файловый менеджер про desktop.ini не
/// знает, и отдавать его значило бы положить служебный файл в каждую вьюху —
/// мусор, который видно, ради оформления, которого не будет.</summary>
public static class ShellFolders
{
    public const string DesktopIni = "desktop.ini";
    public const string AutorunInf = "autorun.inf";
    public const string IconsDirectory = "icons";

    /// <summary>Отдавать ли оформление. Свойство, а не константа: тесты
    /// проверяют обе стороны, а притворяться Windows на Linux иначе нечем.</summary>
    public static bool Enabled { get; set; } = OperatingSystem.IsWindows();

    private static readonly Dictionary<string, string> IconByView =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["branches"] = "branches",
            ["tags"] = "tags",
            ["commits"] = "commits",
            ["dates"] = "dates",
            ["history"] = "history",
            [".gitfs"] = "system",
        };

    private static readonly Dictionary<string, string> TipByView =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["branches"] = "Every branch as a live working tree.",
            ["tags"] = "Released versions, frozen.",
            ["commits"] = "Snapshots by commit sha.",
            ["dates"] = "The same commits, filed by day.",
            ["history"] = "Each file opens as a folder of its versions.",
            [".gitfs"] = "What this volume is doing: status, log, sandbox.",
        };

    public static IEnumerable<string> IconNames => IconByView.Values.Append("volume").Distinct();

    /// <summary>desktop.ini для корня вьюхи, или null — оформления нет.</summary>
    public static byte[]? DesktopIniFor(string view)
    {
        if (!Enabled || !IconByView.TryGetValue(view, out var icon)) return null;
        var depth = view.Count(c => c == '/') + 1;      // сколько «..» до корня тома
        var up = string.Concat(Enumerable.Repeat(@"..\", depth));
        var sb = new StringBuilder();
        sb.Append("[.ShellClassInfo]\r\n");
        sb.Append($"IconResource={up}.gitfs\\{IconsDirectory}\\{icon}.ico,0\r\n");
        if (TipByView.TryGetValue(view, out var tip)) sb.Append($"InfoTip={tip}\r\n");
        // Явное «это не обычная папка»: Проводник иначе кэширует прежний вид.
        sb.Append("[ViewState]\r\nMode=\r\nVid=\r\nFolderType=Generic\r\n");
        return Encoding.UTF8.GetBytes(sb.ToString());
    }

    /// <summary>autorun.inf в корне тома — единственный документированный
    /// способ дать диску свою иконку. Сработает он или нет, решает политика
    /// Windows, а не gitfs: файл мы отдаём, обещать результат не можем.</summary>
    public static byte[]? AutorunFor(string repositoryName)
    {
        if (!Enabled) return null;
        var sb = new StringBuilder();
        sb.Append("[autorun]\r\n");
        sb.Append($"icon=.gitfs\\{IconsDirectory}\\volume.ico\r\n");
        sb.Append($"label=gitfs: {repositoryName}\r\n");
        return Encoding.UTF8.GetBytes(sb.ToString());
    }

    /// <summary>Байты .ico из ресурсов сборки. Иконки собраны из знаков
    /// дизайн-отдела (tools: GITFS_UI_PREVIEW=icons) и лежат в репозитории
    /// файлами — это ассеты, а не шаг сборки.</summary>
    public static byte[]? Icon(string name)
    {
        if (!IconNames.Contains(name, StringComparer.OrdinalIgnoreCase)) return null;
        var assembly = typeof(ShellFolders).Assembly;
        var resource = assembly.GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith("." + name + ".ico", StringComparison.OrdinalIgnoreCase));
        if (resource is null) return null;
        using var stream = assembly.GetManifestResourceStream(resource);
        if (stream is null) return null;
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        return memory.ToArray();
    }
}
