namespace Gitfs.Cli;

/// <summary>Единственный вызов libc во всём CLI: послать SIGTERM. .NET умеет
/// только Kill() = SIGKILL, после которого не отрабатывает ни снятие тома,
/// ни удаление песочницы.</summary>
internal static class Interop
{
    [System.Runtime.InteropServices.DllImport("libc", SetLastError = true, EntryPoint = "kill")]
    private static extern int NativeKill(int pid, int signal);

    public static int Kill(int pid, int signal)
    {
        try { return NativeKill(pid, signal); }
        catch (DllNotFoundException) { return -1; }
        catch (EntryPointNotFoundException) { return -1; }
    }
}
