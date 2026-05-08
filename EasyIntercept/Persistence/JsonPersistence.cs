using System.Text.Json;
using System.Text.RegularExpressions;
using EasyIntercept.AutoResponder;
using EasyIntercept.Models;

namespace EasyIntercept.Persistence;

public class JsonPersistence : IDisposable
{
    private readonly ILogger<JsonPersistence> _log;
    private readonly string _basePath;
    private readonly string _autoResponderPath;
    private readonly string _recordingsPath;
    private readonly string _analysisPath;
    private readonly Dictionary<Guid, string> _fileIndex = new();
    private readonly HashSet<string> _pendingWrites = new();
    private FileSystemWatcher? _watcher;
    private Timer? _debounceTimer;
    private readonly object _lock = new();

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public Action? OnAutoResponderChanged { get; set; }
    public Action? OnRecordingsChanged { get; set; }
    public Action? OnAnalysisChanged { get; set; }

    public JsonPersistence(ILogger<JsonPersistence> log)
    {
        _log = log;
        _basePath = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "rules");
        _basePath = Path.GetFullPath(_basePath);
        _autoResponderPath = Path.Combine(_basePath, "auto-responder");
        _recordingsPath = Path.Combine(_basePath, "recordings");
        _analysisPath = Path.Combine(_basePath, "analysis");

        Directory.CreateDirectory(_autoResponderPath);
        Directory.CreateDirectory(_recordingsPath);
        Directory.CreateDirectory(_analysisPath);

