using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using EasyIntercept.AutoResponder;
using EasyIntercept.Models;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace EasyIntercept.Tests.Proxy;

/// <summary>
/// End-to-end through a real <see cref="EasyIntercept.Proxy.ProxyConnection"/> and a real Kestrel upstream.
/// The SSE server only writes the next event after the test signals it has read the previous one
/// through the proxy, so a proxy that buffers deadlocks (and fails on the timeout) instead of passing.
/// </summary>
public class ProxyStreamingTests : IClassFixture<ProxyFixture>
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);
    private readonly ProxyFixture _proxy;

    public ProxyStreamingTests(ProxyFixture proxy) => _proxy = proxy;

    private static TaskCompletionSource[] Gates(int n) =>
        Enumerable.Range(0, n).Select(_ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).ToArray();

    private static Task<TestServer> SseServerAsync(TaskCompletionSource[] gates, bool https = false, Action<HttpContext>? afterLastGate = null) =>
        TestServer.StartAsync(app => app.MapGet("/sse", async ctx =>
        {
            ctx.Response.ContentType = "text/event-stream";
            ctx.Response.Headers.CacheControl = "no-cache";
            for (var i = 0; i < gates.Length; i++)
            {
                await ctx.Response.WriteAsync($"data: event {i + 1}\n\n");
                await ctx.Response.Body.FlushAsync();
                await gates[i].Task.WaitAsync(Timeout);
            }
            afterLastGate?.Invoke(ctx);
        }), https);

    private Task<ProxySession> FinalSessionAsync(string baseUrl, string? exactPath = null) =>
        _proxy.Hub.WaitForAsync((m, s) => m == "UpdateSession" && s.ResponseComplete
            && (exactPath is null ? s.Url.StartsWith(baseUrl) : s.Url == baseUrl + exactPath), Timeout);

    private static int ClosedPort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    [Fact]
    public async Task Sse_events_reach_the_client_before_the_stream_ends()
    {
        var gates = Gates(3);
        await using var server = await SseServerAsync(gates);
        using var cts = new CancellationTokenSource(Timeout);
        using var client = await RawProxyClient.ConnectAsync(_proxy.ProxyPort);
        await client.SendAsync($"GET {server.BaseUrl}/sse HTTP/1.1\r\nHost: {server.HostPort}\r\n\r\n");

        var (status, headers) = await client.ReadHeadAsync(cts.Token);
        Assert.StartsWith("HTTP/1.1 200", status);
        Assert.Contains(headers, h => h.Key == "Transfer-Encoding" && h.Value == "chunked");
        Assert.Contains(headers, h => h.Key == "Content-Type" && h.Value.StartsWith("text/event-stream"));
        Assert.DoesNotContain(headers, h => h.Key.Equals("Content-Length", StringComparison.OrdinalIgnoreCase));

        var text = await client.ReadChunksUntilAsync("data: event 1\n\n", cts.Token);
        Assert.DoesNotContain("event 2", text); // the server has not sent it yet
        gates[0].SetResult();
        text += await client.ReadChunksUntilAsync("data: event 2\n\n", cts.Token);
        gates[1].SetResult();
        text += await client.ReadChunksUntilAsync("data: event 3\n\n", cts.Token);
        gates[2].SetResult();
        Assert.Null(await client.ReadChunkAsync(cts.Token)); // terminating chunk
        Assert.Equal("data: event 1\n\ndata: event 2\n\ndata: event 3\n\n", text);

        var final = await FinalSessionAsync(server.BaseUrl);
        Assert.Equal(200, final.ResponseStatus);
        Assert.Equal(text, final.ResponseBody);
        Assert.Equal(text.Length.ToString(), final.ResponseHeaders["Content-Length"]);
        Assert.False(final.ResponseHeaders.ContainsKey("Transfer-Encoding"));
        Assert.True(final.TimeToFirstByteMs <= final.DurationMs);

        var events = _proxy.Hub.For(final.Id);
        Assert.Equal("NewSession", events[0].Method);
        Assert.Equal(0, events[0].Session.ResponseStatus);
        Assert.False(events[0].Session.ResponseComplete);
        Assert.Equal("UpdateSession", events[1].Method);
        Assert.Equal(200, events[1].Session.ResponseStatus);
        Assert.False(events[1].Session.ResponseComplete);
        Assert.Equal("", events[1].Session.ResponseBody);
        Assert.Contains(events, e => !e.Session.ResponseComplete && e.Session.ResponseBody.StartsWith("data: event 1"));
        Assert.True(events[^1].Session.ResponseComplete);

        var onDisk = File.ReadAllText(_proxy.Store.GetFilePath(final.Id)!);
        Assert.Contains("\"ResponseComplete\": true", onDisk);
        Assert.Contains("event 3", onDisk);
    }

    [Fact]
    public async Task Sse_streams_through_a_tls_tunnel()
    {
        var gates = Gates(2);
        await using var server = await SseServerAsync(gates, https: true);
        using var cts = new CancellationTokenSource(Timeout);
        using var client = await RawProxyClient.ConnectAsync(_proxy.ProxyPort);
        await client.TunnelAsync(server.HostPort, tls: true);
        await client.SendAsync($"GET /sse HTTP/1.1\r\nHost: {server.HostPort}\r\n\r\n");

        var (status, headers) = await client.ReadHeadAsync(cts.Token);
        Assert.StartsWith("HTTP/1.1 200", status);
        Assert.Contains(headers, h => h.Key == "Transfer-Encoding" && h.Value == "chunked");

        await client.ReadChunksUntilAsync("data: event 1\n\n", cts.Token);
        gates[0].SetResult();
        await client.ReadChunksUntilAsync("data: event 2\n\n", cts.Token);
        gates[1].SetResult();
        Assert.Null(await client.ReadChunkAsync(cts.Token));

        var final = await FinalSessionAsync(server.BaseUrl);
        Assert.StartsWith("https://", final.Url);
        Assert.Equal("data: event 1\n\ndata: event 2\n\n", final.ResponseBody);
    }

    [Fact]
    public async Task Content_length_responses_pass_through_with_their_length()
    {
        var body = """{"ok":true,"n":1}""";
        await using var server = await TestServer.StartAsync(app => app.MapGet("/json", async ctx =>
        {
            ctx.Response.ContentType = "application/json";
            ctx.Response.ContentLength = body.Length;
            await ctx.Response.WriteAsync(body);
        }));

        using var http = _proxy.NewHttpClient();
        using var resp = await http.GetAsync($"{server.BaseUrl}/json");

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal(body.Length, resp.Content.Headers.ContentLength);
        Assert.NotEqual(true, resp.Headers.TransferEncodingChunked);
        Assert.Equal(body, await resp.Content.ReadAsStringAsync());

        var final = await FinalSessionAsync(server.BaseUrl);
        Assert.Equal(body, final.ResponseBody);
        Assert.Equal(body.Length.ToString(), final.ResponseHeaders["Content-Length"]);
        Assert.True(final.TimeToFirstByteMs <= final.DurationMs);
    }

    [Fact]
    public async Task Gzip_upstream_is_decompressed_and_reframed_as_chunked()
    {
        var plain = string.Concat(Enumerable.Repeat("hello gzip world ", 500));
        byte[] gz;
        using (var ms = new MemoryStream())
        {
            using (var g = new GZipStream(ms, CompressionLevel.Fastest, leaveOpen: true))
                g.Write(Encoding.UTF8.GetBytes(plain));
            gz = ms.ToArray();
        }
        await using var server = await TestServer.StartAsync(app => app.MapGet("/gz", async ctx =>
        {
            ctx.Response.ContentType = "text/plain";
            ctx.Response.Headers.ContentEncoding = "gzip";
            ctx.Response.ContentLength = gz.Length;
            await ctx.Response.Body.WriteAsync(gz);
        }));

        using var cts = new CancellationTokenSource(Timeout);
        using var client = await RawProxyClient.ConnectAsync(_proxy.ProxyPort);
        await client.SendAsync($"GET {server.BaseUrl}/gz HTTP/1.1\r\nHost: {server.HostPort}\r\nAccept-Encoding: gzip\r\n\r\n");
        var (status, headers) = await client.ReadHeadAsync(cts.Token);

        Assert.StartsWith("HTTP/1.1 200", status);
        Assert.DoesNotContain(headers, h => h.Key.Equals("Content-Encoding", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(headers, h => h.Key.Equals("Content-Length", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(headers, h => h.Key == "Transfer-Encoding" && h.Value == "chunked");

        var sb = new StringBuilder();
        while (await client.ReadChunkAsync(cts.Token) is { } chunk) sb.Append(Encoding.UTF8.GetString(chunk));
        Assert.Equal(plain, sb.ToString());

        var final = await FinalSessionAsync(server.BaseUrl);
        Assert.Equal(plain, final.ResponseBody);
        Assert.False(final.ResponseHeaders.ContainsKey("Content-Encoding"));
    }

    [Theory]
    [InlineData("GET", 204)]
    [InlineData("GET", 304)]
    [InlineData("HEAD", 200)]
    public async Task Responses_without_a_body_end_after_the_head(string method, int upstreamStatus)
    {
        await using var server = await TestServer.StartAsync(app => app.MapMethods("/nobody", ["GET", "HEAD"], async ctx =>
        {
            ctx.Response.StatusCode = upstreamStatus;
            if (upstreamStatus == 200)
            {
                ctx.Response.ContentType = "text/plain";
                ctx.Response.ContentLength = 5;
                await ctx.Response.WriteAsync("hello"); // Kestrel drops it for HEAD
            }
        }));

        using var cts = new CancellationTokenSource(Timeout);
        using var client = await RawProxyClient.ConnectAsync(_proxy.ProxyPort);
        await client.SendAsync($"{method} {server.BaseUrl}/nobody HTTP/1.1\r\nHost: {server.HostPort}\r\n\r\n");
        var (status, headers) = await client.ReadHeadAsync(cts.Token);

        Assert.StartsWith($"HTTP/1.1 {upstreamStatus}", status);
        Assert.DoesNotContain(headers, h => h.Key.Equals("Transfer-Encoding", StringComparison.OrdinalIgnoreCase));
        if (method == "HEAD") Assert.Contains(headers, h => h.Key == "Content-Length" && h.Value == "5");
        Assert.Empty(await client.ReadToEndAsync(cts.Token)); // the connection closes right after the head

        var final = await FinalSessionAsync(server.BaseUrl);
        Assert.Equal(upstreamStatus, final.ResponseStatus);
        Assert.Equal("", final.ResponseBody);
    }

    [Fact]
    public async Task Binary_streams_pass_through_unchanged_and_are_summarised()
    {
        var payload = new byte[3 * 10_000];
        RandomNumberGenerator.Fill(payload);
        await using var server = await TestServer.StartAsync(app => app.MapGet("/bin", async ctx =>
        {
            ctx.Response.ContentType = "application/octet-stream";
            for (var i = 0; i < 3; i++)
            {
                await ctx.Response.Body.WriteAsync(payload.AsMemory(i * 10_000, 10_000));
                await ctx.Response.Body.FlushAsync();
                await Task.Delay(30);
            }
        }));

        using var http = _proxy.NewHttpClient();
        var received = await http.GetByteArrayAsync($"{server.BaseUrl}/bin");
        Assert.Equal(payload, received);

        var final = await FinalSessionAsync(server.BaseUrl);
        Assert.Equal("[30000 bytes]", final.ResponseBody);
        Assert.All(_proxy.Hub.For(final.Id).Where(e => !e.Session.ResponseComplete), e => Assert.Equal("", e.Session.ResponseBody));
    }

    [Fact]
    public async Task Streams_beyond_the_capture_cap_still_reach_the_client_in_full()
    {
        const int megabytes = 20;
        var chunk = new byte[1024 * 1024];
        Array.Fill(chunk, (byte)'x');
        await using var server = await TestServer.StartAsync(app => app.MapGet("/big", async ctx =>
        {
            ctx.Response.ContentType = "text/plain";
            for (var i = 0; i < megabytes; i++)
            {
                await ctx.Response.Body.WriteAsync(chunk);
                await ctx.Response.Body.FlushAsync();
            }
        }));

        using var http = _proxy.NewHttpClient();
        using var resp = await http.GetAsync($"{server.BaseUrl}/big", HttpCompletionOption.ResponseHeadersRead);
        long total = 0;
        var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        await using (var body = await resp.Content.ReadAsStreamAsync())
        {
            var buf = new byte[81920];
            int n;
            while ((n = await body.ReadAsync(buf)) > 0) { total += n; hash.AppendData(buf, 0, n); }
        }
        Assert.Equal(megabytes * 1024L * 1024, total);
        var expected = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        for (var i = 0; i < megabytes; i++) expected.AppendData(chunk);
        Assert.Equal(expected.GetHashAndReset(), hash.GetHashAndReset());

        var final = await FinalSessionAsync(server.BaseUrl);
        Assert.Equal((megabytes * 1024L * 1024).ToString(), final.ResponseHeaders["Content-Length"]);
        Assert.Contains("[EasyIntercept: capture truncated at", final.ResponseBody);
        Assert.True(final.ResponseBody.Length < 17 * 1024 * 1024);
    }

    [Fact]
    public async Task Client_disconnect_stops_the_upstream_and_finalises_the_session()
    {
        var serverStopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = await TestServer.StartAsync(app => app.MapGet("/endless", async ctx =>
        {
            ctx.Response.ContentType = "text/event-stream";
            try
            {
                for (var i = 1; i <= 200; i++)
                {
                    await ctx.Response.WriteAsync($"data: event {i}\n\n", ctx.RequestAborted);
                    await ctx.Response.Body.FlushAsync(ctx.RequestAborted);
                    await Task.Delay(50, ctx.RequestAborted);
                }
            }
            catch (Exception) { /* the proxy dropped the upstream connection */ }
            finally { serverStopped.TrySetResult(); }
        }));

        using var cts = new CancellationTokenSource(Timeout);
        var client = await RawProxyClient.ConnectAsync(_proxy.ProxyPort);
        await client.SendAsync($"GET {server.BaseUrl}/endless HTTP/1.1\r\nHost: {server.HostPort}\r\n\r\n");
        await client.ReadHeadAsync(cts.Token);
        await client.ReadChunksUntilAsync("data: event 1\n\n", cts.Token);
        client.Dispose(); // walk away mid-stream

        var final = await FinalSessionAsync(server.BaseUrl);
        Assert.Equal(200, final.ResponseStatus);
        Assert.Contains("data: event 1", final.ResponseBody);
        Assert.Contains("client disconnected", final.ResponseBody);
        Assert.Equal("client disconnected", final.ResponseHeaders["X-EasyIntercept-Note"]);
        Assert.True(int.Parse(final.ResponseHeaders["Content-Length"]) < 200 * 16, "the proxy kept reading after the client left");

        await serverStopped.Task.WaitAsync(Timeout); // the upstream connection was torn down, not left streaming into the void
    }

    [Fact]
    public async Task Upstream_dropping_mid_body_is_passed_on_as_a_truncated_response()
    {
        var gates = Gates(1);
        await using var server = await SseServerAsync(gates, afterLastGate: ctx => ctx.Abort());

        using var cts = new CancellationTokenSource(Timeout);
        using var client = await RawProxyClient.ConnectAsync(_proxy.ProxyPort);
        await client.SendAsync($"GET {server.BaseUrl}/sse HTTP/1.1\r\nHost: {server.HostPort}\r\n\r\n");
        await client.ReadHeadAsync(cts.Token);
        await client.ReadChunksUntilAsync("data: event 1\n\n", cts.Token);
        gates[0].SetResult();

        // No terminating chunk: the connection just ends, which is how a truncated chunked body must look.
        await Assert.ThrowsAsync<EndOfStreamException>(() => client.ReadChunksUntilAsync("never", cts.Token));

        var final = await FinalSessionAsync(server.BaseUrl);
        Assert.Contains("data: event 1", final.ResponseBody);
        Assert.StartsWith("upstream error", final.ResponseHeaders["X-EasyIntercept-Note"]);
    }

    [Fact]
    public async Task Auto_responder_and_bad_gateway_paths_still_complete_sessions()
    {
        var mockUrl = $"http://127.0.0.1:{ClosedPort()}/mock";
        _proxy.AutoResponder.Add(new AutoResponderRule
        {
            Method = "GET",
            Url = mockUrl,
            ResponseStatus = 201,
            ResponseHeaders = { ["Content-Type"] = "application/json" },
            ResponseBody = """{"mock":true}""",
        });
        using var http = _proxy.NewHttpClient();

        using var mocked = await http.GetAsync(mockUrl);
        Assert.Equal(HttpStatusCode.Created, mocked.StatusCode);
        Assert.Equal("""{"mock":true}""", await mocked.Content.ReadAsStringAsync());
        Assert.True(mocked.Headers.Contains("X-EasyIntercept-AutoResponder"));
        var mockSession = await _proxy.Hub.WaitForAsync((m, s) => m == "UpdateSession" && s.ResponseComplete && s.Url == mockUrl, Timeout);
        Assert.Equal(201, mockSession.ResponseStatus);

        var deadUrl = $"http://127.0.0.1:{ClosedPort()}/dead";
        using var dead = await http.GetAsync(deadUrl);
        Assert.Equal(HttpStatusCode.BadGateway, dead.StatusCode);
        Assert.NotEmpty(await dead.Content.ReadAsStringAsync());
        var deadSession = await _proxy.Hub.WaitForAsync((m, s) => m == "UpdateSession" && s.ResponseComplete && s.Url == deadUrl, Timeout);
        Assert.Equal(502, deadSession.ResponseStatus);
        Assert.NotEmpty(deadSession.ResponseBody);
    }

    [Fact]
    public async Task Twenty_concurrent_streams_do_not_interfere()
    {
        await using var server = await TestServer.StartAsync(app => app.MapGet("/sse/{id:int}", async (int id, HttpContext ctx) =>
        {
            ctx.Response.ContentType = "text/event-stream";
            for (var i = 1; i <= 3; i++)
            {
                await ctx.Response.WriteAsync($"data: stream {id} event {i}\n\n");
                await ctx.Response.Body.FlushAsync();
                await Task.Delay(40);
            }
        }));

        using var http = _proxy.NewHttpClient();
        var bodies = await Task.WhenAll(Enumerable.Range(1, 20).Select(id => http.GetStringAsync($"{server.BaseUrl}/sse/{id}")));

        for (var id = 1; id <= 20; id++)
        {
            var expected = $"data: stream {id} event 1\n\ndata: stream {id} event 2\n\ndata: stream {id} event 3\n\n";
            Assert.Equal(expected, bodies[id - 1]);
            var final = await FinalSessionAsync(server.BaseUrl, $"/sse/{id}");
            Assert.Equal(expected, final.ResponseBody);
        }
    }
}
