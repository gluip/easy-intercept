using System.Net.Security;
using System.Net.Sockets;
using System.Text;

namespace EasyIntercept.Tests.Proxy;

/// <summary>
/// Talks HTTP/1.1 to the proxy byte by byte so tests can assert on the exact framing (chunk by chunk,
/// head before body) instead of what HttpClient chooses to surface.
/// </summary>
public sealed class RawProxyClient : IDisposable
{
    private readonly TcpClient _tcp;
    private Stream _stream;
    private readonly byte[] _buf = new byte[64 * 1024];
    private int _start, _end;

    public Stream Stream => _stream;

    private RawProxyClient(TcpClient tcp)
    {
        _tcp = tcp;
        _stream = tcp.GetStream();
    }

    public static async Task<RawProxyClient> ConnectAsync(int proxyPort)
    {
        var tcp = new TcpClient();
        tcp.NoDelay = true;
        await tcp.ConnectAsync("127.0.0.1", proxyPort);
        return new RawProxyClient(tcp);
    }

    /// <summary>CONNECT to host:port; with <paramref name="tls"/> the tunnel is wrapped in TLS, trusting the proxy's minted certificate.</summary>
    public async Task TunnelAsync(string hostPort, bool tls)
    {
        await SendAsync($"CONNECT {hostPort} HTTP/1.1\r\nHost: {hostPort}\r\n\r\n");
        var (statusLine, _) = await ReadHeadAsync();
        if (!statusLine.Contains(" 200 ")) throw new InvalidOperationException("CONNECT refused: " + statusLine);
        if (!tls) return;

        var ssl = new SslStream(_stream, leaveInnerStreamOpen: false, (_, _, _, _) => true);
        await ssl.AuthenticateAsClientAsync(hostPort.Split(':')[0]);
        _stream = ssl;
    }

    public async Task SendAsync(string text)
    {
        await _stream.WriteAsync(Encoding.ASCII.GetBytes(text));
        await _stream.FlushAsync();
    }

    public async Task<(string StatusLine, List<KeyValuePair<string, string>> Headers)> ReadHeadAsync(CancellationToken ct = default)
    {
        var statusLine = await ReadLineAsync(ct) ?? throw new EndOfStreamException("connection closed before the status line");
        var headers = new List<KeyValuePair<string, string>>();
        while (true)
        {
            var line = await ReadLineAsync(ct) ?? throw new EndOfStreamException("connection closed inside the head");
            if (line.Length == 0) break;
            var colon = line.IndexOf(':');
            headers.Add(new(line[..colon].Trim(), line[(colon + 1)..].Trim()));
        }
        return (statusLine, headers);
    }

    /// <summary>Reads one chunk of a chunked body; returns null for the terminating chunk.</summary>
    public async Task<byte[]?> ReadChunkAsync(CancellationToken ct = default)
    {
        var sizeLine = await ReadLineAsync(ct) ?? throw new EndOfStreamException("connection closed inside chunked body");
        var size = Convert.ToInt32(sizeLine.Split(';')[0].Trim(), 16);
        if (size == 0)
        {
            await ReadLineAsync(ct); // trailer terminator
            return null;
        }
        var data = await ReadExactAsync(size, ct);
        await ReadLineAsync(ct); // CRLF after the data
        return data;
    }

    /// <summary>Reads chunks until the accumulated text contains <paramref name="needle"/>.</summary>
    public async Task<string> ReadChunksUntilAsync(string needle, CancellationToken ct = default)
    {
        var sb = new StringBuilder();
        while (!sb.ToString().Contains(needle))
        {
            var chunk = await ReadChunkAsync(ct) ?? throw new EndOfStreamException($"stream ended before '{needle}' arrived; got: {sb}");
            sb.Append(Encoding.UTF8.GetString(chunk));
        }
        return sb.ToString();
    }

    public async Task<byte[]> ReadExactAsync(int count, CancellationToken ct = default)
    {
        var result = new byte[count];
        var offset = 0;
        while (offset < count)
        {
            if (_end == _start && !await FillAsync(ct)) throw new EndOfStreamException($"connection closed after {offset} of {count} bytes");
            var n = Math.Min(_end - _start, count - offset);
            Buffer.BlockCopy(_buf, _start, result, offset, n);
            _start += n;
            offset += n;
        }
        return result;
    }

    public async Task<byte[]> ReadToEndAsync(CancellationToken ct = default)
    {
        var ms = new MemoryStream();
        ms.Write(_buf, _start, _end - _start);
        _start = _end = 0;
        await _stream.CopyToAsync(ms, ct);
        return ms.ToArray();
    }

    private async Task<string?> ReadLineAsync(CancellationToken ct)
    {
        var sb = new StringBuilder();
        while (true)
        {
            if (_end == _start && !await FillAsync(ct)) return sb.Length == 0 ? null : sb.ToString();
            var b = _buf[_start++];
            if (b == '\n')
            {
                if (sb.Length > 0 && sb[^1] == '\r') sb.Length--;
                return sb.ToString();
            }
            sb.Append((char)b);
        }
    }

    private async Task<bool> FillAsync(CancellationToken ct)
    {
        _start = 0;
        _end = await _stream.ReadAsync(_buf, ct);
        return _end > 0;
    }

    public void Close() => _tcp.Close();

    public void Dispose()
    {
        _stream.Dispose();
        _tcp.Dispose();
    }
}
