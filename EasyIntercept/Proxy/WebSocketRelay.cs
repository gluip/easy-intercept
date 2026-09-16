using System.Diagnostics;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;

namespace EasyIntercept.Proxy;

/// <summary>
/// Relays a WebSocket between the proxy client and the upstream server. Both ends are terminated with
/// the framework's WebSocket implementation, so no frame parsing of our own: the client side is
/// <see cref="WebSocket.CreateFromStream(Stream, WebSocketCreationOptions)"/> over the (TLS) connection
/// we already hold, the upstream side a <see cref="ClientWebSocket"/>. Every message is forwarded
/// fragment by fragment as it arrives and captured for the session.
/// </summary>
public sealed class WebSocketRelay
{
    public const string AcceptGuid = "258EAFA5-E914-47DA-95CA-C5AB0DC85B11";
    public const int BufferSize = 64 * 1024;
    private static readonly TimeSpan CloseGrace = TimeSpan.FromSeconds(2);

    /// <summary>Headers the handshake itself owns; everything else is forwarded to the upstream handshake.</summary>
    private static readonly HashSet<string> HandshakeOwnedHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Host", "Connection", "Upgrade", "Keep-Alive", "Proxy-Connection", "Proxy-Authorization",
        "TE", "Trailers", "Transfer-Encoding", "Content-Length",
        "Sec-WebSocket-Key", "Sec-WebSocket-Version", "Sec-WebSocket-Extensions", "Sec-WebSocket-Protocol",
    };

    public static string ComputeAccept(string key) =>
        Convert.ToBase64String(SHA1.HashData(Encoding.ASCII.GetBytes(key.Trim() + AcceptGuid)));

    public static string ToWebSocketUrl(string url)
    {
        if (url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) return "wss://" + url[8..];
        if (url.StartsWith("http://", StringComparison.OrdinalIgnoreCase)) return "ws://" + url[7..];
        return url;
    }

    public static bool IsForwardedHandshakeHeader(string name) => !HandshakeOwnedHeaders.Contains(name);

    /// <summary>Copies the client's handshake headers and requested sub-protocols onto the upstream connection.</summary>
    public static void ApplyClientHeaders(ClientWebSocketOptions options, IReadOnlyDictionary<string, string> reqHeaders)
    {
        foreach (var (key, val) in reqHeaders)
        {
            if (!IsForwardedHandshakeHeader(key)) continue;
            try { options.SetRequestHeader(key, val); }
            catch (ArgumentException) { /* header the framework refuses to set; not worth failing the connection */ }
        }
        foreach (var protocol in RequestedSubProtocols(reqHeaders))
            options.AddSubProtocol(protocol);
    }

    public static IReadOnlyList<string> RequestedSubProtocols(IReadOnlyDictionary<string, string> reqHeaders) =>
        reqHeaders.TryGetValue("Sec-WebSocket-Protocol", out var protocols)
            ? protocols.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            : [];

    /// <summary>The 101 we send to the client, and the same headers for the session record.</summary>
    public static (byte[] Head, Dictionary<string, string> Headers) BuildHandshakeResponse(string key, string? subProtocol)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Upgrade"] = "websocket",
            ["Connection"] = "Upgrade",
            ["Sec-WebSocket-Accept"] = ComputeAccept(key),
        };
        if (!string.IsNullOrEmpty(subProtocol))
            headers["Sec-WebSocket-Protocol"] = subProtocol;

        var sb = new StringBuilder("HTTP/1.1 101 Switching Protocols\r\n");
        foreach (var (k, v) in headers) sb.Append(k).Append(": ").Append(v).Append("\r\n");
        sb.Append("\r\n");
        return (Encoding.ASCII.GetBytes(sb.ToString()), headers);
    }

    private readonly WebSocket _client;
    private readonly WebSocket _upstream;
    private readonly WebSocketCapture _capture;
    private readonly Stopwatch _clock;
    private readonly SemaphoreSlim _clientSend = new(1, 1);
    private readonly SemaphoreSlim _upstreamSend = new(1, 1);

    public WebSocketRelay(WebSocket client, WebSocket upstream, WebSocketCapture capture, Stopwatch clock)
    {
        _client = client;
        _upstream = upstream;
        _capture = capture;
        _clock = clock;
    }

    /// <summary>
    /// Runs both directions until one side closes or fails, gives the other side a moment to finish the
    /// close handshake, then tears both down. Returns a note describing an abnormal end, or null.
    /// </summary>
    public async Task<string?> RunAsync(Func<Task> onInterim, CancellationToken ct)
    {
        var outbound = RelayDirectionAsync(_client, _upstream, _clientSend, _upstreamSend, WebSocketCapture.DirectionOut, onInterim, ct);
        var inbound = RelayDirectionAsync(_upstream, _client, _upstreamSend, _clientSend, WebSocketCapture.DirectionIn, onInterim, ct);

        var first = await Task.WhenAny(outbound, inbound);
        var other = first == outbound ? inbound : outbound;
        await Task.WhenAny(other, Task.Delay(CloseGrace, CancellationToken.None));

        // After a clean close handshake both sockets are already closed and disposing them ends the
        // connections gracefully. Abort only when a side is stuck or failed, so the peer sees a reset.
        if (!other.IsCompleted || first.IsFaulted || other.IsFaulted)
        {
            _client.Abort();
            _upstream.Abort();
        }
        try { await Task.WhenAll(outbound, inbound); } catch { /* reported below */ }

        if (ct.IsCancellationRequested) return "proxy shutting down";
        if (first.IsFaulted)
        {
            var ex = first.Exception?.GetBaseException();
            var side = first == outbound ? "client" : "upstream";
            return $"{side} connection lost: {ex?.Message}";
        }
        return null;
    }

    private async Task RelayDirectionAsync(
        WebSocket from, WebSocket to, SemaphoreSlim fromSend, SemaphoreSlim toSend,
        string direction, Func<Task> onInterim, CancellationToken ct)
    {
        var buffer = new byte[BufferSize];
        var captured = new MemoryStream();
        long messageBytes = 0;

        while (true)
        {
            ValueWebSocketReceiveResult r;
            try
            {
                r = await from.ReceiveAsync(buffer.AsMemory(), ct);
            }
            catch (WebSocketException) when (to.State is WebSocketState.Closed or WebSocketState.Aborted)
            {
                return; // the other side already finished; nothing left to relay
            }

            if (r.MessageType == WebSocketMessageType.Close)
            {
                var status = from.CloseStatus ?? WebSocketCloseStatus.NormalClosure;
                var reason = from.CloseStatusDescription;
                _capture.AddClose(direction, from.CloseStatus, reason, _clock.ElapsedMilliseconds);
                await CloseOutputAsync(to, toSend, status, reason, ct);   // forward the close
                await CloseOutputAsync(from, fromSend, status, reason, ct); // acknowledge it
                return;
            }

            await toSend.WaitAsync(ct);
            try { await to.SendAsync(buffer.AsMemory(0, r.Count), r.MessageType, r.EndOfMessage, ct); }
            finally { toSend.Release(); }

            messageBytes += r.Count;
            var room = WebSocketCapture.MaxTextBytes - (int)captured.Length;
            if (room > 0) captured.Write(buffer, 0, Math.Min(room, r.Count));

            if (r.EndOfMessage)
            {
                _capture.Add(direction, r.MessageType, captured.GetBuffer().AsSpan(0, (int)captured.Length), messageBytes, _clock.ElapsedMilliseconds);
                captured.SetLength(0);
                messageBytes = 0;
                if (_capture.ShouldPushInterim(_clock.ElapsedMilliseconds))
                    await onInterim();
            }
        }
    }

    private static async Task CloseOutputAsync(WebSocket ws, SemaphoreSlim sendLock, WebSocketCloseStatus status, string? reason, CancellationToken ct)
    {
        await sendLock.WaitAsync(ct);
        try
        {
            if (ws.State is WebSocketState.Open or WebSocketState.CloseReceived)
                await ws.CloseOutputAsync(status, reason, ct);
        }
        catch (WebSocketException) { /* peer already gone */ }
        catch (ObjectDisposedException) { }
        catch (InvalidOperationException) { }
        finally { sendLock.Release(); }
    }
}
