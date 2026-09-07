using System.Text.Json.Nodes;

namespace EasyIntercept.Hosting;

/// <summary>Command-line flags that control desktop behaviour (not ASP.NET config).</summary>
public sealed class StartupOptions
{
    public const int DefaultUiPort = 1337;
    public const int ProxyPort = 9999;

    /// <summary>Generate the root CA (if needed) and import it into the OS trust store, then exit.</summary>
    public bool InstallCa { get; init; }

    /// <summary>Started by the OS at login: stay quiet, don't open a browser.</summary>
    public bool Autostart { get; init; }

    public bool NoBrowser { get; init; }
    public bool NoTray { get; init; }

    public bool ShouldOpenBrowser => !Autostart && !NoBrowser;

    public static StartupOptions Parse(string[] args)
    {
        var set = new HashSet<string>(args, StringComparer.OrdinalIgnoreCase);
        return new StartupOptions
        {
            InstallCa = set.Contains("--install-ca"),
            Autostart = set.Contains("--autostart"),
            NoBrowser = set.Contains("--no-browser"),
            NoTray = set.Contains("--no-tray"),
        };
    }

    /// <summary>Strips our own flags so ASP.NET's command-line config provider doesn't choke on them.</summary>
    public static string[] StripOwnFlags(string[] args)
    {
        var own = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "--install-ca", "--autostart", "--no-browser", "--no-tray" };
        return args.Where(a => !own.Contains(a)).ToArray();
    }

    public static int GetUiPort(IConfiguration config) =>
        config.GetValue<int?>("UiPort") ?? DefaultUiPort;

    public static string UiUrl(int port) => $"http://localhost:{port}";

    /// <summary>Persists a setting in the user-level appsettings.json under the data root.</summary>
    public static void SaveUserSetting(string userSettingsFile, string key, JsonNode? value)
    {
        JsonObject root;
        try
        {
            root = File.Exists(userSettingsFile)
                ? JsonNode.Parse(File.ReadAllText(userSettingsFile)) as JsonObject ?? new JsonObject()
                : new JsonObject();
        }
        catch
        {
            root = new JsonObject();
        }

        root[key] = value;
        Directory.CreateDirectory(Path.GetDirectoryName(userSettingsFile)!);
        File.WriteAllText(userSettingsFile, root.ToJsonString(new() { WriteIndented = true }));
    }
}
