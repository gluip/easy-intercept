namespace EasyIntercept;

/// <summary>
/// Resolves every on-disk location the app writes to. Everything lives under a single
/// data root (default: %LOCALAPPDATA%\EasyIntercept) so the app works regardless of the
/// current working directory — required for autostart and Program Files installs.
/// </summary>
public class AppPaths
{
    public const string AppFolderName = "EasyIntercept";

    public string Root { get; }
    public string Sessions { get; }
    public string AutoResponder { get; }
    public string Certs { get; }
    public string BrowserProfiles { get; }

    /// <summary>User-editable overrides (e.g. UiPort) that survive reinstalls.</summary>
    public string UserSettingsFile => Path.Combine(Root, "appsettings.json");

    public AppPaths(IConfiguration config)
        : this(config["DataRoot"], config["SessionsPath"], config["AutoResponderPath"])
    {
    }

    public AppPaths(string? dataRoot, string? sessionsPath = null, string? autoResponderPath = null)
    {
        Root = ResolveRoot(dataRoot);
        Sessions = Resolve(sessionsPath ?? "sessions");
        AutoResponder = Resolve(autoResponderPath ?? "auto-responder");
        Certs = Resolve("certs");
        BrowserProfiles = Resolve("browser-profiles");

        foreach (var dir in new[] { Root, Sessions, AutoResponder, Certs, BrowserProfiles })
            Directory.CreateDirectory(dir);
    }

    public static string DefaultRoot =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), AppFolderName);

    public static string ResolveRoot(string? configured) =>
        string.IsNullOrWhiteSpace(configured) ? DefaultRoot : Path.GetFullPath(configured);

    private string Resolve(string path) =>
        Path.IsPathRooted(path) ? Path.GetFullPath(path) : Path.GetFullPath(Path.Combine(Root, path));
}
