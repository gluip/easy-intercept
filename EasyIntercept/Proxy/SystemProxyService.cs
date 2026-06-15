using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32;

namespace EasyIntercept.Proxy;

public class SystemProxyService
{
    private const string ProxyHost = "127.0.0.1";
    private const int ProxyPort = 9999;
    private const string ProxyAddress = "127.0.0.1:9999";

    public bool IsEnabled()
    {
        if (OperatingSystem.IsWindows()) return IsEnabledWindows();
        if (OperatingSystem.IsMacOS()) return IsEnabledMac();
        return false;
    }

    public void Enable()
    {
        if (OperatingSystem.IsWindows()) EnableWindows();
        else if (OperatingSystem.IsMacOS()) EnableMac();
    }

    public void Disable()
    {
        if (OperatingSystem.IsWindows()) DisableWindows();
        else if (OperatingSystem.IsMacOS()) DisableMac();
    }

    // ---- Windows ----

    private const string RegistryKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Internet Settings";
    private const int INTERNET_OPTION_SETTINGS_CHANGED = 39;
    private const int INTERNET_OPTION_REFRESH = 37;

    [DllImport("wininet.dll", SetLastError = true)]
    [SupportedOSPlatform("windows")]
    private static extern bool InternetSetOption(IntPtr hInternet, int dwOption, IntPtr lpBuffer, int dwBufferLength);

    [SupportedOSPlatform("windows")]
    private bool IsEnabledWindows()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RegistryKeyPath);
        if (key is null) return false;
        var enabled = key.GetValue("ProxyEnable") as int?;
        var server = key.GetValue("ProxyServer") as string;
        return enabled == 1 && server == ProxyAddress;
    }

    [SupportedOSPlatform("windows")]
    private void EnableWindows()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RegistryKeyPath, writable: true)
            ?? throw new InvalidOperationException("Cannot open Internet Settings registry key");
        key.SetValue("ProxyEnable", 1, RegistryValueKind.DWord);
        key.SetValue("ProxyServer", ProxyAddress, RegistryValueKind.String);
        key.SetValue("ProxyOverride", "<local>", RegistryValueKind.String);
        NotifyWindowsSettingsChanged();
    }

    [SupportedOSPlatform("windows")]
    private void DisableWindows()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RegistryKeyPath, writable: true)
            ?? throw new InvalidOperationException("Cannot open Internet Settings registry key");
        key.SetValue("ProxyEnable", 0, RegistryValueKind.DWord);
        NotifyWindowsSettingsChanged();
    }

    [SupportedOSPlatform("windows")]
    private static void NotifyWindowsSettingsChanged()
    {
        InternetSetOption(IntPtr.Zero, INTERNET_OPTION_SETTINGS_CHANGED, IntPtr.Zero, 0);
        InternetSetOption(IntPtr.Zero, INTERNET_OPTION_REFRESH, IntPtr.Zero, 0);
    }

    // ---- macOS ----

    [SupportedOSPlatform("macos")]
    private IEnumerable<string> GetMacNetworkServices()
    {
        var output = RunProcess("networksetup", ["-listallnetworkservices"]);
        return output.Split('\n')
            .Skip(1) // skip the header line about asterisks
            .Where(s => !string.IsNullOrWhiteSpace(s) && !s.TrimStart().StartsWith('*'))
            .Select(s => s.Trim());
    }

    [SupportedOSPlatform("macos")]
    private bool IsEnabledMac()
    {
        var service = GetMacNetworkServices().FirstOrDefault();
        if (service is null) return false;

        var output = RunProcess("networksetup", ["-getwebproxy", service]);
        var lines = output.Split('\n');
        var enabled = lines.FirstOrDefault(l => l.StartsWith("Enabled:"))?.Split(':', 2)[1].Trim();
        var server = lines.FirstOrDefault(l => l.StartsWith("Server:"))?.Split(':', 2)[1].Trim();
        var port = lines.FirstOrDefault(l => l.StartsWith("Port:"))?.Split(':', 2)[1].Trim();
        return enabled == "Yes" && server == ProxyHost && port == ProxyPort.ToString();
    }

    [SupportedOSPlatform("macos")]
    private void EnableMac()
    {
        foreach (var service in GetMacNetworkServices())
        {
            RunProcess("networksetup", ["-setwebproxy", service, ProxyHost, ProxyPort.ToString()]);
            RunProcess("networksetup", ["-setwebproxystate", service, "on"]);
            RunProcess("networksetup", ["-setsecurewebproxy", service, ProxyHost, ProxyPort.ToString()]);
            RunProcess("networksetup", ["-setsecurewebproxystate", service, "on"]);
        }
    }

    [SupportedOSPlatform("macos")]
    private void DisableMac()
    {
        foreach (var service in GetMacNetworkServices())
        {
            RunProcess("networksetup", ["-setwebproxystate", service, "off"]);
            RunProcess("networksetup", ["-setsecurewebproxystate", service, "off"]);
        }
    }

    private static string RunProcess(string filename, string[] args)
    {
        var psi = new ProcessStartInfo(filename)
        {
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var arg in args)
            psi.ArgumentList.Add(arg);

        using var process = Process.Start(psi)!;
        var output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        return output;
    }
}
