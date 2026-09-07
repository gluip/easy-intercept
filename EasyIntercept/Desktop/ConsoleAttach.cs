#if WINDOWS
using System.Runtime.InteropServices;

namespace EasyIntercept.Desktop;

/// <summary>
/// The Windows build is a WinExe (no console window when double-clicked or autostarted).
/// When launched from a terminal (or via <c>dotnet run</c>) we attach to the parent's console
/// so logging is still visible during development.
/// </summary>
public static class ConsoleAttach
{
    private const int ATTACH_PARENT_PROCESS = -1;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AttachConsole(int dwProcessId);

    public static bool TryAttachParentConsole()
    {
        if (!AttachConsole(ATTACH_PARENT_PROCESS)) return false;

        Console.SetOut(new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true });
        Console.SetError(new StreamWriter(Console.OpenStandardError()) { AutoFlush = true });
        return true;
    }
}
#endif