        _log.LogInformation("Rules path: {Path}", _basePath);
    }

    // --- Slugify ---

    private static string Slugify(string name)
    {
        var slug = Regex.Replace(name.ToLowerInvariant(), @"[^a-z0-9]+", "-").Trim('-');
        if (string.IsNullOrEmpty(slug)) slug = "rule";
        return slug;
    }

    // --- URL-based hierarchical path ---

    private static string SanitizeSegment(string s)
        => Regex.Replace(s, @"[<>:""|?*\\]", "_");

    private static (string host, string[] pathSegments)? ParseUrlPattern(string urlPattern)
    {
        // Remove regex backslash escapes to recover original URL
        var url = Regex.Replace(urlPattern, @"\\(.)", "$1");

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return null;
        if (uri.Scheme != "http" && uri.Scheme != "https")
            return null;

        var host = SanitizeSegment(uri.Host);
        var path = Uri.UnescapeDataString(uri.AbsolutePath).Trim('/');
        var segments = path
            .Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Select(SanitizeSegment)
            .ToArray();

        return (host, segments);
    }

    /// <summary>
    /// Builds a hierarchical folder path: baseDir/domain/path/METHOD/
    /// Adds _1, _2 etc. suffix only if another rule already occupies that folder.
    /// Falls back to baseDir/slugified-name/ for non-URL patterns.
    /// </summary>
    private string RuleFolderPath(string baseDir, AutoResponderRule rule)
    {
        var method = rule.Method.ToUpperInvariant();
        if (method == "*") method = "ANY";

        var parsed = ParseUrlPattern(rule.UrlPattern);
        if (parsed != null)
        {
            var (host, pathSegments) = parsed.Value;
            var parts = new[] { baseDir, host }.Concat(pathSegments).ToArray();
            var parent = Path.Combine(parts);
            return ResolveUniqueFolder(parent, method, rule.Id);
        }

        // Fallback for non-URL patterns
        var slug = Slugify(rule.Name);
        return ResolveUniqueFolder(baseDir, slug, rule.Id);
    }

    /// <summary>
    /// Returns parent/baseName if available, otherwise parent/baseName_1, _2 etc.
    /// A folder is "available" if it doesn't exist, or if it already belongs to this rule.
    /// </summary>
    private string ResolveUniqueFolder(string parent, string baseName, Guid ruleId)
    {
        var candidate = Path.Combine(parent, baseName);
        if (IsFolderOwnedByRule(candidate, ruleId))
        {
            Directory.CreateDirectory(candidate);
            return candidate;
        }

        // Conflict — find next available suffix
        for (var i = 1; ; i++)
        {
            candidate = Path.Combine(parent, $"{baseName}_{i}");
            if (IsFolderOwnedByRule(candidate, ruleId))
            {
                Directory.CreateDirectory(candidate);
                return candidate;
            }
        }
    }

    private bool IsFolderOwnedByRule(string folder, Guid ruleId)
    {
        var fullPath = Path.GetFullPath(folder);

        // Already indexed to this rule
        if (_fileIndex.TryGetValue(ruleId, out var indexed)
            && string.Equals(indexed, fullPath, StringComparison.OrdinalIgnoreCase))
            return true;

        // Folder doesn't exist yet — it's free
        if (!Directory.Exists(folder))
            return true;

        // Folder exists — check if it has a match.json with a different rule ID
        var matchFile = Path.Combine(folder, "match.json");
        if (!File.Exists(matchFile))
            return true; // No match.json = orphaned folder, safe to reuse

        try
        {
            var json = File.ReadAllText(matchFile);
            var match = JsonSerializer.Deserialize<MatchDto>(json, JsonOpts);
            return match != null && match.Id == ruleId;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Resolves a unique recording folder: recordings/slug or recordings/slug_1 etc.
    /// </summary>
    private string ResolveRecordingFolder(string baseName, Guid recordingId)
    {
        var candidate = Path.Combine(_recordingsPath, baseName);
        if (IsRecordingFolderOwned(candidate, recordingId))
            return candidate;

        for (var i = 1; ; i++)
        {
            candidate = Path.Combine(_recordingsPath, $"{baseName}_{i}");
            if (IsRecordingFolderOwned(candidate, recordingId))
                return candidate;
        }
    }

    private bool IsRecordingFolderOwned(string folder, Guid recordingId)
    {
        var fullPath = Path.GetFullPath(folder);
        if (_fileIndex.TryGetValue(recordingId, out var indexed)
            && string.Equals(indexed, fullPath, StringComparison.OrdinalIgnoreCase))
            return true;

        if (!Directory.Exists(folder))
            return true;

        var metaFile = Path.Combine(folder, "_meta.json");
        if (!File.Exists(metaFile))
            return true;

        try
        {
            var json = File.ReadAllText(metaFile);
            var meta = JsonSerializer.Deserialize<RecordingMeta>(json, JsonOpts);
            return meta != null && meta.Id == recordingId;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Removes empty directories from path up to (but not including) stopAt.
    /// </summary>
    private static void CleanupEmptyDirs(string filePath, string stopAt)
    {
        var dir = Path.GetDirectoryName(filePath);
        var stop = Path.GetFullPath(stopAt);
        while (dir != null
               && !string.Equals(Path.GetFullPath(dir), stop, StringComparison.OrdinalIgnoreCase)
               && Directory.Exists(dir)
               && !Directory.EnumerateFileSystemEntries(dir).Any())
        {
            Directory.Delete(dir);
            dir = Path.GetDirectoryName(dir);
        }
    }

    // --- Atomic write ---

    private void WriteFile(string path, string content)
    {
        var dir = Path.GetDirectoryName(path);
        if (dir != null) Directory.CreateDirectory(dir);

        var normalized = Path.GetFullPath(path);
        var tmp = path + ".tmp";
        var tmpNormalized = Path.GetFullPath(tmp);
        lock (_lock)
        {
            _pendingWrites.Add(normalized);
            _pendingWrites.Add(tmpNormalized);
        }

        File.WriteAllText(tmp, content);
        File.Move(tmp, path, overwrite: true);
    }

    // --- Body file helpers ---

    private static string ContentTypeToExtension(string contentType)
    {
        var mime = contentType.Split(';')[0].Trim().ToLowerInvariant();
        return mime switch
        {
            "application/json" => ".json",
            "text/json" => ".json",
            "text/html" => ".html",
            "application/xhtml+xml" => ".html",
            "text/xml" => ".xml",
            "application/xml" => ".xml",
            "text/css" => ".css",
            "application/javascript" or "text/javascript" => ".js",
            "text/plain" => ".txt",
            "text/csv" => ".csv",
            "image/svg+xml" => ".svg",
            _ => ".txt",
        };
    }

    /// <summary>
    /// Finds the body file inside a rule folder (body.*).
    /// </summary>
    private static string? FindBodyFile(string ruleFolder)
    {
        if (!Directory.Exists(ruleFolder)) return null;
        var files = Directory.GetFiles(ruleFolder, "body.*");
        return files.Length > 0 ? files[0] : null;
    }

    private void SaveRuleFiles(string ruleFolder, AutoResponderRule rule)
    {
        Directory.CreateDirectory(ruleFolder);

        // 1. match.json — filter criteria
        var matchDto = new MatchDto
        {
            Id = rule.Id,
            Name = rule.Name,
            UrlPattern = rule.UrlPattern,
            Method = rule.Method,
            BodyPattern = rule.BodyPattern,
            BodyPatternIsRegex = rule.BodyPatternIsRegex,
            Enabled = rule.Enabled,
        };
        WriteFile(Path.Combine(ruleFolder, "match.json"), JsonSerializer.Serialize(matchDto, JsonOpts));

        // 2. response.json — response metadata
        var responseDto = new ResponseDto
        {
            StatusCode = rule.StatusCode,
            ContentType = rule.ContentType,
            Headers = rule.Headers,
        };
        WriteFile(Path.Combine(ruleFolder, "response.json"), JsonSerializer.Serialize(responseDto, JsonOpts));

        // 3. body.{ext} — response body with correct extension
        var ext = ContentTypeToExtension(rule.ContentType);
        var bodyPath = Path.Combine(ruleFolder, "body" + ext);

        // If content-type changed, old body file may have different extension — clean it up
        var oldBodyFile = FindBodyFile(ruleFolder);
        if (oldBodyFile != null
            && !string.Equals(Path.GetFullPath(oldBodyFile), Path.GetFullPath(bodyPath), StringComparison.OrdinalIgnoreCase))
        {
            File.Delete(oldBodyFile);
        }

        WriteFile(bodyPath, rule.Body);
    }

    private AutoResponderRule? LoadRuleFromFolder(string ruleFolder)
    {
        var matchPath = Path.Combine(ruleFolder, "match.json");
        var responsePath = Path.Combine(ruleFolder, "response.json");
        if (!File.Exists(matchPath)) return null;

        var match = JsonSerializer.Deserialize<MatchDto>(File.ReadAllText(matchPath), JsonOpts);
        if (match == null) return null;

        var rule = new AutoResponderRule
        {
            Id = match.Id,
            Name = match.Name,
            Method = match.Method,
            UrlPattern = match.UrlPattern,
            BodyPattern = match.BodyPattern,
            BodyPatternIsRegex = match.BodyPatternIsRegex,
            Enabled = match.Enabled,
        };

        // Load response metadata
        if (File.Exists(responsePath))
        {
            var resp = JsonSerializer.Deserialize<ResponseDto>(File.ReadAllText(responsePath), JsonOpts);
            if (resp != null)
            {
                rule.StatusCode = resp.StatusCode;
                rule.ContentType = resp.ContentType;
                rule.Headers = resp.Headers;
            }
        }

        // Load body from body.* file
        var bodyFile = FindBodyFile(ruleFolder);
        if (bodyFile != null)
            rule.Body = File.ReadAllText(bodyFile);

        return rule;
    }

    private static void DeleteRuleFolder(string ruleFolder)
    {
        if (Directory.Exists(ruleFolder))
            Directory.Delete(ruleFolder, recursive: true);
    }

    // --- Auto-responder ---

    public List<AutoResponderRule> LoadAutoResponderRules()
    {
        var rules = new List<AutoResponderRule>();
        if (!Directory.Exists(_autoResponderPath)) return rules;

        foreach (var matchFile in Directory.GetFiles(_autoResponderPath, "match.json", SearchOption.AllDirectories))
        {
            var folder = Path.GetDirectoryName(matchFile)!;
            try
            {
                var rule = LoadRuleFromFolder(folder);
                if (rule != null)
                {
                    rules.Add(rule);
                    _fileIndex[rule.Id] = Path.GetFullPath(folder);
                }
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Failed to load rule from {Folder}", folder);
            }
        }

        _log.LogInformation("Loaded {Count} auto-responder rules from disk", rules.Count);
        return rules;
    }

    public void SaveAutoResponderRule(AutoResponderRule rule)
    {
        var folder = RuleFolderPath(_autoResponderPath, rule);
        var expectedFull = Path.GetFullPath(folder);

        // If rule exists at a different path (URL/method changed), delete old folder
        if (_fileIndex.TryGetValue(rule.Id, out var oldFolder)
            && !string.Equals(oldFolder, expectedFull, StringComparison.OrdinalIgnoreCase)
            && Directory.Exists(oldFolder))
        {
            DeleteRuleFolder(oldFolder);
            CleanupEmptyDirs(oldFolder, _autoResponderPath);
        }

        SaveRuleFiles(folder, rule);
        _fileIndex[rule.Id] = expectedFull;
    }

    public void DeleteAutoResponderRule(Guid id)
    {
        if (_fileIndex.TryGetValue(id, out var folder))
        {
            DeleteRuleFolder(folder);
            CleanupEmptyDirs(folder, _autoResponderPath);
            _fileIndex.Remove(id);
        }
    }

    // --- Recordings ---

    public List<Recording> LoadRecordings()
    {
        var recordings = new List<Recording>();
        if (!Directory.Exists(_recordingsPath)) return recordings;

        foreach (var dir in Directory.GetDirectories(_recordingsPath))
        {
            var metaPath = Path.Combine(dir, "_meta.json");
            if (!File.Exists(metaPath)) continue;

            try
            {
                var metaJson = File.ReadAllText(metaPath);
                var meta = JsonSerializer.Deserialize<RecordingMeta>(metaJson, JsonOpts);
                if (meta == null) continue;

                var rec = new Recording
                {
                    Id = meta.Id,
                    Name = meta.Name,
                    CreatedAt = meta.CreatedAt,
                    Active = meta.Active,
                };

                // Load rule folders recursively (find match.json files)
                foreach (var matchFile in Directory.GetFiles(dir, "match.json", SearchOption.AllDirectories))
                {
                    var ruleFolder = Path.GetDirectoryName(matchFile)!;
                    try
                    {
                        var rule = LoadRuleFromFolder(ruleFolder);
                        if (rule != null)
                        {
                            rec.Rules.Add(rule);
                            _fileIndex[rule.Id] = Path.GetFullPath(ruleFolder);
                        }
                    }
                    catch (Exception ex)
                    {
                        _log.LogWarning(ex, "Failed to load recording rule from {Folder}", ruleFolder);
                    }
                }

                recordings.Add(rec);
                // Index the recording folder path by recording ID
                _fileIndex[rec.Id] = Path.GetFullPath(dir);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Failed to load recording from {Dir}", dir);
            }
        }

        _log.LogInformation("Loaded {Count} recordings from disk", recordings.Count);
        return recordings;
    }

    public void SaveRecordingMeta(Recording rec)
    {
        var slug = Slugify(rec.Name);
        var newDir = ResolveRecordingFolder(slug, rec.Id);

        // If recording folder exists at a different path (name changed), rename it
        if (_fileIndex.TryGetValue(rec.Id, out var oldDir)
            && Directory.Exists(oldDir)
            && !string.Equals(Path.GetFullPath(oldDir), Path.GetFullPath(newDir), StringComparison.OrdinalIgnoreCase))
        {
            Directory.Move(oldDir, newDir);
        }

        Directory.CreateDirectory(newDir);

        var meta = new RecordingMeta
        {
            Id = rec.Id,
            Name = rec.Name,
            CreatedAt = rec.CreatedAt,
            Active = rec.Active,
        };
        var path = Path.Combine(newDir, "_meta.json");
        var json = JsonSerializer.Serialize(meta, JsonOpts);
        WriteFile(path, json);
        _fileIndex[rec.Id] = Path.GetFullPath(newDir);
    }

    public void SaveRecordingRule(Recording rec, AutoResponderRule rule)
    {
        // Ensure recording folder path is known
        if (!_fileIndex.TryGetValue(rec.Id, out var dir))
        {
            var slug = Slugify(rec.Name);
            dir = ResolveRecordingFolder(slug, rec.Id);
            Directory.CreateDirectory(dir);
            _fileIndex[rec.Id] = Path.GetFullPath(dir);
        }

        var folder = RuleFolderPath(dir, rule);
        var expectedFull = Path.GetFullPath(folder);

        // If rule folder exists at different path (URL/method changed), delete old folder
        if (_fileIndex.TryGetValue(rule.Id, out var oldFolder)
            && !string.Equals(oldFolder, expectedFull, StringComparison.OrdinalIgnoreCase)
            && Directory.Exists(oldFolder))
        {
            DeleteRuleFolder(oldFolder);
            CleanupEmptyDirs(oldFolder, dir);
        }

        SaveRuleFiles(folder, rule);
        _fileIndex[rule.Id] = expectedFull;
    }

    public void DeleteRecordingRule(Recording rec, Guid ruleId)
    {
        if (_fileIndex.TryGetValue(ruleId, out var folder))
        {
            DeleteRuleFolder(folder);
            if (_fileIndex.TryGetValue(rec.Id, out var recDir))
                CleanupEmptyDirs(folder, recDir);
            _fileIndex.Remove(ruleId);
        }
    }

    public void DeleteRecording(Recording rec)
    {
        if (_fileIndex.TryGetValue(rec.Id, out var dir) && Directory.Exists(dir))
        {
            // Remove all indexed rule files for this recording
            foreach (var rule in rec.Rules)
                _fileIndex.Remove(rule.Id);

            Directory.Delete(dir, recursive: true);
            _fileIndex.Remove(rec.Id);
        }
    }

    // --- Analysis ---

    public List<AnalysisRun> LoadAnalysisRuns()
    {
        var runs = new List<AnalysisRun>();
        if (!Directory.Exists(_analysisPath)) return runs;

        foreach (var dir in Directory.GetDirectories(_analysisPath))
        {
            var runPath = Path.Combine(dir, "run.json");
            if (!File.Exists(runPath)) continue;

            try
            {
                var run = JsonSerializer.Deserialize<AnalysisRun>(File.ReadAllText(runPath), JsonOpts);
                if (run == null) continue;
                runs.Add(run);
                _fileIndex[run.Id] = Path.GetFullPath(dir);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Failed to load analysis run from {Dir}", dir);
            }
        }

        return runs.OrderByDescending(r => r.CreatedAt).ToList();
    }

    public void SaveAnalysisRunMeta(AnalysisRun run)
    {
        var slug = Slugify(run.Name);
        var newDir = ResolveAnalysisFolder(slug, run.Id);

        if (_fileIndex.TryGetValue(run.Id, out var oldDir)
            && Directory.Exists(oldDir)
            && !string.Equals(Path.GetFullPath(oldDir), Path.GetFullPath(newDir), StringComparison.OrdinalIgnoreCase))
        {
            Directory.Move(oldDir, newDir);
        }

        Directory.CreateDirectory(newDir);
        WriteFile(Path.Combine(newDir, "run.json"), JsonSerializer.Serialize(run, JsonOpts));
        _fileIndex[run.Id] = Path.GetFullPath(newDir);
    }

    public void SaveAnalysisEvent(AnalysisRun run, AnalysisEvent analysisEvent)
    {
        if (!_fileIndex.TryGetValue(run.Id, out var dir))
        {
            var slug = Slugify(run.Name);
            dir = ResolveAnalysisFolder(slug, run.Id);
            Directory.CreateDirectory(dir);
            _fileIndex[run.Id] = Path.GetFullPath(dir);
        }

        analysisEvent.FileName = BuildAnalysisFileName(analysisEvent);
        var filePath = Path.Combine(dir, analysisEvent.FileName);
        WriteFile(filePath, JsonSerializer.Serialize(analysisEvent, JsonOpts));
    }

    public List<AnalysisEventSummary> LoadAnalysisEventSummaries(AnalysisRun run)
    {
        if (!_fileIndex.TryGetValue(run.Id, out var dir) || !Directory.Exists(dir))
            return [];

        var summaries = new List<AnalysisEventSummary>();
        foreach (var file in Directory.GetFiles(dir, "*.json"))
        {
            if (Path.GetFileName(file).Equals("run.json", StringComparison.OrdinalIgnoreCase))
                continue;

            try
            {
                var analysisEvent = JsonSerializer.Deserialize<AnalysisEvent>(File.ReadAllText(file), JsonOpts);
                if (analysisEvent == null) continue;
                summaries.Add(new AnalysisEventSummary
                {
                    Sequence = analysisEvent.Sequence,
                    FileName = analysisEvent.FileName,
                    Timestamp = analysisEvent.Timestamp,
                    Method = analysisEvent.Method,
                    Url = analysisEvent.Url,
                    Host = analysisEvent.Host,
                    ResponseStatus = analysisEvent.ResponseStatus,
                    DurationMs = analysisEvent.DurationMs,
                });
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Failed to load analysis event from {File}", file);
            }
        }

        return summaries.OrderBy(e => e.Sequence).ToList();
    }

    public AnalysisEvent? LoadAnalysisEvent(AnalysisRun run, int sequence)
    {
        if (!_fileIndex.TryGetValue(run.Id, out var dir) || !Directory.Exists(dir))
            return null;

        var prefix = sequence.ToString("D6") + "_";
        var file = Directory.GetFiles(dir, prefix + "*.json").OrderBy(f => f, StringComparer.OrdinalIgnoreCase).FirstOrDefault();
        if (file == null) return null;

        try
        {
            return JsonSerializer.Deserialize<AnalysisEvent>(File.ReadAllText(file), JsonOpts);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Failed to load analysis event {Sequence} from {RunId}", sequence, run.Id);
            return null;
        }
    }

    public void DeleteAnalysisRun(AnalysisRun run)
    {
        if (_fileIndex.TryGetValue(run.Id, out var dir) && Directory.Exists(dir))
        {
            Directory.Delete(dir, recursive: true);
            _fileIndex.Remove(run.Id);
        }
    }

    private string ResolveAnalysisFolder(string baseName, Guid runId)
    {
        var candidate = Path.Combine(_analysisPath, baseName);
        if (IsAnalysisFolderOwned(candidate, runId))
            return candidate;

        for (var i = 1; ; i++)
        {
            candidate = Path.Combine(_analysisPath, $"{baseName}_{i}");
            if (IsAnalysisFolderOwned(candidate, runId))
                return candidate;
        }
    }

    private bool IsAnalysisFolderOwned(string folder, Guid runId)
    {
        var fullPath = Path.GetFullPath(folder);
        if (_fileIndex.TryGetValue(runId, out var indexed)
            && string.Equals(indexed, fullPath, StringComparison.OrdinalIgnoreCase))
            return true;

        if (!Directory.Exists(folder))
            return true;

        var runFile = Path.Combine(folder, "run.json");
        if (!File.Exists(runFile))
            return true;

        try
        {
            var run = JsonSerializer.Deserialize<AnalysisRun>(File.ReadAllText(runFile), JsonOpts);
            return run != null && run.Id == runId;
        }
        catch
        {
            return false;
        }
    }

    private static string BuildAnalysisFileName(AnalysisEvent analysisEvent)
    {
        var host = SanitizeAnalysisPart(analysisEvent.Host, 40);
        var method = SanitizeAnalysisPart(analysisEvent.Method.ToLowerInvariant(), 16);
        var path = "root";

        if (Uri.TryCreate(analysisEvent.Url, UriKind.Absolute, out var uri))
        {
            var rawPath = Uri.UnescapeDataString(uri.AbsolutePath).Trim('/');
            if (!string.IsNullOrWhiteSpace(rawPath))
                path = SanitizeAnalysisPart(rawPath.Replace('/', '_'), 80);
        }

        return $"{analysisEvent.Sequence:D6}_{host}_{path}_{method}.json";
    }

    private static string SanitizeAnalysisPart(string value, int maxLength)
    {
        var sanitized = Regex.Replace(value.ToLowerInvariant(), @"[^a-z0-9._-]+", "_").Trim('_');
        if (string.IsNullOrWhiteSpace(sanitized)) sanitized = "unknown";
        if (sanitized.Length > maxLength) sanitized = sanitized[..maxLength];
        return sanitized;
    }

    // --- FileSystemWatcher ---

    public void StartWatching()
    {
        _watcher = new FileSystemWatcher(_basePath)
        {
            IncludeSubdirectories = true,
            Filter = "*.*",
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.CreationTime,
        };

        _watcher.Created += OnFileEvent;
        _watcher.Changed += OnFileEvent;
        _watcher.Deleted += OnFileEvent;
        _watcher.Renamed += (_, e) => OnFileEvent(null, e);
        _watcher.EnableRaisingEvents = true;

        _log.LogInformation("FileSystemWatcher started on {Path}", _basePath);
    }

    private void OnFileEvent(object? sender, FileSystemEventArgs e)
    {
        var normalized = Path.GetFullPath(e.FullPath);

        // Skip self-writes
        lock (_lock)
        {
            if (_pendingWrites.Remove(normalized))
                return;
        }

        // Debounce: reset 500ms timer
        _debounceTimer?.Dispose();
        _debounceTimer = new Timer(_ =>
        {
            var relative = Path.GetRelativePath(_basePath, e.FullPath);
            if (relative.StartsWith("auto-responder"))
            {
                _log.LogInformation("Auto-responder rules changed on disk");
                OnAutoResponderChanged?.Invoke();
            }
            else if (relative.StartsWith("recordings"))
            {
                _log.LogInformation("Recordings changed on disk");
                OnRecordingsChanged?.Invoke();
            }
            else if (relative.StartsWith("analysis"))
            {
                _log.LogInformation("Analysis changed on disk");
                OnAnalysisChanged?.Invoke();
            }
        }, null, 500, Timeout.Infinite);
    }

    public void Dispose()
    {
        _watcher?.Dispose();
        _debounceTimer?.Dispose();
    }

    // --- Internal DTO for _meta.json (excludes Rules list) ---

    private class RecordingMeta
    {
        public Guid Id { get; set; }
        public string Name { get; set; } = "";
        public DateTime CreatedAt { get; set; }
        public bool Active { get; set; }
    }

    /// <summary>DTO for match.json — when to intercept</summary>
    private class MatchDto
    {
        public Guid Id { get; set; }
        public string Name { get; set; } = "";
        public string UrlPattern { get; set; } = "";
        public string Method { get; set; } = "*";
        public string BodyPattern { get; set; } = "";
        public bool BodyPatternIsRegex { get; set; }
        public bool Enabled { get; set; } = true;
    }

    /// <summary>DTO for response.json — what to respond with</summary>
    private class ResponseDto
    {
        public int StatusCode { get; set; } = 200;
        public string ContentType { get; set; } = "application/json";
        public Dictionary<string, string> Headers { get; set; } = new();
    }
}
