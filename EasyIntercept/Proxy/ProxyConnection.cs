using System.Diagnostics;
using System.IO.Compression;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using EasyIntercept.AutoResponder;
using EasyIntercept.Certificates;
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

    private readonly TcpClient _client;
    private readonly SessionStore _sessions;
    private readonly PinStore _pins;
    private readonly AutoResponderStore _autoResponder;
    private readonly RecordingStore _recordings;
    private readonly AnalysisStore _analysis;
    private readonly IHubContext<ProxyHub> _hub;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly CertificateService _certs;

    public ProxyConnection(
        TcpClient client,
        SessionStore sessions,
        PinStore pins,
        AutoResponderStore autoResponder,
        RecordingStore recordings,
        AnalysisStore analysis,
        IHubContext<ProxyHub> hub,
        IHttpClientFactory httpClientFactory,
        CertificateService certs)
    {
        _client = client;
        _sessions = sessions;
        _pins = pins;
        _autoResponder = autoResponder;
        _recordings = recordings;
        _analysis = analysis;
        _hub = hub;
        _httpClientFactory = httpClientFactory;
        _certs = certs;
    }

    public async Task HandleAsync()
    {
        using var tcp = _client;
        tcp.NoDelay = true;
        var stream = tcp.GetStream();

        // Read raw bytes until \r\n\r\n
        var buf = new byte[8192];
        var filled = 0;
        var headerEnd = -1;

        while (filled < buf.Length)
        {
            var n = await stream.ReadAsync(buf.AsMemory(filled, buf.Length - filled));
            if (n == 0) return;
            filled += n;

            for (var i = Math.Max(0, filled - n - 3); i <= filled - 4; i++)
            {
                if (buf[i] == '\r' && buf[i + 1] == '\n' && buf[i + 2] == '\r' && buf[i + 3] == '\n')
                {
                    headerEnd = i;
                    break;
                }
            }
            if (headerEnd >= 0) break;
        }

        if (headerEnd < 0) return;

        var headerText = Encoding.ASCII.GetString(buf, 0, headerEnd);
        var lines = headerText.Split("\r\n");
        if (lines.Length == 0) return;

        var reqLine = lines[0].Split(' ');
        if (reqLine.Length < 2) return;

        var method = reqLine[0];
        var url = reqLine[1];

        if (method.Equals("CONNECT", StringComparison.OrdinalIgnoreCase))
        {
            await HandleConnect(stream, url);
            return;
        }

        // Plain HTTP — forward directly
        await ForwardRequest(stream, method, url, lines, buf, headerEnd, filled);
    }

    private async Task HandleConnect(NetworkStream rawStream, string hostPort)
    {
        // url is "host:port"
        var host = hostPort.Contains(':') ? hostPort[..hostPort.IndexOf(':')] : hostPort;

        // Tell client the tunnel is established
        await WriteRaw(rawStream, "HTTP/1.1 200 Connection Established\r\n\r\n");

        // Wrap client side with SslStream using our generated cert
        var cert = _certs.GetCertificateForHost(host);
        var clientSsl = new SslStream(rawStream, leaveInnerStreamOpen: true);
        await clientSsl.AuthenticateAsServerAsync(cert);

        // Now read the actual HTTP request from the SSL stream
        var buf = new byte[8192];
        var filled = 0;
        var headerEnd = -1;

        while (filled < buf.Length)
        {
            var n = await clientSsl.ReadAsync(buf.AsMemory(filled, buf.Length - filled));
            if (n == 0) return;
            filled += n;

            for (var i = Math.Max(0, filled - n - 3); i <= filled - 4; i++)
            {
                if (buf[i] == '\r' && buf[i + 1] == '\n' && buf[i + 2] == '\r' && buf[i + 3] == '\n')
                {
                    headerEnd = i;
                    break;
                }
            }
            if (headerEnd >= 0) break;
        }

        if (headerEnd < 0) return;

        var headerText = Encoding.ASCII.GetString(buf, 0, headerEnd);
        var lines = headerText.Split("\r\n");
        if (lines.Length == 0) return;

        var reqLine = lines[0].Split(' ');
        if (reqLine.Length < 2) return;

        var method = reqLine[0];
        var path = reqLine[1]; // relative path like "/get"
        var fullUrl = $"https://{host}{path}";

        await ForwardRequest(clientSsl, method, fullUrl, lines, buf, headerEnd, filled);

        await clientSsl.ShutdownAsync();
        clientSsl.Dispose();
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

        // Read request body
        var bodyStart = headerEnd + 4;
        var leftover = filled - bodyStart;
        byte[] reqBody = [];

        if (reqHeaders.TryGetValue("Content-Length", out var cl) && int.TryParse(cl, out var bodyLen) && bodyLen > 0)
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

        // Check pin store
        if (_pins.TryGet(url, out var pinned))
        {
            var pb = Encoding.UTF8.GetBytes(pinned!.Body);
            await WriteRaw(stream, $"HTTP/1.1 {pinned.StatusCode} OK\r\nContent-Length: {pb.Length}\r\nX-EasyIntercept-Pinned: true\r\nConnection: close\r\n\r\n");
            await stream.WriteAsync(pb);
            return;
        }

        // Decompress gzip if present
        var (actualBody, originalEncoding) = DecompressRequestBodyIfNeeded(reqBody, reqHeaders);

        // Check auto-responder rules (manual first, then active recording)
        var hasContentEncoding = originalEncoding != null;
        var isReqText = reqHeaders.TryGetValue("Content-Type", out var reqCt) && IsTextContentType(reqCt);
        var reqBodyStr = (actualBody.Length > 0 && isReqText) ? Encoding.UTF8.GetString(actualBody) : "";
        var activeRecording = _recordings.GetActive();
        var rule = _autoResponder.Match(method, url, reqBodyStr,
            activeRecording?.Rules);
        if (rule != null)
        {
            var arBody = Encoding.UTF8.GetBytes(rule.Body);
            var arSb = new StringBuilder();
            arSb.Append($"HTTP/1.1 {rule.StatusCode} OK\r\n");
            arSb.Append($"Content-Type: {rule.ContentType}\r\n");
            foreach (var (hk, hv) in rule.Headers)
                arSb.Append($"{hk}: {hv}\r\n");
            arSb.Append($"Content-Length: {arBody.Length}\r\n");
            arSb.Append("X-EasyIntercept-AutoResponder: true\r\n");
            arSb.Append("Connection: close\r\n\r\n");

            await stream.WriteAsync(Encoding.ASCII.GetBytes(arSb.ToString()));
            await stream.WriteAsync(arBody);

            var arHeaders = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["Content-Type"] = rule.ContentType,
                ["X-EasyIntercept-AutoResponder"] = "true",
            };
            foreach (var (hk, hv) in rule.Headers)
                arHeaders[hk] = hv;

            var arSession = new ProxySession
            {
                Method = method,
                Url = url,
                RequestHeaders = reqHeaders,
                RequestBody = (actualBody.Length > 0 && isReqText) ? Encoding.UTF8.GetString(actualBody) 
                    : (actualBody.Length > 0 ? $"[{actualBody.Length} bytes binary]" : ""),
                ResponseStatus = rule.StatusCode,
                ResponseHeaders = arHeaders,
                ResponseBody = rule.Body,
                DurationMs = 0,
            };
            _sessions.Add(arSession);
            await _hub.Clients.All.SendAsync("NewSession", arSession);
            _analysis.Capture(method, url, reqHeaders, actualBody, rule.StatusCode, arHeaders, arBody, 0);
            return;
        }

        // Build upstream request
        var httpClient = _httpClientFactory.CreateClient("proxy");
        var req = new HttpRequestMessage(new HttpMethod(method), url);

        foreach (var (key, val) in reqHeaders)
        {
            if (HopByHopHeaders.Contains(key)) continue;
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

        // Send upstream — simple: send, get full response, write back
        var sw = Stopwatch.StartNew();
        HttpResponseMessage upstream;
        try
        {
            upstream = await httpClient.SendAsync(req);
        }
        catch (Exception ex)
        {
            sw.Stop();
            var errBody = Encoding.UTF8.GetBytes(ex.Message);
            await WriteRaw(stream, $"HTTP/1.1 502 Bad Gateway\r\nContent-Length: {errBody.Length}\r\nConnection: close\r\n\r\n");
            await stream.WriteAsync(errBody);
            return;
        }

        using (upstream)
        {
            var respBody = await upstream.Content.ReadAsByteArrayAsync();
            sw.Stop();

            // Collect response headers
            var respHeaders = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var h in upstream.Headers)
                respHeaders[h.Key] = string.Join(", ", h.Value);
            foreach (var h in upstream.Content.Headers)
                respHeaders[h.Key] = string.Join(", ", h.Value);

            // Write full response
            var sb = new StringBuilder();
            sb.Append($"HTTP/1.1 {(int)upstream.StatusCode} {upstream.ReasonPhrase}\r\n");
            foreach (var (key, val) in respHeaders)
            {
                if (key.Equals("Transfer-Encoding", StringComparison.OrdinalIgnoreCase)) continue;
                if (key.Equals("Content-Length", StringComparison.OrdinalIgnoreCase)) continue;
                sb.Append($"{key}: {val}\r\n");
            }
            sb.Append($"Content-Length: {respBody.Length}\r\n");
            sb.Append("Connection: close\r\n\r\n");

            await stream.WriteAsync(Encoding.ASCII.GetBytes(sb.ToString()));
            await stream.WriteAsync(respBody);

            // Log session
            var isText = respBody.Length == 0
                || respBody.Length <= 4096
                || (respHeaders.TryGetValue("Content-Type", out var rct)
                    && (rct.Contains("text/") || rct.Contains("json") || rct.Contains("xml") || rct.Contains("javascript")));

            var session = new ProxySession
            {
                Method = method,
                Url = url,
                RequestHeaders = reqHeaders,
                RequestBody = (actualBody.Length > 0 && isReqText) ? Encoding.UTF8.GetString(actualBody) 
                    : (actualBody.Length > 0 ? $"[{actualBody.Length} bytes binary]" : ""),
                ResponseStatus = (int)upstream.StatusCode,
                ResponseHeaders = respHeaders,
                ResponseBody = isText ? Encoding.UTF8.GetString(respBody) : $"[{respBody.Length} bytes]",
                DurationMs = sw.ElapsedMilliseconds,
            };

            _sessions.Add(session);
            await _hub.Clients.All.SendAsync("NewSession", session);
            _analysis.Capture(method, url, reqHeaders, actualBody, (int)upstream.StatusCode, respHeaders, respBody, sw.ElapsedMilliseconds);

            // Capture into recording (skip auto-responded sessions)
            _recordings.CaptureSession(session);
        }
    }

    private static async Task WriteRaw(Stream stream, string text) =>
        await stream.WriteAsync(Encoding.ASCII.GetBytes(text));

    private static (int headerEnd, int filled) FindHeaderEnd(byte[] buf, int filled)
    {
        for (var i = 0; i <= filled - 4; i++)
        {
            if (buf[i] == '\r' && buf[i + 1] == '\n' && buf[i + 2] == '\r' && buf[i + 3] == '\n')
                return (i, filled);
        }
        return (-1, filled);
    }

    private static bool IsTextContentType(string? contentType)
    {
        if (string.IsNullOrEmpty(contentType)) return false;
        var lower = contentType.ToLowerInvariant();
        return lower.Contains("text/") 
            || lower.Contains("json") 
            || lower.Contains("xml") 
            || lower.Contains("javascript") 
            || lower.Contains("x-www-form-urlencoded");
    }

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
