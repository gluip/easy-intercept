using System.Diagnostics;
using System.Runtime.Versioning;
using Microsoft.Win32;

namespace EasyIntercept.Proxy;

public record DetectedBrowser(string Id, string Name, string ExePath);

public class BrowserLauncherService
{
    private const int ProxyPort = 9999;
    private const string ProfileRootDir = "browser-profiles";

    public IReadOnlyList<DetectedBrowser> DetectBrowsers()
    {
        var browsers = new List<DetectedBrowser>();

        var chromePath = FindChrome();
        if (chromePath is not null)
            browsers.Add(new DetectedBrowser("chrome", "Chrome", chromePath));

        return browsers;
    }

    public void Launch(string browserId)
    {
        var browser = DetectBrowsers().FirstOrDefault(b => b.Id == browserId)
            ?? throw new InvalidOperationException($"Browser '{browserId}' was not found.");

        var profileDir = Path.GetFullPath(Path.Combine(ProfileRootDir, browserId));
        Directory.CreateDirectory(profileDir);

        var psi = new ProcessStartInfo(browser.ExePath)
        {
            UseShellExecute = false,
        };
        psi.ArgumentList.Add($"--proxy-server=127.0.0.1:{ProxyPort}");
        psi.ArgumentList.Add($"--user-data-dir={profileDir}");
        psi.ArgumentList.Add("--no-first-run");
        psi.ArgumentList.Add("--no-default-browser-check");

        Process.Start(psi);
    }

    private static string? FindChrome()
    {
        if (OperatingSystem.IsWindows()) return FindChromeWindows();
        if (OperatingSystem.IsMacOS()) return FindChromeMac();
        return null;
    }

    [SupportedOSPlatform("windows")]
    private static string? FindChromeWindows()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\chrome.exe");
            var path = key?.GetValue(null) as string;
            if (!string.IsNullOrEmpty(path) && File.Exists(path)) return path;
        }
        catch
        {
            // fall through to well-known paths
        }

        var candidates = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Google", "Chrome", "Application", "chrome.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Google", "Chrome", "Application", "chrome.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Google", "Chrome", "Application", "chrome.exe"),
        };
        return candidates.FirstOrDefault(File.Exists);
    }

    [SupportedOSPlatform("macos")]
    private static string? FindChromeMac()
    {
        var candidates = new[]
        {
            "/Applications/Google Chrome.app/Contents/MacOS/Google Chrome",
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Personal), "..", "Applications", "Google Chrome.app", "Contents", "MacOS", "Google Chrome"),
        };
        return candidates.FirstOrDefault(File.Exists);
    }
}
