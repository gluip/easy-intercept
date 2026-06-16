using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.RegularExpressions;
using EasyIntercept.Models;

namespace EasyIntercept.Storage;

public class SessionStore
{
    private const int MaxSessions = 1000;
    private readonly ConcurrentQueue<ProxySession> _queue = new();
    private readonly ConcurrentDictionary<Guid, ProxySession> _index = new();
    private readonly ConcurrentDictionary<Guid, string> _files = new();
    private readonly string _dir;
    private int _counter;

    private static readonly JsonSerializerOptions _json = new() { WriteIndented = true };

    public SessionStore(IConfiguration config)
    {
        var raw = config["SessionsPath"] ?? "sessions";
        _dir = Path.IsPathRooted(raw) ? raw : Path.Combine(Directory.GetCurrentDirectory(), raw);
        Directory.CreateDirectory(_dir);
        LoadFromDisk();
    }

    public void Add(ProxySession session)
    {
        _queue.Enqueue(session);
        _index[session.Id] = session;

        var n = Interlocked.Increment(ref _counter);
        var fileName = BuildFileName(n, session);
        var path = Path.Combine(_dir, fileName);
        _files[session.Id] = path;
        File.WriteAllText(path, JsonSerializer.Serialize(session, _json));

        while (_queue.Count > MaxSessions && _queue.TryDequeue(out var evicted))
        {
            _index.TryRemove(evicted.Id, out _);
            if (_files.TryRemove(evicted.Id, out var old))
                TryDelete(old);
        }
    }

    public ProxySession? Get(Guid id) =>
        _index.TryGetValue(id, out var session) ? session : null;

    public void Update(ProxySession session)
    {
        if (!_index.ContainsKey(session.Id)) return;
        _index[session.Id] = session;
        if (_files.TryGetValue(session.Id, out var path))
            File.WriteAllText(path, JsonSerializer.Serialize(session, _json));
    }

    public void Remove(Guid id)
    {
        _index.TryRemove(id, out _);
        if (_files.TryRemove(id, out var path))
            TryDelete(path);
    }

    public void RemoveMany(IEnumerable<Guid> ids)
    {
        foreach (var id in ids)
            Remove(id);
    }

    public void Clear()
    {
        _index.Clear();
        while (_queue.TryDequeue(out _)) { }
        foreach (var path in _files.Values)
            TryDelete(path);
        _files.Clear();
    }

    public IEnumerable<ProxySession> GetAll() =>
        _queue.Where(s => _index.ContainsKey(s.Id)).Select(s => _index[s.Id]).Reverse();

    private static string BuildFileName(int n, ProxySession s)
    {
        var uri = new Uri(s.Url);
        var host = Sanitize(uri.Host);
        var path = Sanitize(uri.AbsolutePath.TrimStart('/'));
        if (path.Length > 60) path = path[..60];
        var name = $"{n:D4}_{s.Method}_{host}_{path}".TrimEnd('_');
        return name + ".json";
    }

    private static string Sanitize(string s) =>
        Regex.Replace(s, @"[^a-zA-Z0-9\-]", "_").Trim('_');

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch { }
    }

    private void LoadFromDisk()
    {
        var files = Directory.GetFiles(_dir, "*.json").OrderBy(f => f).ToArray();
        foreach (var path in files)
        {
            try
            {
                var session = JsonSerializer.Deserialize<ProxySession>(File.ReadAllText(path), _json);
                if (session is null) continue;
                _queue.Enqueue(session);
                _index[session.Id] = session;
                _files[session.Id] = path;

                // Parse sequence number from filename to continue counter
                var numStr = Path.GetFileName(path).Split('_')[0];
                if (int.TryParse(numStr, out var num) && num > _counter)
                    _counter = num;
            }
            catch { }
        }
    }
}
