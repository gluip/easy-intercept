using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using EasyIntercept.AutoResponder;
using EasyIntercept.Certificates;
using EasyIntercept.Hubs;
using EasyIntercept.Models;
using EasyIntercept.Storage;
using Microsoft.AspNetCore.SignalR;

namespace EasyIntercept.Proxy;

public class ProxyConnection
{
    private const int MaxHeadBytes = 8192;

    private readonly TcpClient _client;
    private readonly SessionStore _sessions;
    private readonly IHubContext<ProxyHub> _hub;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly CertificateService _certs;
    private readonly AutoResponderStore _autoResponder;
    private readonly CancellationToken _stopping;

    public ProxyConnection(
        TcpClient client,
        SessionStore sessions,
        IHubContext<ProxyHub> hub,
        IHttpClientFactory httpClientFactory,
        CertificateService certs,
        AutoResponderStore autoResponder,
        CancellationToken stoppingToken = default)
    {
        _client = client;
        _sessions = sessions;
        _hub = hub;
        _httpClientFactory = httpClientFactory;
        _certs = certs;
        _autoResponder = autoResponder;
        _stopping = stoppingToken;
    }

    public async Task HandleAsync()
    {
        using var tcp = _client;
        tcp.NoDelay = true;
        var stream = tcp.GetStream();

        var buf = new byte[MaxHeadBytes];
        var (headerEnd, filled) = await ReadHeadAsync(stream, buf);
        if (headerEnd < 0) return;

        var lines = Encoding.ASCII.GetString(buf, 0, headerEnd).Split("\r\n");
        var reqLine = lines[0].Split(' ');
        if (reqLine.Length < 2) return;

        var method = reqLine[0];
        var url = reqLine[1];

        if (method.Equals("CONNECT", StringComparison.OrdinalIgnoreCase))
        {
            await HandleConnect(tcp, stream, url);
            return;
        }

        // Plain HTTP — forward directly
        await ForwardRequest(stream, method, url, lines, buf, headerEnd, filled);
    }

    private async Task HandleConnect(TcpClient tcp, NetworkStream rawStream, string hostPort)
    {
        var (host, port) = SplitHostPort(hostPort, 443);

        // Tell client the tunnel is established
        await WriteRaw(rawStream, "HTTP/1.1 200 Connection Established\r\n\r\n");

        // A TLS ClientHello starts with 0x16. Anything else is a plaintext tunnel, which is how
        // ws:// (and curl --proxytunnel) go through an HTTP proxy.
        var peek = new byte[1];
        var peeked = await tcp.Client.ReceiveAsync(peek, SocketFlags.Peek, _stopping);
        if (peeked == 0) return;
        var isTls = peek[0] == 0x16;

        Stream stream = rawStream;
        SslStream? clientSsl = null;
        var scheme = "http";
        var defaultPort = 80;
        if (isTls)
        {
            // Wrap client side with SslStream using our generated cert
            var cert = _certs.GetCertificateForHost(host);
            clientSsl = new SslStream(rawStream, leaveInnerStreamOpen: true);
            await clientSsl.AuthenticateAsServerAsync(cert);
            stream = clientSsl;
            scheme = "https";
            defaultPort = 443;
        }

        // Now read the actual HTTP request from the tunnel
        var buf = new byte[MaxHeadBytes];
        var (headerEnd, filled) = await ReadHeadAsync(stream, buf);
        if (headerEnd < 0) return;

        var lines = Encoding.ASCII.GetString(buf, 0, headerEnd).Split("\r\n");
        var reqLine = lines[0].Split(' ');
        if (reqLine.Length < 2) return;

        var method = reqLine[0];
        var path = reqLine[1]; // relative path like "/get", occasionally absolute-form
        var authority = port == defaultPort ? host : $"{host}:{port}";
        var fullUrl = path.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || path.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
            ? path
            : $"{scheme}://{authority}{path}";

        await ForwardRequest(stream, method, fullUrl, lines, buf, headerEnd, filled);

        if (clientSsl is not null)
        {
            // Send close_notify, then wait briefly for the client's FIN before closing the socket. Closing
            // with the client's own close_notify still unread would turn our FIN into a reset.
            try
            {
                await clientSsl.ShutdownAsync();
                await DrainAsync(rawStream, TimeSpan.FromSeconds(1));
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException) { /* client already gone */ }
            clientSsl.Dispose();
        }
    }

    private static async Task DrainAsync(Stream stream, TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        var buf = new byte[1024];
        try
        {
            while (await stream.ReadAsync(buf, cts.Token) > 0) { }
        }
        catch (OperationCanceledException) { }
    }

