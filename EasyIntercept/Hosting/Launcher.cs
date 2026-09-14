using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace EasyIntercept.Hosting;

public static class Launcher
{
    public static void OpenUrl(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Could not open browser for {url}: {ex.Message}");
        }
    }

    public static void OpenFolder(string path)
    {
        try
        {
            Directory.CreateDirectory(path);
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Could not open folder {path}: {ex.Message}");
        }
    }

    public enum HostOs { Windows, MacOS, Linux }

    public static HostOs CurrentOs =>
        OperatingSystem.IsWindows() ? HostOs.Windows
        : OperatingSystem.IsMacOS() ? HostOs.MacOS
        : HostOs.Linux;

    /// <summary>Opens the OS file manager with <paramref name="path"/> selected. Returns false if that failed.</summary>
    public static bool RevealFile(string path)
    {
        try
        {
            using var process = Process.Start(RevealCommand(path, CurrentOs));
            return process is not null;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Could not reveal {path}: {ex.Message}");
            return false;
        }
    }

    public static ProcessStartInfo RevealCommand(string path, HostOs os)
    {
        switch (os)
        {
            case HostOs.Windows:
                // explorer parses "/select,<path>" itself; ArgumentList's quoting would break it
                return new ProcessStartInfo("explorer.exe") { Arguments = $"/select,\"{path}\"", UseShellExecute = true };
            case HostOs.MacOS:
                return new ProcessStartInfo("open") { ArgumentList = { "-R", path } };
            default:
                // xdg-open has no "select this file"; the containing folder is the closest thing
                return new ProcessStartInfo("xdg-open") { ArgumentList = { Path.GetDirectoryName(path) ?? path } };
        }
    }

    public static bool IsPortFree(int port)
    {
        try
        {
            var listener = new TcpListener(IPAddress.Any, port);
            listener.Start();
            listener.Stop();
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
    }

    public static int FindFreePort(int startAt)
    {
        for (var p = startAt; p < 65535; p++)
            if (p != StartupOptions.ProxyPort && IsPortFree(p)) return p;
        return startAt;
    }
}
