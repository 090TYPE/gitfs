using System.Text;
using Gitfs.Core;
using Gitfs.Vfs.Overlay;

namespace Gitfs.Vfs.Views;

/// <summary>Служебная вьюха `.gitfs/` — диагностика ВНУТРИ смонтированного
/// тома (спека §3 и §14):
///
///   .gitfs/status.txt   режим, опорная точка, эпоха, кэши, оверлей
///   .gitfs/log.txt      события: обрезания, переоткрытия пакетов, отказы
///   .gitfs/overlay/     что именно записал пользователь (только чтение)
///
/// Смысл в том, что «диагностика лежит внутри самого продукта»: чтобы понять,
/// почему том ведёт себя так, не нужен ни отдельный инструмент, ни доступ к
/// машине — достаточно открыть файл на диске, который уже открыт.
///
/// Содержимое СИНТЕТИЧЕСКОЕ: этих байтов нет ни в репозитории, ни на диске,
/// они собираются в момент открытия. Поэтому вьюха реализует
/// <see cref="ISyntheticView"/>, а не отдаёт OID блоба.</summary>
public sealed class GitfsView : ViewBase, ISyntheticView
{
    public const string ViewName = ".gitfs";
    public const string StatusFile = "status.txt";
    public const string LogFile = "log.txt";
    public const string OverlayDir = "overlay";

    private readonly MountLog _log;
    private readonly OverlayStore? _overlay;
    private readonly DateTimeOffset _mountedAt;

    public GitfsView(NamePolicy names, MountLog log, OverlayStore? overlay = null,
        DateTimeOffset? mountedAt = null) : base(names)
    {
        _log = log;
        _overlay = overlay;
        _mountedAt = mountedAt ?? DateTimeOffset.Now;
    }

    public override string Name => ViewName;

    public override NodeInfo? Resolve(RepoSnapshot snapshot, IReadOnlyList<string> segments)
    {
        var stamp = DateTimeOffset.Now;
        if (segments.Count == 0) return NodeInfo.Directory(stamp);

        if (segments.Count == 1)
        {
            if (Is(segments[0], StatusFile) || Is(segments[0], LogFile))
            {
                // Размер обязан совпасть с тем, что отдаст чтение: Проводник
                // и `cat` верят stat, и файл, объявленный нулевым, читается
                // как пустой независимо от содержимого.
                var bytes = Read(snapshot, segments);
                return new NodeInfo(NodeKind.File, default, bytes?.LongLength ?? 0, stamp);
            }
            if (Is(segments[0], OverlayDir)) return NodeInfo.Directory(stamp);
            return null;
        }

        if (!Is(segments[0], OverlayDir)) return null;
        if (_overlay is null) return null;

        var virtualPath = string.Join('/', segments.Skip(1));
        foreach (var entry in _overlay.Entries)
        {
            if (entry.Kind != OverlayKind.File) continue;
            if (!string.Equals(OverlayName(entry), virtualPath, Names.Comparison)) continue;
            var file = System.IO.Path.Combine(_overlay.Root, entry.StorageName);
            if (!File.Exists(file)) return null;
            var info = new FileInfo(file);
            return new NodeInfo(NodeKind.File, default, info.Length, info.LastWriteTimeUtc);
        }
        // промежуточная папка внутри overlay/
        return AnyUnder(virtualPath) ? NodeInfo.Directory(stamp) : null;
    }

    public override IEnumerable<DirEntry> List(RepoSnapshot snapshot,
        IReadOnlyList<string> segments)
    {
        var stamp = DateTimeOffset.Now;
        if (segments.Count == 0)
        {
            yield return new DirEntry(StatusFile, new NodeInfo(NodeKind.File, default,
                Render(snapshot).Length, stamp));
            yield return new DirEntry(LogFile, new NodeInfo(NodeKind.File, default,
                Encoding.UTF8.GetByteCount(_log.Render()), stamp));
            if (_overlay is not null) yield return new DirEntry(OverlayDir, NodeInfo.Directory(stamp));
            yield break;
        }
        if (!Is(segments[0], OverlayDir) || _overlay is null) yield break;

        var prefix = segments.Count == 1 ? "" : string.Join('/', segments.Skip(1)) + "/";
        var seen = new HashSet<string>(Names.Comparer);
        foreach (var entry in _overlay.Entries)
        {
            if (entry.Kind != OverlayKind.File) continue;
            var name = OverlayName(entry);
            if (!name.StartsWith(prefix, Names.Comparison)) continue;
            var rest = name[prefix.Length..];
            var slash = rest.IndexOf('/');
            if (slash < 0)
            {
                if (!seen.Add(rest)) continue;
                var file = System.IO.Path.Combine(_overlay.Root, entry.StorageName);
                var size = File.Exists(file) ? new FileInfo(file).Length : 0;
                yield return new DirEntry(rest, new NodeInfo(NodeKind.File, default, size, stamp));
            }
            else
            {
                var dir = rest[..slash];
                if (!seen.Add(dir)) continue;
                yield return new DirEntry(dir, NodeInfo.Directory(stamp));
            }
        }
    }

