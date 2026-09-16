using System.Collections.Concurrent;
using EasyIntercept.Hubs;
using EasyIntercept.Models;
using Microsoft.AspNetCore.SignalR;

namespace EasyIntercept.Tests.Proxy;

/// <summary>Stands in for SignalR: records every (method, session) the proxy publishes to all clients.</summary>
public sealed class RecordingHubContext : IHubContext<ProxyHub>
{
    public ConcurrentQueue<(string Method, ProxySession Session)> Events { get; } = new();

    public IHubClients Clients { get; }
    public IGroupManager Groups => throw new NotSupportedException();

    public RecordingHubContext()
    {
        Clients = new AllOnlyClients(new Recorder(this));
    }

    public List<(string Method, ProxySession Session)> For(Guid sessionId) =>
        Events.Where(e => e.Session.Id == sessionId).ToList();

    /// <summary>Polls until an event matches; throws after the timeout so a regression fails instead of hanging.</summary>
    public async Task<ProxySession> WaitForAsync(Func<string, ProxySession, bool> predicate, TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(10));
        while (DateTime.UtcNow < deadline)
        {
            foreach (var (method, session) in Events)
                if (predicate(method, session)) return session;
            await Task.Delay(20);
        }
        throw new TimeoutException($"No matching hub event within the timeout. Seen: {string.Join(", ", Events.Select(e => $"{e.Method}:{e.Session.ResponseStatus}/{e.Session.ResponseComplete}"))}");
    }

    private sealed class Recorder(RecordingHubContext owner) : IClientProxy
    {
        public Task SendCoreAsync(string method, object?[] args, CancellationToken cancellationToken = default)
        {
            if (args.Length > 0 && args[0] is ProxySession s)
                owner.Events.Enqueue((method, s));
            return Task.CompletedTask;
        }
    }

    private sealed class AllOnlyClients(IClientProxy all) : IHubClients
    {
        public IClientProxy All => all;
        public IClientProxy AllExcept(IReadOnlyList<string> excludedConnectionIds) => throw new NotSupportedException();
        public IClientProxy Client(string connectionId) => throw new NotSupportedException();
        public IClientProxy Clients(IReadOnlyList<string> connectionIds) => throw new NotSupportedException();
        public IClientProxy Group(string groupName) => throw new NotSupportedException();
        public IClientProxy Groups(IReadOnlyList<string> groupNames) => throw new NotSupportedException();
        public IClientProxy GroupExcept(string groupName, IReadOnlyList<string> excludedConnectionIds) => throw new NotSupportedException();
        public IClientProxy User(string userId) => throw new NotSupportedException();
        public IClientProxy Users(IReadOnlyList<string> userIds) => throw new NotSupportedException();
    }
}
