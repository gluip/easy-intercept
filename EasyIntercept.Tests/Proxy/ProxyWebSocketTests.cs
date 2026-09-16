using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using EasyIntercept.Models;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace EasyIntercept.Tests.Proxy;

/// <summary>WebSockets end-to-end: a real ClientWebSocket through the proxy to a real Kestrel WebSocket server.</summary>
public class ProxyWebSocketTests : IClassFixture<ProxyFixture>
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);
    private readonly ProxyFixture _proxy;

    public ProxyWebSocketTests(ProxyFixture proxy) => _proxy = proxy;

    private sealed class ServerObservations
    {
        public TaskCompletionSource<(WebSocketCloseStatus? Status, string? Reason)> ClientClose { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private static Task<TestServer> WsServerAsync(ServerObservations? obs = null, bool https = false) =>
        TestServer.StartAsync(app =>
        {
            app.Map("/echo", async (HttpContext ctx) =>
            {
                using var ws = await ctx.WebSockets.AcceptWebSocketAsync();
                await EchoAsync(ws, obs, ctx.RequestAborted);
            });
            app.Map("/proto", async (HttpContext ctx) =>
            {
                using var ws = await ctx.WebSockets.AcceptWebSocketAsync("b");
                await EchoAsync(ws, obs, ctx.RequestAborted);
            });
            app.Map("/headers", async (HttpContext ctx) =>
            {
                using var ws = await ctx.WebSockets.AcceptWebSocketAsync();
                var seen = JsonSerializer.Serialize(new
                {
                    cookie = ctx.Request.Headers.Cookie.ToString(),
                    authorization = ctx.Request.Headers.Authorization.ToString(),
                    xTest = ctx.Request.Headers["X-Test"].ToString(),
                    key = ctx.Request.Headers.SecWebSocketKey.ToString(),
                });
                await ws.SendAsync(Encoding.UTF8.GetBytes(seen), WebSocketMessageType.Text, true, ctx.RequestAborted);
                await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", ctx.RequestAborted);
            });
            app.Map("/fragments", async (HttpContext ctx) =>
            {
                using var ws = await ctx.WebSockets.AcceptWebSocketAsync();
                await ws.SendAsync("part1-"u8.ToArray(), WebSocketMessageType.Text, endOfMessage: false, ctx.RequestAborted);
                await ws.SendAsync("part2-"u8.ToArray(), WebSocketMessageType.Text, endOfMessage: false, ctx.RequestAborted);
                await ws.SendAsync("part3"u8.ToArray(), WebSocketMessageType.Text, endOfMessage: true, ctx.RequestAborted);
                await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", ctx.RequestAborted);
            });
            app.Map("/serverclose", async (HttpContext ctx) =>
            {
                using var ws = await ctx.WebSockets.AcceptWebSocketAsync();
                await ws.CloseAsync((WebSocketCloseStatus)4000, "server done", ctx.RequestAborted);
            });
            app.Map("/burst/{n:int}", async (int n, HttpContext ctx) =>
            {
                using var ws = await ctx.WebSockets.AcceptWebSocketAsync();
                for (var i = 0; i < n; i++)
                    await ws.SendAsync(Encoding.UTF8.GetBytes($"m{i}"), WebSocketMessageType.Text, true, ctx.RequestAborted);
                await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "burst done", ctx.RequestAborted);
            });
            app.Map("/slow", async (HttpContext ctx) =>
            {
                using var ws = await ctx.WebSockets.AcceptWebSocketAsync();
                for (var i = 1; i <= 5; i++)
                {
                    await ws.SendAsync(Encoding.UTF8.GetBytes($"tick {i}"), WebSocketMessageType.Text, true, ctx.RequestAborted);
                    await Task.Delay(250, ctx.RequestAborted);
                }
                await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "slow done", ctx.RequestAborted);
            });
            app.Map("/nope", (HttpContext ctx) => { ctx.Response.StatusCode = 404; return Task.CompletedTask; });
        }, https);

    private static async Task EchoAsync(WebSocket ws, ServerObservations? obs, CancellationToken ct)
    {
        var buffer = new byte[64 * 1024];
        using var message = new MemoryStream();
        while (ws.State == WebSocketState.Open)
        {
            var r = await ws.ReceiveAsync(buffer, ct);
            if (r.MessageType == WebSocketMessageType.Close)
            {
                obs?.ClientClose.TrySetResult((ws.CloseStatus, ws.CloseStatusDescription));
                await ws.CloseAsync(ws.CloseStatus ?? WebSocketCloseStatus.NormalClosure, ws.CloseStatusDescription, ct);
                return;
            }
            message.Write(buffer, 0, r.Count);
            if (r.EndOfMessage)
            {
                await ws.SendAsync(message.ToArray(), r.MessageType, true, ct);
                message.SetLength(0);
            }
        }
    }

    private ClientWebSocket NewClient(bool trustProxyCert = false)
    {
        var ws = new ClientWebSocket();
        ws.Options.Proxy = _proxy.WebProxy;
        ws.Options.CollectHttpResponseDetails = true;
        if (trustProxyCert) ws.Options.RemoteCertificateValidationCallback = (_, _, _, _) => true;
        return ws;
    }

    private static string WsUrl(TestServer server, string path) =>
        $"{(server.Scheme == "https" ? "wss" : "ws")}://{server.HostPort}{path}";

    private static async Task<(WebSocketMessageType Type, byte[] Data)> ReceiveMessageAsync(WebSocket ws, CancellationToken ct)
    {
        var buffer = new byte[64 * 1024];
        using var ms = new MemoryStream();
        while (true)
        {
            var r = await ws.ReceiveAsync(buffer, ct);
            if (r.MessageType == WebSocketMessageType.Close) return (WebSocketMessageType.Close, []);
            ms.Write(buffer, 0, r.Count);
            if (r.EndOfMessage) return (r.MessageType, ms.ToArray());
        }
    }

    private static async Task<string> ReceiveTextAsync(WebSocket ws, CancellationToken ct)
    {
        var (type, data) = await ReceiveMessageAsync(ws, ct);
        Assert.Equal(WebSocketMessageType.Text, type);
        return Encoding.UTF8.GetString(data);
    }

    private Task<ProxySession> FinalSessionAsync(string wsUrl) =>
        _proxy.Hub.WaitForAsync((m, s) => m == "UpdateSession" && s.ResponseComplete && s.Url == wsUrl, Timeout);

    [Fact]
    public async Task Text_echo_round_trips_and_is_captured_in_both_directions()
    {
        var obs = new ServerObservations();
        await using var server = await WsServerAsync(obs);
        using var cts = new CancellationTokenSource(Timeout);
        using var ws = NewClient();
        var url = WsUrl(server, "/echo");

        await ws.ConnectAsync(new Uri(url), cts.Token);
        await ws.SendAsync("hello"u8.ToArray(), WebSocketMessageType.Text, true, cts.Token);
        Assert.Equal("hello", await ReceiveTextAsync(ws, cts.Token));
        await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", cts.Token);

        var final = await FinalSessionAsync(url);
        Assert.Equal("GET", final.Method);
        Assert.Equal(101, final.ResponseStatus);
        Assert.Equal("websocket", final.ResponseHeaders["Upgrade"]);
        Assert.True(final.ResponseHeaders.ContainsKey("Sec-WebSocket-Accept"));
        Assert.False(final.ResponseHeaders.ContainsKey("X-EasyIntercept-Note"));
        Assert.True(final.TimeToFirstByteMs <= final.DurationMs);

        var messages = final.WebSocketMessages!;
        Assert.Equal(["out", "in", "out"], messages.Select(m => m.Direction));
        Assert.Equal(["text", "text", "close"], messages.Select(m => m.Type));
        Assert.Equal("hello", messages[0].Data);
        Assert.Equal("hello", messages[1].Data);
        Assert.Equal("1000 bye", messages[2].Data);
        Assert.True(messages[0].OffsetMs <= messages[1].OffsetMs && messages[1].OffsetMs <= messages[2].OffsetMs);

        var (status, reason) = await obs.ClientClose.Task.WaitAsync(Timeout);
        Assert.Equal(WebSocketCloseStatus.NormalClosure, status);
        Assert.Equal("bye", reason);

        var onDisk = File.ReadAllText(_proxy.Store.GetFilePath(final.Id)!);
        Assert.Contains("\"WebSocketMessages\"", onDisk);
        Assert.Contains("\"ResponseComplete\": true", onDisk);
    }

    [Fact]
    public async Task Binary_echo_of_one_megabyte_is_intact_and_summarised()
    {
        await using var server = await WsServerAsync();
        using var cts = new CancellationTokenSource(Timeout);
        using var ws = NewClient();
        var url = WsUrl(server, "/echo");
        var payload = new byte[1024 * 1024];
        Random.Shared.NextBytes(payload);

        await ws.ConnectAsync(new Uri(url), cts.Token);
        await ws.SendAsync(payload, WebSocketMessageType.Binary, true, cts.Token);
        var (type, data) = await ReceiveMessageAsync(ws, cts.Token);
        Assert.Equal(WebSocketMessageType.Binary, type);
        Assert.Equal(payload, data);
        await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, null, cts.Token);

        var final = await FinalSessionAsync(url);
        Assert.Equal("[1048576 bytes binary]", final.WebSocketMessages![0].Data);
        Assert.Equal("binary", final.WebSocketMessages[0].Type);
        Assert.Equal("[1048576 bytes binary]", final.WebSocketMessages[1].Data);
    }

    [Fact]
    public async Task Fragmented_message_is_delivered_whole_and_captured_once()
    {
        await using var server = await WsServerAsync();
        using var cts = new CancellationTokenSource(Timeout);
        using var ws = NewClient();
        var url = WsUrl(server, "/fragments");

        await ws.ConnectAsync(new Uri(url), cts.Token);
        Assert.Equal("part1-part2-part3", await ReceiveTextAsync(ws, cts.Token));
        var (closeType, _) = await ReceiveMessageAsync(ws, cts.Token);
        Assert.Equal(WebSocketMessageType.Close, closeType);
        await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, null, cts.Token);

        var final = await FinalSessionAsync(url);
        var texts = final.WebSocketMessages!.Where(m => m.Type == "text").ToList();
        Assert.Single(texts);
        Assert.Equal("part1-part2-part3", texts[0].Data);
        Assert.Equal("in", texts[0].Direction);
    }

    [Fact]
    public async Task Sub_protocol_chosen_by_the_server_reaches_the_client()
    {
        await using var server = await WsServerAsync();
        using var cts = new CancellationTokenSource(Timeout);
        using var ws = NewClient();
        ws.Options.AddSubProtocol("a");
        ws.Options.AddSubProtocol("b");
        var url = WsUrl(server, "/proto");

        await ws.ConnectAsync(new Uri(url), cts.Token);
        Assert.Equal("b", ws.SubProtocol);
        await ws.SendAsync("x"u8.ToArray(), WebSocketMessageType.Text, true, cts.Token);
        Assert.Equal("x", await ReceiveTextAsync(ws, cts.Token));
        await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, null, cts.Token);

        var final = await FinalSessionAsync(url);
        Assert.Equal("b", final.ResponseHeaders["Sec-WebSocket-Protocol"]);
    }

    [Fact]
    public async Task Handshake_headers_are_forwarded_and_the_accept_key_matches_the_client_key()
    {
        await using var server = await WsServerAsync();
        using var cts = new CancellationTokenSource(Timeout);
        using var raw = await RawProxyClient.ConnectAsync(_proxy.ProxyPort);
        await raw.SendAsync(
            $"GET {server.BaseUrl}/headers HTTP/1.1\r\n" +
            $"Host: {server.HostPort}\r\n" +
            "Upgrade: websocket\r\nConnection: Upgrade\r\n" +
            "Sec-WebSocket-Key: dGhlIHNhbXBsZSBub25jZQ==\r\nSec-WebSocket-Version: 13\r\n" +
            "Cookie: session=abc\r\nAuthorization: Bearer t0k\r\nX-Test: yes\r\n\r\n");

        var (status, headers) = await raw.ReadHeadAsync(cts.Token);
        Assert.StartsWith("HTTP/1.1 101", status);
        Assert.Contains(headers, h => h.Key == "Sec-WebSocket-Accept" && h.Value == "s3pPLMBiTxaQ9kYGzzhZRbK+xOo=");
        Assert.Contains(headers, h => h.Key == "Upgrade" && h.Value == "websocket");
        Assert.Contains(headers, h => h.Key == "Connection" && h.Value == "Upgrade");

        var seen = JsonDocument.Parse(await ReadTextFrameAsync(raw, cts.Token)).RootElement;
        Assert.Equal("session=abc", seen.GetProperty("cookie").GetString());
        Assert.Equal("Bearer t0k", seen.GetProperty("authorization").GetString());
        Assert.Equal("yes", seen.GetProperty("xTest").GetString());
        Assert.NotEqual("dGhlIHNhbXBsZSBub25jZQ==", seen.GetProperty("key").GetString()); // upstream handshake has its own key
    }

    /// <summary>Server-to-client frames are unmasked; enough of RFC 6455 §5.2 to read one text frame.</summary>
    private static async Task<string> ReadTextFrameAsync(RawProxyClient raw, CancellationToken ct)
    {
        var header = await raw.ReadExactAsync(2, ct);
        Assert.Equal(0x1, header[0] & 0x0F); // text opcode
        long len = header[1] & 0x7F;
        if (len == 126) len = (await raw.ReadExactAsync(2, ct)) is var b ? (b[0] << 8) | b[1] : 0;
        else if (len == 127) len = BitConverter.ToInt64((await raw.ReadExactAsync(8, ct)).Reverse().ToArray());
        return Encoding.UTF8.GetString(await raw.ReadExactAsync((int)len, ct));
    }

    [Fact]
    public async Task Server_initiated_close_reaches_the_client_and_the_session()
    {
        await using var server = await WsServerAsync();
        using var cts = new CancellationTokenSource(Timeout);
        using var ws = NewClient();
        var url = WsUrl(server, "/serverclose");

        await ws.ConnectAsync(new Uri(url), cts.Token);
        var (type, _) = await ReceiveMessageAsync(ws, cts.Token);
        Assert.Equal(WebSocketMessageType.Close, type);
        Assert.Equal((WebSocketCloseStatus)4000, ws.CloseStatus);
        Assert.Equal("server done", ws.CloseStatusDescription);
        await ws.CloseAsync((WebSocketCloseStatus)4000, "server done", cts.Token);

        var final = await FinalSessionAsync(url);
        var close = Assert.Single(final.WebSocketMessages!, m => m.Type == "close");
        Assert.Equal("in", close.Direction);
        Assert.Equal("4000 server done", close.Data);
    }

    [Fact]
    public async Task Refused_upstream_handshakes_are_reported_with_the_upstream_status()
    {
        await using var server = await WsServerAsync();
        using var cts = new CancellationTokenSource(Timeout);

        using (var ws = NewClient())
        {
            var url = WsUrl(server, "/nope");
            var ex = await Assert.ThrowsAsync<WebSocketException>(() => ws.ConnectAsync(new Uri(url), cts.Token));
            Assert.Equal(HttpStatusCode.NotFound, ws.HttpStatusCode);
            var session = await FinalSessionAsync(url);
            Assert.Equal(404, session.ResponseStatus);
        }

        var closed = new TcpListener(IPAddress.Loopback, 0);
        closed.Start();
        var deadPort = ((IPEndPoint)closed.LocalEndpoint).Port;
        closed.Stop();
        using (var ws = NewClient())
        {
            var url = $"ws://127.0.0.1:{deadPort}/dead";
            await Assert.ThrowsAsync<WebSocketException>(() => ws.ConnectAsync(new Uri(url), cts.Token));
            Assert.Equal(HttpStatusCode.BadGateway, ws.HttpStatusCode);
            var session = await FinalSessionAsync(url);
            Assert.Equal(502, session.ResponseStatus);
            Assert.NotEmpty(session.ResponseBody);
        }
    }

    [Fact]
    public async Task Secure_websockets_go_through_the_tls_tunnel()
    {
        await using var server = await WsServerAsync(https: true);
        using var cts = new CancellationTokenSource(Timeout);
        using var ws = NewClient(trustProxyCert: true);
        var url = WsUrl(server, "/echo");
        Assert.StartsWith("wss://", url);

        await ws.ConnectAsync(new Uri(url), cts.Token);
        await ws.SendAsync("secure"u8.ToArray(), WebSocketMessageType.Text, true, cts.Token);
        Assert.Equal("secure", await ReceiveTextAsync(ws, cts.Token));
        await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, null, cts.Token);

        var final = await FinalSessionAsync(url);
        Assert.Equal("secure", final.WebSocketMessages![0].Data);
    }

    [Fact]
    public async Task Messages_are_pushed_to_the_ui_while_the_socket_is_open()
    {
        await using var server = await WsServerAsync();
        using var cts = new CancellationTokenSource(Timeout);
        using var ws = NewClient();
        var url = WsUrl(server, "/slow");

        await ws.ConnectAsync(new Uri(url), cts.Token);
        for (var i = 1; i <= 5; i++) Assert.Equal($"tick {i}", await ReceiveTextAsync(ws, cts.Token));
        await ReceiveMessageAsync(ws, cts.Token); // close
        await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, null, cts.Token);

        var final = await FinalSessionAsync(url);
        var interim = _proxy.Hub.For(final.Id)
            .Where(e => e.Method == "UpdateSession" && !e.Session.ResponseComplete && e.Session.WebSocketMessages is { Count: > 0 })
            .Select(e => e.Session.WebSocketMessages!.Count)
            .Distinct()
            .ToList();
        Assert.True(interim.Count >= 2, $"expected growing interim updates, got counts [{string.Join(",", interim)}]");
        Assert.Equal(6, final.WebSocketMessages!.Count); // 5 ticks + close
    }

    [Fact]
    public async Task Capture_is_capped_but_every_message_still_reaches_the_client()
    {
        await using var server = await WsServerAsync();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var ws = NewClient();
        var url = WsUrl(server, "/burst/1100");

        await ws.ConnectAsync(new Uri(url), cts.Token);
        for (var i = 0; i < 1100; i++) Assert.Equal($"m{i}", await ReceiveTextAsync(ws, cts.Token));
        await ReceiveMessageAsync(ws, cts.Token); // close
        await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, null, cts.Token);

        var final = await FinalSessionAsync(url);
        var messages = final.WebSocketMessages!;
        Assert.Equal(1002, messages.Count); // 1000 kept + note + close
        Assert.Equal("m999", messages[999].Data);
        Assert.Equal("note", messages[1000].Type);
        Assert.Contains("100 more messages", messages[1000].Data);
        Assert.Equal("close", messages[1001].Type);
    }
}
