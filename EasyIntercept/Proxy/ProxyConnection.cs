using System.Diagnostics;
using System.Net.Sockets;
using System.Text;
using EasyIntercept.Hubs;
using EasyIntercept.Models;
using EasyIntercept.Pins;
using EasyIntercept.Storage;
using Microsoft.AspNetCore.SignalR;

namespace EasyIntercept.Proxy;

public class ProxyConnection
{
    private static readonly HashSet<string> HopByHopHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Connection", "Keep-Alive", "Proxy-Authenticate", "Proxy-Authorization",
        "TE", "Trailers", "Transfer-Encoding", "Upgrade", "Proxy-Connection",
    };

    // These belong on HttpContent.Headers, not HttpRequestMessage.Headers
    private static readonly HashSet<string> ContentOnlyHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Content-Type", "Content-Encoding", "Content-Language", "Content-Location",
        "Content-MD5", "Content-Range", "Expires", "Last-Modified",
    };

    private readonly TcpClient _client;
    private readonly SessionStore _sessions;
    private readonly PinStore _pins;
    private readonly IHubContext<ProxyHub> _hub;
    private readonly IHttpClientFactory _httpClientFactory;

    public ProxyConnection(
        TcpClient client,
        SessionStore sessions,
        PinStore pins,
        IHubContext<ProxyHub> hub,
        IHttpClientFactory httpClientFactory)
    {
        _client = client;
        _sessions = sessions;
        _pins = pins;
        _hub = hub;
        _httpClientFactory = httpClientFactory;
    }

    public async Task HandleAsync()
    {
        using var tcpClient = _client;
        var stream = tcpClient.GetStream();

        // StreamReader with Latin1 (byte-transparent) and leaveOpen:true
        // Reads headers in 8 KB chunks instead of 1 byte at a time
        using var reader = new StreamReader(stream, Encoding.Latin1,
            detectEncodingFromByteOrderMarks: false, bufferSize: 8192, leaveOpen: true);

        // --- Parse request line ---
        var firstLine = await reader.ReadLineAsync() ?? "";
        if (string.IsNullOrWhiteSpace(firstLine)) return;

        var parts = firstLine.Split(' ');
        if (parts.Length < 2) return;

        var method = parts[0].ToUpperInvariant();
        var url    = parts[1];

        // HTTPS CONNECT tunneling — not supported in Phase 1
        if (method == "CONNECT")
        {
            const string msg = "HTTP/1.1 405 HTTPS Not Supported\r\n" +
                               "Content-Length: 0\r\nConnection: close\r\n\r\n";
            await stream.WriteAsync(Encoding.ASCII.GetBytes(msg));
            return;
        }

        // --- Parse request headers ---
        var requestHeaders = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        while (true)
        {
            var line = await reader.ReadLineAsync() ?? "";
            if (string.IsNullOrEmpty(line)) break;
            var colon = line.IndexOf(':');
            if (colon > 0)
                requestHeaders[line[..colon].Trim()] = line[(colon + 1)..].Trim();
        }

        // --- Read request body (if any) ---
        // After headers, the StreamReader may have buffered body bytes — drain via reader
        var requestBodyBytes = Array.Empty<byte>();
        if (requestHeaders.TryGetValue("Content-Length", out var clStr)
            && int.TryParse(clStr, out var bodyLen) && bodyLen > 0)
        {
            var chars = new char[bodyLen];
            await reader.ReadBlockAsync(chars, 0, bodyLen);
            requestBodyBytes = Encoding.Latin1.GetBytes(chars);
        }

        var requestBodyText = requestBodyBytes.Length > 0
            ? Encoding.UTF8.GetString(requestBodyBytes)
            : "";

        // --- Check PinStore ---
        if (_pins.TryGet(url, out var pinned))
        {
            await WritePinnedResponseAsync(stream, pinned!);
            return;
        }

        // --- Build upstream request ---
        var httpClient      = _httpClientFactory.CreateClient("proxy");
        var upstreamRequest = new HttpRequestMessage(new HttpMethod(method), url);

        foreach (var (key, value) in requestHeaders)
        {
            if (HopByHopHeaders.Contains(key)) continue;
            if (key.Equals("Host",             StringComparison.OrdinalIgnoreCase)) continue;
            if (key.Equals("Accept-Encoding",  StringComparison.OrdinalIgnoreCase)) continue;
            if (ContentOnlyHeaders.Contains(key)) continue;
            upstreamRequest.Headers.TryAddWithoutValidation(key, value);
        }

        if (requestBodyBytes.Length > 0)
        {
            upstreamRequest.Content = new ByteArrayContent(requestBodyBytes);
            if (requestHeaders.TryGetValue("Content-Type", out var ct))
                upstreamRequest.Content.Headers.TryAddWithoutValidation("Content-Type", ct);
        }

        // --- Send upstream (ResponseHeadersRead = don't buffer body before returning) ---
        var sw = Stopwatch.StartNew();
        HttpResponseMessage upstreamResponse;
        try
        {
            upstreamResponse = await httpClient.SendAsync(
                upstreamRequest, HttpCompletionOption.ResponseHeadersRead);
        }
        catch (Exception ex)
        {
            sw.Stop();
            await WriteErrorAsync(stream, 502, "Bad Gateway", ex.Message);
            return;
        }

        // --- Stream response body to client while collecting for logging ---
        using (upstreamResponse)
        {
            var responseHeaders = CollectHeaders(upstreamResponse);

            // Write status + headers immediately — client starts receiving data right away
            await WriteResponseHeadersAsync(
                stream,
                (int)upstreamResponse.StatusCode,
                upstreamResponse.ReasonPhrase ?? "OK",
                responseHeaders);

            // Stream body with chunked encoding: client receives data as it arrives
            var bodyBuffer = new MemoryStream();
            await using var upstreamBody = await upstreamResponse.Content.ReadAsStreamAsync();
            var pipe = new byte[16 * 1024];
            int read;
            while ((read = await upstreamBody.ReadAsync(pipe)) > 0)
            {
                // Chunked format: hex-length\r\n + data + \r\n
                await stream.WriteAsync(Encoding.ASCII.GetBytes($"{read:x}\r\n"));
                await stream.WriteAsync(pipe.AsMemory(0, read));
                await stream.WriteAsync(Encoding.ASCII.GetBytes("\r\n"));
                bodyBuffer.Write(pipe, 0, read);
            }
            // Terminal chunk
            await stream.WriteAsync(Encoding.ASCII.GetBytes("0\r\n\r\n"));
            sw.Stop();

            var responseBodyBytes = bodyBuffer.ToArray();
            var responseBodyText  = DecodeBodyText(responseBodyBytes, responseHeaders);

            var session = new ProxySession
            {
                Method          = method,
                Url             = url,
                RequestHeaders  = requestHeaders,
                RequestBody     = requestBodyText,
                ResponseStatus  = (int)upstreamResponse.StatusCode,
                ResponseHeaders = responseHeaders,
                ResponseBody    = responseBodyText,
                DurationMs      = sw.ElapsedMilliseconds,
            };

            _sessions.Add(session);
            await _hub.Clients.All.SendAsync("NewSession", session);
        }
    }

    // ---------- helpers ----------

    private static Dictionary<string, string> CollectHeaders(HttpResponseMessage response)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var h in response.Headers)
            result[h.Key] = string.Join(", ", h.Value);
        foreach (var h in response.Content.Headers)
            result[h.Key] = string.Join(", ", h.Value);
        return result;
    }

    private static string DecodeBodyText(byte[] bytes, Dictionary<string, string> headers)
    {
        if (!headers.TryGetValue("Content-Type", out var ct))
            return $"[{bytes.Length} bytes]";

        var isText = ct.Contains("text/",                   StringComparison.OrdinalIgnoreCase)
                  || ct.Contains("json",                    StringComparison.OrdinalIgnoreCase)
                  || ct.Contains("xml",                     StringComparison.OrdinalIgnoreCase)
                  || ct.Contains("javascript",              StringComparison.OrdinalIgnoreCase)
                  || ct.Contains("x-www-form-urlencoded",   StringComparison.OrdinalIgnoreCase);

        return isText ? Encoding.UTF8.GetString(bytes) : $"[binary: {bytes.Length} bytes]";
    }

    // Writes status line + headers only (body is streamed separately)
    private static async Task WriteResponseHeadersAsync(
        Stream stream, int status, string reason,
        Dictionary<string, string> headers)
    {
        var sb = new StringBuilder();
        sb.Append($"HTTP/1.1 {status} {reason}\r\n");
        foreach (var (key, value) in headers)
        {
            if (key.Equals("Transfer-Encoding", StringComparison.OrdinalIgnoreCase)) continue;
            if (key.Equals("Content-Length",    StringComparison.OrdinalIgnoreCase)) continue;
            sb.Append($"{key}: {value}\r\n");
        }
        sb.Append("Transfer-Encoding: chunked\r\n");
        sb.Append("Connection: close\r\n");
        sb.Append("\r\n");

        await stream.WriteAsync(Encoding.ASCII.GetBytes(sb.ToString()));
    }

    private static async Task WritePinnedResponseAsync(Stream stream, PinnedResponse pinned)
    {
        var bodyBytes = Encoding.UTF8.GetBytes(pinned.Body);
        var sb = new StringBuilder();
        sb.Append($"HTTP/1.1 {pinned.StatusCode} OK\r\n");
        foreach (var (key, value) in pinned.Headers)
        {
            if (key.Equals("Transfer-Encoding", StringComparison.OrdinalIgnoreCase)) continue;
            if (key.Equals("Content-Length",    StringComparison.OrdinalIgnoreCase)) continue;
            sb.Append($"{key}: {value}\r\n");
        }
        sb.Append($"Content-Length: {bodyBytes.Length}\r\n");
        sb.Append("Connection: close\r\n");
        sb.Append("X-EasyIntercept-Pinned: true\r\n");
        sb.Append("\r\n");

        await stream.WriteAsync(Encoding.ASCII.GetBytes(sb.ToString()));
        await stream.WriteAsync(bodyBytes);
    }

    // Reads one line — kept only for potential future use (not called in hot path)
    private static async Task<string> ReadLineAsync(Stream stream)
    {
        var sb     = new StringBuilder();
        var buffer = new byte[1];
        while (true)
        {
            var read = await stream.ReadAsync(buffer);
            if (read == 0) break;
            if (buffer[0] == '\n') break;
            if (buffer[0] != '\r') sb.Append((char)buffer[0]);
        }
        return sb.ToString();
    }

    private static async Task WriteErrorAsync(Stream stream, int status, string reason, string message)
    {
        var bodyBytes = Encoding.UTF8.GetBytes($"EasyIntercept: {message}");
        var header    = $"HTTP/1.1 {status} {reason}\r\n" +
                        $"Content-Type: text/plain\r\n" +
                        $"Content-Length: {bodyBytes.Length}\r\n" +
                        "Connection: close\r\n\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(header));
        await stream.WriteAsync(bodyBytes);
    }

}