    private async Task ForwardRequest(Stream stream, string method, string url, string[] lines, byte[] buf, int headerEnd, int filled)
    {
        var reqHeaders = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 1; i < lines.Length; i++)
        {
            var colon = lines[i].IndexOf(':');
            if (colon > 0)
                reqHeaders[lines[i][..colon].Trim()] = lines[i][(colon + 1)..].Trim();
        }

        var isWebSocket = reqHeaders.TryGetValue("Upgrade", out var upgrade)
            && upgrade.Contains("websocket", StringComparison.OrdinalIgnoreCase);

        // Read request body
        var bodyStart = headerEnd + 4;
        var leftover = filled - bodyStart;
        byte[] reqBody = [];

        if (!isWebSocket && reqHeaders.TryGetValue("Content-Length", out var cl) && int.TryParse(cl, out var bodyLen) && bodyLen > 0)
        {
            reqBody = new byte[bodyLen];
            var copied = Math.Min(leftover, bodyLen);
            Buffer.BlockCopy(buf, bodyStart, reqBody, 0, copied);
            var remaining = bodyLen - copied;
            var offset = copied;
            while (remaining > 0)
            {
                var n = await stream.ReadAsync(reqBody.AsMemory(offset, remaining));
                if (n == 0) break;
                offset += n;
                remaining -= n;
            }
        }

        // Decompress gzip if present
        var (actualBody, _) = DecompressRequestBodyIfNeeded(reqBody, reqHeaders);
        var isReqText = reqHeaders.TryGetValue("Content-Type", out var reqCt) && IsTextContentType(reqCt);

        var startTime = DateTime.UtcNow;

        // Send a pending session immediately so the request shows up before the response arrives
        var requestBodyForSession = (actualBody.Length > 0 && isReqText)
            ? Encoding.UTF8.GetString(actualBody)
            : (actualBody.Length > 0 ? $"[{actualBody.Length} bytes binary]" : "");

        var pendingSession = new ProxySession
        {
            Timestamp = startTime,
            Method = method,
            Url = url,
            RequestHeaders = reqHeaders,
            RequestBody = requestBodyForSession,
            ResponseStatus = 0,
            ResponseHeaders = new(),
            ResponseBody = "",
            DurationMs = 0,
            ResponseComplete = false,
        };
        _sessions.Add(pendingSession);
        await PublishAsync("NewSession", pendingSession);

        if (isWebSocket)
        {
            await HandleWebSocket(stream, url, reqHeaders, pendingSession);
            return;
        }

        // Auto-responder check — runs before any upstream request
        var requestBodyText = actualBody.Length > 0 ? Encoding.UTF8.GetString(actualBody) : "";
        var match = _autoResponder.FindMatch(method, url, requestBodyText);
        if (match is not null)
        {
            var bodyBytes = Encoding.UTF8.GetBytes(match.ResponseBody);
            var reason = ReasonPhrase(match.ResponseStatus);

            var sb = new StringBuilder();
            sb.Append($"HTTP/1.1 {match.ResponseStatus} {reason}\r\n");
            foreach (var (key, val) in match.ResponseHeaders)
            {
                if (ResponseRelay.HopByHopHeaders.Contains(key)) continue;
                if (key.Equals("Content-Length", StringComparison.OrdinalIgnoreCase)) continue;
                if (key.Equals("Content-Encoding", StringComparison.OrdinalIgnoreCase)) continue;
                sb.Append($"{key}: {val}\r\n");
            }
            sb.Append($"Content-Length: {bodyBytes.Length}\r\n");
            sb.Append("X-EasyIntercept-AutoResponder: true\r\n");
            sb.Append("Connection: close\r\n\r\n");

            if (match.LatencyMs > 0)
                await Task.Delay(match.LatencyMs);

            await stream.WriteAsync(Encoding.ASCII.GetBytes(sb.ToString()));
            await stream.WriteAsync(bodyBytes);
            await stream.FlushAsync();

            var fakeRespHeaders = new Dictionary<string, string>(match.ResponseHeaders, StringComparer.OrdinalIgnoreCase)
            {
                ["X-EasyIntercept-AutoResponder"] = "true",
                ["Content-Length"] = bodyBytes.Length.ToString()
            };

            var session = pendingSession with
            {
                ResponseStatus = match.ResponseStatus,
                ResponseHeaders = fakeRespHeaders,
                ResponseBody = match.ResponseBody,
                DurationMs = match.LatencyMs,
                ResponseComplete = true,
            };

            _sessions.Update(session);
            await PublishAsync("UpdateSession", session);
            return;
        }

