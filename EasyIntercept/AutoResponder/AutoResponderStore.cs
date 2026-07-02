using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace EasyIntercept.AutoResponder;

public class AutoResponderRule
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    public bool IsEnabled { get; set; } = true;
    public string Method { get; set; } = "";
    public string Url { get; set; } = "";
    public int ResponseStatus { get; set; } = 200;
    public Dictionary<string, string> ResponseHeaders { get; set; } = new();
    public string ResponseBody { get; set; } = "";
    public int LatencyMs { get; set; } = 0;
    public string BodyMatchType { get; set; } = "none"; // none | contains | regex
    public string BodyMatch { get; set; } = "";
}

public class AutoResponderStore : IDisposable
{
    private readonly ConcurrentDictionary<Guid, AutoResponderRule> _rules = new();
    private readonly ConcurrentDictionary<Guid, string> _files = new();
    private readonly string _dir;
    private static readonly JsonSerializerOptions _json = new() { WriteIndented = true };
    private readonly FileSystemWatcher _watcher;
    private Timer? _debounceTimer;

    public AutoResponderStore(IConfiguration config)
    {
        var raw = config["AutoResponderPath"] ?? "auto-responder";
        _dir = Path.IsPathRooted(raw) ? raw : Path.Combine(Directory.GetCurrentDirectory(), raw);
        Directory.CreateDirectory(_dir);
        LoadFromDisk();

        _watcher = new FileSystemWatcher(_dir, "*.json")
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName,
            IncludeSubdirectories = false,
            EnableRaisingEvents = true,
        };
        _watcher.Created += OnDiskChanged;
        _watcher.Changed += OnDiskChanged;
        _watcher.Deleted += OnDiskChanged;
        _watcher.Renamed += (s, e) => OnDiskChanged(s, e);
    }

    private void OnDiskChanged(object sender, FileSystemEventArgs e)
    {
        _debounceTimer?.Dispose();
        _debounceTimer = new Timer(_ => ReloadFromDisk(), null, 300, Timeout.Infinite);
    }

    private void ReloadFromDisk()
    {
        var newRules = new Dictionary<Guid, AutoResponderRule>();
        var newFiles = new Dictionary<Guid, string>();

        foreach (var path in Directory.GetFiles(_dir, "*.json"))
        {
            try
            {
                var rule = JsonSerializer.Deserialize<AutoResponderRule>(File.ReadAllText(path), _json);
                if (rule is null) continue;
                newRules[rule.Id] = rule;
                newFiles[rule.Id] = path;
            }
            catch { }
        }

        _rules.Clear();
        _files.Clear();
        foreach (var (id, rule) in newRules) _rules[id] = rule;
        foreach (var (id, path) in newFiles) _files[id] = path;
    }

    public void Dispose()
    {
        _debounceTimer?.Dispose();
        _watcher.Dispose();
    }

    public IEnumerable<AutoResponderRule> GetAll() => _rules.Values;

    public void Add(AutoResponderRule rule)
    {
        _rules[rule.Id] = rule;
        SaveToDisk(rule);
    }

    public bool Update(Guid id, AutoResponderRule updated)
    {
        if (!_rules.ContainsKey(id)) return false;
        DeleteFromDisk(id);
        _rules[id] = updated;
        SaveToDisk(updated);
        return true;
    }

    public bool Remove(Guid id)
    {
        if (!_rules.TryRemove(id, out _)) return false;
        DeleteFromDisk(id);
        return true;
    }

    public AutoResponderRule? FindMatch(string method, string url, string body) =>
        _rules.Values.FirstOrDefault(r =>
            r.IsEnabled &&
            r.Method.Equals(method, StringComparison.OrdinalIgnoreCase) &&
            r.Url.Equals(url, StringComparison.Ordinal) &&
            MatchesBody(r, body));

    private static bool MatchesBody(AutoResponderRule rule, string body) =>
        rule.BodyMatchType switch
        {
            "contains" => body.Contains(rule.BodyMatch ?? "", StringComparison.OrdinalIgnoreCase),
            "regex"    => !string.IsNullOrEmpty(rule.BodyMatch) && Regex.IsMatch(body, rule.BodyMatch),
            _          => true,
        };

    private void SaveToDisk(AutoResponderRule rule)
    {
        var path = Path.Combine(_dir, BuildFileName(rule));
        _files[rule.Id] = path;
        File.WriteAllText(path, JsonSerializer.Serialize(rule, _json));
    }

    private void DeleteFromDisk(Guid id)
    {
        if (_files.TryRemove(id, out var path))
            try { File.Delete(path); } catch { }
    }

    private static string BuildFileName(AutoResponderRule rule)
    {
        Uri.TryCreate(rule.Url, UriKind.Absolute, out var uri);
        var host = uri is not null ? Sanitize(uri.Host) : "unknown";
        var path = uri is not null ? Sanitize(uri.AbsolutePath.TrimStart('/')) : "";
        if (path.Length > 60) path = path[..60];
        var name = $"{rule.Method.ToUpperInvariant()}_{host}_{path}".TrimEnd('_');
        return name + ".json";
    }

    private static string Sanitize(string s) =>
        Regex.Replace(s, @"[^a-zA-Z0-9\-]", "_").Trim('_');

    private void LoadFromDisk()
    {
        foreach (var path in Directory.GetFiles(_dir, "*.json"))
        {
            try
            {
                var rule = JsonSerializer.Deserialize<AutoResponderRule>(File.ReadAllText(path), _json);
                if (rule is null) continue;
                _rules[rule.Id] = rule;
                _files[rule.Id] = path;
            }
            catch { }
        }
    }
}