    /// <summary>Байты синтетического файла. null — путь не синтетический
    /// (например, файл внутри overlay/: он лежит на диске и читается обычным
    /// путём).</summary>
    public override byte[]? Read(RepoSnapshot snapshot, IReadOnlyList<string> segments)
    {
        if (segments.Count != 1) return null;
        if (Is(segments[0], StatusFile)) return Render(snapshot);
        if (Is(segments[0], LogFile)) return Encoding.UTF8.GetBytes(_log.Render());
        return null;
    }

    /// <summary>Файл песочницы на диске. Читается как есть — копировать его в
    /// память ради просмотра незачем, а размера у пользовательской записи
    /// может быть сколько угодно.</summary>
    public override string? PhysicalPath(RepoSnapshot snapshot, IReadOnlyList<string> segments)
    {
        if (_overlay is null || segments.Count < 2 || !Is(segments[0], OverlayDir)) return null;
        var virtualPath = string.Join('/', segments.Skip(1));
        foreach (var entry in _overlay.Entries)
        {
            if (entry.Kind != OverlayKind.File) continue;
            if (!string.Equals(OverlayName(entry), virtualPath, Names.Comparison)) continue;
            var file = System.IO.Path.Combine(_overlay.Root, entry.StorageName);
            return File.Exists(file) ? file : null;
        }
        return null;
    }

    /// <summary>Служебная вьюха не принимает запись НИГДЕ: диагностика
    /// читается, а не правится.</summary>
    public override bool IsWriteProtected(IReadOnlyList<string> segments) => true;

    private bool Is(string a, string b) => string.Equals(a, b, Names.Comparison);

    private bool AnyUnder(string virtualPath)
    {
        if (_overlay is null) return false;
        var prefix = virtualPath + "/";
        foreach (var entry in _overlay.Entries)
            if (OverlayName(entry).StartsWith(prefix, Names.Comparison)) return true;
        return false;
    }

    private static string OverlayName(OverlayEntry entry) => entry.VirtualPath.TrimStart('/');

    /// <summary>status.txt. Формат нарочно человеческий, а не машинный: его
    /// читают глазами в Блокноте, когда что-то идёт не так. Всё, что здесь
    /// написано, взято у живых объектов — ни одного придуманного числа.</summary>
    private byte[] Render(RepoSnapshot snapshot)
    {
        var sb = new StringBuilder();
        var o = snapshot.Options;

        sb.AppendLine("gitfs status");
        sb.AppendLine();
        sb.AppendLine($"mounted           {_mountedAt:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine($"git dir           {snapshot.GitDir}");
        sb.AppendLine($"mode              {(o.ReadOnly ? "read-only" : "writes go to a sandbox")}");
        sb.AppendLine($"history ref       {o.HistoryRef ?? "HEAD"}");
        sb.AppendLine($"name policy       {(o.NamePolicy == NamePolicyKind.Portable ? "portable" : "native")}");
        sb.AppendLine($"commit limit      {o.CommitLimit}");
        sb.AppendLine($"history limit     {o.HistoryLimit}");
        sb.AppendLine();
        sb.AppendLine("caches            hits / misses");
        Cache(sb, "  trees", snapshot.TreeCache.Hits, snapshot.TreeCache.Misses);
        Cache(sb, "  listings", snapshot.ListingCache.Hits, snapshot.ListingCache.Misses);
        Cache(sb, "  paths", snapshot.PathCache.Hits, snapshot.PathCache.Misses);
        Cache(sb, "  history", snapshot.HistoryCache.Hits, snapshot.HistoryCache.Misses);
        sb.AppendLine($"  budget          {Mb(snapshot.TreeCache.Used)} of {Mb(snapshot.TreeCache.MaxCost)} (trees)");
        sb.AppendLine($"  max object      {o.MaxCachedBlobMegabytes} MB");
        sb.AppendLine();
        sb.AppendLine($"packs             {snapshot.Objects.PackCount}");
        sb.AppendLine($"  delta hits      {snapshot.Objects.DeltaBaseCacheHits}");
        sb.AppendLine($"  size hits       {snapshot.Objects.SizeCacheHits}");
        sb.AppendLine($"pack reopens      {_log.Reopens}");
        sb.AppendLine();
        if (_overlay is null)
        {
            sb.AppendLine("sandbox           none (this volume takes no writes)");
        }
        else
        {
            var files = _overlay.Entries.Count(e => e.Kind == OverlayKind.File);
            var hidden = _overlay.Entries.Count(e => e.Kind != OverlayKind.File);
            sb.AppendLine($"sandbox           {_overlay.Root}");
            sb.AppendLine($"  files written   {files}");
            sb.AppendLine($"  paths hidden    {hidden}");
            sb.AppendLine($"  bytes           {_overlay.TotalBytes}");
        }
        sb.AppendLine();
        sb.AppendLine("the repository itself is never written to; see .gitfs/log.txt for events");
        return Encoding.UTF8.GetBytes(sb.ToString());

        static void Cache(StringBuilder sb, string name, long hits, long misses) =>
            sb.AppendLine($"{name.PadRight(18)}{hits} / {misses}");

        static string Mb(long bytes) => $"{bytes / (1024.0 * 1024):0.#} MB";
    }
}