        // Build upstream request
        var httpClient = _httpClientFactory.CreateClient(ProxyHttpClient.Name);
        var req = new HttpRequestMessage(new HttpMethod(method), url);

        foreach (var (key, val) in reqHeaders)
        {
            if (ResponseRelay.HopByHopHeaders.Contains(key)) continue;
            if (key.Equals("Host", StringComparison.OrdinalIgnoreCase)) continue;
            if (key.Equals("Accept-Encoding", StringComparison.OrdinalIgnoreCase)) continue;
            if (key.Equals("Content-Type", StringComparison.OrdinalIgnoreCase)) continue;
            if (key.Equals("Content-Length", StringComparison.OrdinalIgnoreCase)) continue;
            req.Headers.TryAddWithoutValidation(key, val);
        }

        if (actualBody.Length > 0)
        {
            req.Content = new ByteArrayContent(actualBody);
            if (reqHeaders.TryGetValue("Content-Type", out var ct))
                req.Content.Headers.TryAddWithoutValidation("Content-Type", ct);
        }

        // Send upstream: forward the head as soon as it arrives, then relay the body chunk by chunk.
        var sw = Stopwatch.StartNew();
        HttpResponseMessage upstream;
        try
        {
            upstream = await httpClient.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, _stopping);
        }
        catch (Exception ex)
        {
            await WriteErrorAsync(stream, pendingSession, 502, ex.Message, sw.ElapsedMilliseconds);
            return;
        }

        using (upstream)
        {
            var ttfb = sw.ElapsedMilliseconds;
            var status = (int)upstream.StatusCode;

            // Wire headers keep multi-valued Set-Cookie as separate lines; the session stores one joined value per name.
            var wireHeaders = new List<KeyValuePair<string, string>>();
            var sessionHeaders = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var h in upstream.Headers.Concat(upstream.Content.Headers))
            {
                if (h.Key.Equals("Set-Cookie", StringComparison.OrdinalIgnoreCase))
                    foreach (var v in h.Value) wireHeaders.Add(new(h.Key, v));
                else
                    wireHeaders.Add(new(h.Key, string.Join(", ", h.Value)));
                sessionHeaders[h.Key] = string.Join(", ", h.Value);
            }
            sessionHeaders.Remove("Transfer-Encoding");
            sessionHeaders.Remove("Content-Encoding");

            var hasBody = !ResponseRelay.HasNoBody(method, status);
            var isHead = method.Equals("HEAD", StringComparison.OrdinalIgnoreCase);
            // After AutomaticDecompression the handler has removed Content-Length for compressed bodies,
            // so a value here is the real length. HEAD passes it through as information.
            var contentLength = hasBody || isHead ? upstream.Content.Headers.ContentLength : null;
            var chunked = hasBody && contentLength is null;

            sessionHeaders.TryGetValue("Content-Type", out var rct);
            var capture = new SessionCapture(rct);

            var head = ResponseRelay.BuildHead(status, upstream.ReasonPhrase, wireHeaders, contentLength, hasBody);
            var result = new PumpResult(PumpOutcome.Completed, 0);
            try
            {
                await stream.WriteAsync(head, _stopping);
                await stream.FlushAsync(_stopping);
            }
            catch (OperationCanceledException) { result = new(PumpOutcome.Cancelled, 0); }
            catch (Exception ex) { result = new(PumpOutcome.ClientGone, 0, ex); }

            var streaming = pendingSession with
            {
                ResponseStatus = status,
                ResponseHeaders = new Dictionary<string, string>(sessionHeaders, StringComparer.OrdinalIgnoreCase),
                ResponseBody = "",
                ResponseComplete = false,
                TimeToFirstByteMs = ttfb,
                DurationMs = ttfb,
            };
            _sessions.UpdateInMemory(streaming);
            await PublishAsync("UpdateSession", streaming);

            if (hasBody && result.Outcome == PumpOutcome.Completed)
            {
                Stream? body = null;
                try { body = await upstream.Content.ReadAsStreamAsync(_stopping); }
                catch (OperationCanceledException) { result = new(PumpOutcome.Cancelled, 0); }
                catch (Exception ex) { result = new(PumpOutcome.UpstreamFailed, 0, ex); }

                if (body is not null)
                {
                    using (body)
                    {
                        result = await ResponseRelay.PumpAsync(body, stream, chunked, async chunk =>
                        {
                            capture.Append(chunk.Span);
                            if (capture.ShouldPushInterim(sw.ElapsedMilliseconds))
                            {
                                var interim = streaming with
                                {
                                    ResponseBody = capture.BodyText(interim: true),
                                    DurationMs = sw.ElapsedMilliseconds,
                                };
                                _sessions.UpdateInMemory(interim);
                                await PublishAsync("UpdateSession", interim);
                            }
                        }, _stopping);
                    }
                }
            }
            sw.Stop();

            var note = result.Outcome switch
            {
                PumpOutcome.ClientGone => "client disconnected",
                PumpOutcome.UpstreamFailed => $"upstream error: {result.Error?.Message}",
                PumpOutcome.Cancelled => "proxy shutting down",
                _ => null,
            };

            var bodyText = capture.BodyText();
            if (note is not null)
            {
                bodyText = AppendNote(bodyText, note, capture.TotalBytes);
                sessionHeaders["X-EasyIntercept-Note"] = note;
            }
            sessionHeaders["Content-Length"] = capture.TotalBytes.ToString();

            var final = streaming with
            {
                ResponseHeaders = sessionHeaders,
                ResponseBody = bodyText,
                DurationMs = sw.ElapsedMilliseconds,
                ResponseComplete = true,
            };
            _sessions.Update(final);
            await PublishAsync("UpdateSession", final);
        }
    }

    private async Task HandleWebSocket(Stream stream, string url, Dictionary<string, string> reqHeaders, ProxySession pendingSession)
    {
        var sw = Stopwatch.StartNew();
        var wsUrl = WebSocketRelay.ToWebSocketUrl(url);

        if (!reqHeaders.TryGetValue("Sec-WebSocket-Key", out var key) || string.IsNullOrWhiteSpace(key))
        {
            await WriteErrorAsync(stream, pendingSession with { Url = wsUrl }, 400, "Missing Sec-WebSocket-Key header", 0);
            return;
        }

        // Upstream handshake first, so the client gets the real outcome (101, or the server's refusal).
        var upstream = new ClientWebSocket();
        upstream.Options.CollectHttpResponseDetails = true;
        upstream.Options.KeepAliveInterval = TimeSpan.Zero; // the real client drives pings
        WebSocketRelay.ApplyClientHeaders(upstream.Options, reqHeaders);

        var httpClient = _httpClientFactory.CreateClient(ProxyHttpClient.Name);
        try
        {
            await upstream.ConnectAsync(new Uri(wsUrl), httpClient, _stopping);
        }
        catch (Exception ex)
        {
            var upstreamStatus = (int)upstream.HttpStatusCode;
            var status = upstreamStatus is 0 or 101 ? 502 : upstreamStatus;
            await WriteErrorAsync(stream, pendingSession with { Url = wsUrl }, status, ex.Message, sw.ElapsedMilliseconds);
            upstream.Dispose();
            return;
        }

        var ttfb = sw.ElapsedMilliseconds;
        var (head, respHeaders) = WebSocketRelay.BuildHandshakeResponse(key, upstream.SubProtocol);
        var open = pendingSession with
        {
            Url = wsUrl,
            ResponseStatus = 101,
            ResponseHeaders = respHeaders,
            ResponseBody = "",
            ResponseComplete = false,
            TimeToFirstByteMs = ttfb,
            DurationMs = ttfb,
            WebSocketMessages = new(),
        };

        try
        {
            await stream.WriteAsync(head, _stopping);
            await stream.FlushAsync(_stopping);
        }
        catch (Exception)
        {
            upstream.Abort();
            upstream.Dispose();
            await FinishWebSocketAsync(open, new(), "client disconnected during handshake", sw.ElapsedMilliseconds);
            return;
        }

        _sessions.UpdateInMemory(open);
        await PublishAsync("UpdateSession", open);

        using var client = WebSocket.CreateFromStream(new LeaveOpenStream(stream), new WebSocketCreationOptions
        {
            IsServer = true,
            SubProtocol = upstream.SubProtocol,
            KeepAliveInterval = TimeSpan.Zero,
        });

        var capture = new WebSocketCapture();
        var relay = new WebSocketRelay(client, upstream, capture, sw);
        string? note;
        using (upstream)
        {
            note = await relay.RunAsync(async () =>
            {
                var interim = open with { WebSocketMessages = capture.Snapshot(), DurationMs = sw.ElapsedMilliseconds };
                _sessions.UpdateInMemory(interim);
                await PublishAsync("UpdateSession", interim);
            }, _stopping);
        }
        sw.Stop();

        await FinishWebSocketAsync(open, capture.Snapshot(), note, sw.ElapsedMilliseconds);
    }

    private async Task FinishWebSocketAsync(ProxySession open, List<WebSocketMessage> messages, string? note, long durationMs)
    {
        var headers = new Dictionary<string, string>(open.ResponseHeaders, StringComparer.OrdinalIgnoreCase);
        if (note is not null) headers["X-EasyIntercept-Note"] = note;
        var final = open with
        {
            ResponseHeaders = headers,
            WebSocketMessages = messages,
            DurationMs = durationMs,
            ResponseComplete = true,
        };
        _sessions.Update(final);
        await PublishAsync("UpdateSession", final);
    }

    /// <summary>Answers the client with a small error response and records it as the session's outcome.</summary>
    private async Task WriteErrorAsync(Stream stream, ProxySession pendingSession, int status, string message, long durationMs)
    {
        var body = Encoding.UTF8.GetBytes(message);
        try
        {
            await WriteRaw(stream, $"HTTP/1.1 {status} {ReasonPhrase(status)}\r\nContent-Type: text/plain; charset=utf-8\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n");
            await stream.WriteAsync(body);
            await stream.FlushAsync();
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException) { /* client already gone; still record the session */ }

        var errSession = pendingSession with
        {
            ResponseStatus = status,
            ResponseHeaders = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["Content-Type"] = "text/plain; charset=utf-8",
                ["Content-Length"] = body.Length.ToString(),
            },
            ResponseBody = message,
            DurationMs = durationMs,
            ResponseComplete = true,
        };
        _sessions.Update(errSession);
        await PublishAsync("UpdateSession", errSession);
    }

    /// <summary>SignalR is best-effort: a UI hiccup must never break the relay.</summary>
    private async Task PublishAsync(string method, ProxySession session)
    {
        try { await _hub.Clients.All.SendAsync(method, session); }
        catch { }
    }

    private static string AppendNote(string bodyText, string note, long totalBytes) =>
        bodyText.StartsWith('[') && bodyText.EndsWith(']')
            ? $"{bodyText[..^1]}, {note}]"
            : $"{bodyText}\n\n[EasyIntercept: {note} after {totalBytes} bytes]";

    private static string ReasonPhrase(int status) => status switch
    {
        101 => "Switching Protocols",
        200 => "OK", 201 => "Created", 204 => "No Content",
        400 => "Bad Request", 401 => "Unauthorized", 403 => "Forbidden",
        404 => "Not Found", 500 => "Internal Server Error", 502 => "Bad Gateway",
        _ => "OK"
    };

    private static (string host, int port) SplitHostPort(string hostPort, int defaultPort)
    {
        var colon = hostPort.LastIndexOf(':');
        if (colon > 0 && int.TryParse(hostPort[(colon + 1)..], out var port))
            return (hostPort[..colon], port);
        return (hostPort, defaultPort);
    }

    /// <summary>Reads until the blank line that ends the request head. Returns (-1, filled) when none arrives within the buffer.</summary>
    private static async Task<(int headerEnd, int filled)> ReadHeadAsync(Stream stream, byte[] buf)
    {
        var filled = 0;
        while (filled < buf.Length)
        {
            var n = await stream.ReadAsync(buf.AsMemory(filled, buf.Length - filled));
            if (n == 0) return (-1, filled);
            var scanFrom = Math.Max(0, filled - 3);
            filled += n;
            for (var i = scanFrom; i <= filled - 4; i++)
            {
                if (buf[i] == '\r' && buf[i + 1] == '\n' && buf[i + 2] == '\r' && buf[i + 3] == '\n')
                    return (i, filled);
            }
        }
        return (-1, filled);
    }

    private static async Task WriteRaw(Stream stream, string text) =>
        await stream.WriteAsync(Encoding.ASCII.GetBytes(text));

    private static bool IsTextContentType(string? contentType) => SessionCapture.IsTextLike(contentType);

    private static (byte[] body, string? originalEncoding) DecompressRequestBodyIfNeeded(
        byte[] reqBody,
        Dictionary<string, string> reqHeaders)
    {
        byte[] actualBody = reqBody;
        string? originalEncoding = null;

        if (reqHeaders.TryGetValue("Content-Encoding", out var contentEncoding))
        {
            originalEncoding = contentEncoding;
            if (contentEncoding.ToLowerInvariant().Contains("gzip"))
            {
                try
                {
                    using var input = new MemoryStream(reqBody);
                    using var gzip = new GZipStream(input, CompressionMode.Decompress);
                    using var output = new MemoryStream();
                    gzip.CopyTo(output);
                    actualBody = output.ToArray();
                    reqHeaders.Remove("Content-Encoding");
                    reqHeaders["Content-Length"] = actualBody.Length.ToString();
                }
                catch
                {
                    // Keep original if decompression fails
                }
            }
        }

        return (actualBody, originalEncoding);
    }
}
