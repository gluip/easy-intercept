using System.Collections.Concurrent;
using EasyIntercept.Models;
using EasyIntercept.Persistence;

namespace EasyIntercept.Storage;

public class AnalysisStore
{
    private readonly ConcurrentDictionary<Guid, AnalysisRun> _runs = new();
    private readonly JsonPersistence _persistence;
    private readonly object _lock = new();
    private Guid? _runId;
    private int _sequence;

    public AnalysisStore(JsonPersistence persistence)
    {
        _persistence = persistence;
    }

    public void Init()
    {
        _runs.Clear();
        foreach (var run in _persistence.LoadAnalysisRuns())
            _runs[run.Id] = run;

        _runId = null;
        _sequence = 0;
    }

    public void ReloadFromDisk()
    {
        var currentId = _runId;
        var currentSequence = _sequence;
        Init();
        if (currentId.HasValue && _runs.ContainsKey(currentId.Value))
        {
            _runId = currentId;
            _sequence = Math.Max(currentSequence, _runs[currentId.Value].EventCount);
        }
    }

    public IEnumerable<AnalysisRun> GetAll() =>
        _runs.Values.OrderByDescending(r => r.CreatedAt).ToArray();

    public AnalysisRun? Get(Guid id) =>
        _runs.TryGetValue(id, out var run) ? run : null;

    public AnalysisRun StartRun(string name, string? hostFilter)
    {
        var run = new AnalysisRun
        {
            Name = string.IsNullOrWhiteSpace(name) ? $"Analysis {DateTime.Now:HH:mm:ss}" : name.Trim(),
            HostFilter = hostFilter?.Trim() ?? "",
        };

        lock (_lock)
        {
            _runs[run.Id] = run;
            _persistence.SaveAnalysisRunMeta(run);
            _runId = run.Id;
            _sequence = 0;
        }

        return run;
    }

    public void StopRun()
    {
        lock (_lock)
        {
            if (!_runId.HasValue) return;
            if (_runs.TryGetValue(_runId.Value, out var run))
            {
                run.StoppedAt = DateTime.UtcNow;
                run.EventCount = _sequence;
                _persistence.SaveAnalysisRunMeta(run);
            }

            _runId = null;
        }
    }

    public bool Delete(Guid id)
    {
        lock (_lock)
        {
            if (_runId == id) _runId = null;
            if (!_runs.TryRemove(id, out var run)) return false;
            _persistence.DeleteAnalysisRun(run);
            return true;
        }
    }

    public Guid? RunId => _runId;

    public bool IsRunning => _runId.HasValue;

    public object GetStatus() => new { RunId = _runId };

    public IReadOnlyList<AnalysisEventSummary> GetEventSummaries(Guid runId)
    {
        var run = Get(runId);
        return run == null ? [] : _persistence.LoadAnalysisEventSummaries(run);
    }

    public AnalysisEvent? GetEvent(Guid runId, int sequence)
    {
        var run = Get(runId);
        return run == null ? null : _persistence.LoadAnalysisEvent(run, sequence);
    }

    public void Capture(
        string method,
        string url,
        Dictionary<string, string> requestHeaders,
        byte[] requestBody,
        int responseStatus,
        Dictionary<string, string> responseHeaders,
        byte[] responseBody,
        long durationMs)
    {
        lock (_lock)
        {
            if (!_runId.HasValue) return;
            if (!_runs.TryGetValue(_runId.Value, out var run))
            {
                _runId = null;
                return;
            }

            if (!ShouldCapture(url, run.HostFilter)) return;

            var sequence = _sequence + 1;
            var now = DateTime.UtcNow;
            var requestContentType = requestHeaders.GetValueOrDefault("Content-Type", "");
            var responseContentType = responseHeaders.GetValueOrDefault("Content-Type", "");
            var requestBodyInfo = DescribeBody(requestBody, requestContentType);
            var responseBodyInfo = DescribeBody(responseBody, responseContentType);
            var host = TryGetHost(url);

            var analysisEvent = new AnalysisEvent
            {
                Sequence = sequence,
                Timestamp = now,
                Method = method,
                Url = url,
                Host = host,
                DurationMs = durationMs,
                RequestHeaders = new Dictionary<string, string>(requestHeaders, StringComparer.OrdinalIgnoreCase),
                RequestContentType = requestContentType,
                RequestBodyByteLength = requestBody.Length,
                RequestBody = requestBodyInfo.text,
                RequestBodySkippedReason = requestBodyInfo.skippedReason,
                ResponseStatus = responseStatus,
                ResponseHeaders = new Dictionary<string, string>(responseHeaders, StringComparer.OrdinalIgnoreCase),
                ResponseContentType = responseContentType,
                ResponseBodyByteLength = responseBody.Length,
                ResponseBody = responseBodyInfo.text,
                ResponseBodySkippedReason = responseBodyInfo.skippedReason,
            };

            _persistence.SaveAnalysisEvent(run, analysisEvent);

            _sequence = sequence;
            run.EventCount = sequence;
            _persistence.SaveAnalysisRunMeta(run);
        }
    }

    private static bool ShouldCapture(string url, string hostFilter)
    {
        if (string.IsNullOrWhiteSpace(hostFilter)) return true;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return false;
        return uri.Host.Contains(hostFilter.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    private static string TryGetHost(string url)
    {
        return Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.Host : "";
    }

    private static (string text, string? skippedReason) DescribeBody(byte[] body, string? contentType)
    {
        if (body.Length == 0) return ("", null);
        if (!IsTextContentType(contentType))
        {
            return ($"[body skipped: non-text content, {body.Length} bytes]", "non-text content");
        }

        try
        {
            var encoding = GetEncoding(contentType);
            return (encoding.GetString(body), null);
        }
        catch
        {
            return (System.Text.Encoding.UTF8.GetString(body), "decoded with UTF-8 fallback");
        }
    }

    private static bool IsTextContentType(string? contentType)
    {
        if (string.IsNullOrWhiteSpace(contentType)) return false;
        var lower = contentType.ToLowerInvariant();
        return lower.Contains("text/")
            || lower.Contains("json")
            || lower.Contains("xml")
            || lower.Contains("javascript")
            || lower.Contains("x-www-form-urlencoded")
            || lower.Contains("event-stream");
    }

    private static System.Text.Encoding GetEncoding(string? contentType)
    {
        if (!string.IsNullOrWhiteSpace(contentType))
        {
            foreach (var part in contentType.Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                var trimmed = part.Trim();
                if (!trimmed.StartsWith("charset=", StringComparison.OrdinalIgnoreCase)) continue;
                var charset = trimmed[8..].Trim().Trim('"');
                if (!string.IsNullOrWhiteSpace(charset))
                    return System.Text.Encoding.GetEncoding(charset);
            }
        }

        return System.Text.Encoding.UTF8;
    }
}