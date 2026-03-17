using System.Collections.Concurrent;
using EasyIntercept.AutoResponder;
using EasyIntercept.Models;

namespace EasyIntercept.Storage;

public class RecordingStore
{
    private readonly ConcurrentDictionary<Guid, Recording> _recordings = new();
    private Guid? _recordingId;

    public IEnumerable<Recording> GetAll() => _recordings.Values.ToArray();

    public Recording? Get(Guid id) =>
        _recordings.TryGetValue(id, out var rec) ? rec : null;

    public Recording Create(string name)
    {
        var rec = new Recording { Name = name };
        _recordings[rec.Id] = rec;
        return rec;
    }

    public bool Delete(Guid id)
    {
        if (_recordingId == id) _recordingId = null;
        if (_recordings.TryRemove(id, out var rec))
        {
            if (rec.Active) rec.Active = false;
            return true;
        }
        return false;
    }

    public Recording? Rename(Guid id, string name)
    {
        var rec = Get(id);
        if (rec == null) return null;
        rec.Name = name;
        return rec;
    }

    // --- Recording (capture) ---

    public Recording? StartRecording(string name)
    {
        var rec = Create(name);
        _recordingId = rec.Id;
        return rec;
    }

    public void StopRecording() => _recordingId = null;

    public Guid? RecordingId => _recordingId;

    public bool IsRecording => _recordingId.HasValue;

    public void CaptureSession(ProxySession session)
    {
        if (_recordingId == null) return;
        var rec = Get(_recordingId.Value);
        if (rec == null) { _recordingId = null; return; }

        // Dedup key: method + url + body
        var existing = rec.Rules.FirstOrDefault(r =>
            r.Method.Equals(session.Method, StringComparison.OrdinalIgnoreCase) &&
            r.UrlPattern == EscapeRegex(session.Url) &&
            r.BodyPattern == session.RequestBody);

        if (existing != null)
        {
            // Last response wins — update response fields
            existing.StatusCode = session.ResponseStatus;
            existing.ContentType = session.ResponseHeaders
                .FirstOrDefault(h => h.Key.Equals("Content-Type", StringComparison.OrdinalIgnoreCase)).Value
                ?? "application/octet-stream";
            existing.Body = session.ResponseBody;
        }
        else
        {
            var ct = session.ResponseHeaders
                .FirstOrDefault(h => h.Key.Equals("Content-Type", StringComparison.OrdinalIgnoreCase)).Value
                ?? "application/octet-stream";

            var url = new Uri(session.Url);
            rec.Rules.Add(new AutoResponderRule
            {
                Name = $"{session.Method} {url.AbsolutePath}",
                Method = session.Method,
                UrlPattern = EscapeRegex(session.Url),
                BodyPattern = session.RequestBody,
                BodyPatternIsRegex = false,
                Enabled = true,
                StatusCode = session.ResponseStatus,
                ContentType = ct,
                Body = session.ResponseBody,
            });
        }
    }

    // --- Activation ---

    public Guid? ActiveId => _recordings.Values.FirstOrDefault(r => r.Active)?.Id;

    public bool Activate(Guid id)
    {
        var target = Get(id);
        if (target == null) return false;

        // Deactivate any currently active
        foreach (var rec in _recordings.Values)
            rec.Active = false;

        target.Active = true;
        return true;
    }

    public bool Deactivate(Guid id)
    {
        var target = Get(id);
        if (target == null) return false;
        target.Active = false;
        return true;
    }

    public Recording? GetActive() =>
        _recordings.Values.FirstOrDefault(r => r.Active);

    // --- Rule management within a recording ---

    public AutoResponderRule? GetRule(Guid recordingId, Guid ruleId)
    {
        var rec = Get(recordingId);
        return rec?.Rules.FirstOrDefault(r => r.Id == ruleId);
    }

    public bool UpdateRule(Guid recordingId, AutoResponderRule updated)
    {
        var rec = Get(recordingId);
        if (rec == null) return false;
        var idx = rec.Rules.FindIndex(r => r.Id == updated.Id);
        if (idx < 0) return false;
        updated.Id = rec.Rules[idx].Id; // preserve Id
        rec.Rules[idx] = updated;
        return true;
    }

    public bool ToggleRule(Guid recordingId, Guid ruleId)
    {
        var rule = GetRule(recordingId, ruleId);
        if (rule == null) return false;
        rule.Enabled = !rule.Enabled;
        return true;
    }

    public bool DeleteRule(Guid recordingId, Guid ruleId)
    {
        var rec = Get(recordingId);
        if (rec == null) return false;
        return rec.Rules.RemoveAll(r => r.Id == ruleId) > 0;
    }

    private static string EscapeRegex(string literal) =>
        System.Text.RegularExpressions.Regex.Escape(literal);
}
