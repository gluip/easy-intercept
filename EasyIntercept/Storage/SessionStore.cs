using System.Collections.Concurrent;
using EasyIntercept.Models;

namespace EasyIntercept.Storage;

public class SessionStore
{
    private const int MaxSessions = 1000;
    private readonly ConcurrentQueue<ProxySession> _queue = new();
    private readonly ConcurrentDictionary<Guid, ProxySession> _index = new();

    public void Add(ProxySession session)
    {
        _queue.Enqueue(session);
        _index[session.Id] = session;

        while (_queue.Count > MaxSessions && _queue.TryDequeue(out var evicted))
            _index.TryRemove(evicted.Id, out _);
    }

    public ProxySession? Get(Guid id) =>
        _index.TryGetValue(id, out var session) ? session : null;

    public void Remove(Guid id)
    {
        _index.TryRemove(id, out _);
    }

    public void RemoveMany(IEnumerable<Guid> ids)
    {
        foreach (var id in ids)
            _index.TryRemove(id, out _);
    }

    public void Clear()
    {
        _index.Clear();
        while (_queue.TryDequeue(out _)) { }
    }

    public IEnumerable<ProxySession> GetAll() =>
        _queue.Where(s => _index.ContainsKey(s.Id)).Reverse();
}
