namespace Gitfs.Mount.WinFsp;

/// <summary>Проба биндинга: подтверждает, что типы WinFsp видны компилятору
/// без установленного драйвера (риск расписания §5 спеки).</summary>
internal static class BindingProbe
{
    public static string TypeName => typeof(Fsp.FileSystemBase).FullName!;
}
