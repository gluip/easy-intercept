using System.Text.Json;
using EasyIntercept.Models;
using EasyIntercept.Storage;
using Xunit;

namespace EasyIntercept.Tests.Proxy;

public class SessionStoreTests : IDisposable
{
    private readonly AppPaths _paths = new(Path.Combine(Path.GetTempPath(), "easyintercept-store-" + Guid.NewGuid().ToString("N")));

    private static ProxySession NewSession() => new()
    {
        Method = "GET",
        Url = "https://example.com/stream",
        ResponseStatus = 0,
        ResponseComplete = false,
    };

    [Fact]
    public void UpdateInMemory_changes_the_snapshot_but_not_the_file()
    {
        var store = new SessionStore(_paths);
        var s = NewSession();
        store.Add(s);
        var path = store.GetFilePath(s.Id)!;
        var before = File.ReadAllText(path);

        store.UpdateInMemory(s with { ResponseStatus = 200, ResponseBody = "partial" });

        Assert.Equal("partial", store.Get(s.Id)!.ResponseBody);
        Assert.Equal(before, File.ReadAllText(path));
    }

    [Fact]
    public void Update_writes_the_file()
    {
        var store = new SessionStore(_paths);
        var s = NewSession();
        store.Add(s);

        store.Update(s with { ResponseStatus = 200, ResponseBody = "done", ResponseComplete = true });

        var onDisk = JsonSerializer.Deserialize<ProxySession>(File.ReadAllText(store.GetFilePath(s.Id)!))!;
        Assert.Equal("done", onDisk.ResponseBody);
        Assert.True(onDisk.ResponseComplete);
    }

    [Fact]
    public void UpdateInMemory_ignores_unknown_sessions()
    {
        var store = new SessionStore(_paths);
        var s = NewSession();
        store.UpdateInMemory(s);
        Assert.Null(store.Get(s.Id));
    }

    [Fact]
    public void Files_from_before_the_streaming_fields_load_with_safe_defaults()
    {
        var legacy = """
            {
              "Id": "6f1c2a3e-1111-2222-3333-444455556666",
              "Timestamp": "2026-01-01T00:00:00Z",
              "Method": "GET",
              "Url": "https://example.com/old",
              "RequestHeaders": {},
              "RequestBody": "",
              "ResponseStatus": 200,
              "ResponseHeaders": {},
              "ResponseBody": "hello",
              "DurationMs": 12
            }
            """;
        File.WriteAllText(Path.Combine(_paths.Sessions, "0001_GET_example-com_old.json"), legacy);

        var store = new SessionStore(_paths);
        var s = store.Get(Guid.Parse("6f1c2a3e-1111-2222-3333-444455556666"))!;

        Assert.True(s.ResponseComplete);
        Assert.Equal(0, s.TimeToFirstByteMs);
        Assert.Null(s.WebSocketMessages);
    }

    [Fact]
    public void WebSocket_messages_round_trip_through_the_file()
    {
        var store = new SessionStore(_paths);
        var s = NewSession() with
        {
            ResponseStatus = 101,
            ResponseComplete = true,
            WebSocketMessages = [new("out", 5, "text", "hi"), new("in", 9, "close", "1000 bye")],
        };
        store.Add(s);

        var reloaded = new SessionStore(_paths).Get(s.Id)!;
        Assert.Equal(s.WebSocketMessages, reloaded.WebSocketMessages);
    }

    public void Dispose()
    {
        try { Directory.Delete(_paths.Root, recursive: true); } catch { }
    }
}
