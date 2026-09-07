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
