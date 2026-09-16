using System.Buffers;
using System.Text;

namespace EasyIntercept.Proxy;

public enum PumpOutcome
{
    /// <summary>The whole upstream body reached the client.</summary>
    Completed,
    /// <summary>Writing to the client failed; the upstream read was stopped.</summary>
    ClientGone,
    /// <summary>Reading from the upstream failed; the client got a truncated body.</summary>
    UpstreamFailed,
    /// <summary>The proxy is shutting down.</summary>
    Cancelled,
}

public readonly record struct PumpResult(PumpOutcome Outcome, long BytesRelayed, Exception? Error = null);

/// <summary>
/// Frames and relays an upstream response to the proxy client without buffering it: the head goes out
/// as soon as the upstream headers arrive and every body chunk is written and flushed as it is read.
/// </summary>
public static class ResponseRelay
{
    public const int BufferSize = 64 * 1024;

    internal static readonly HashSet<string> HopByHopHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Connection", "Keep-Alive", "Proxy-Authenticate", "Proxy-Authorization",
        "TE", "Trailers", "Transfer-Encoding", "Upgrade", "Proxy-Connection",
    };

    private static readonly byte[] CrLf = "\r\n"u8.ToArray();
    private static readonly byte[] LastChunk = "0\r\n\r\n"u8.ToArray();

    /// <summary>Responses that never carry a body, whatever the headers say (RFC 9110 §6.4.1).</summary>
    public static bool HasNoBody(string method, int status) =>
        method.Equals("HEAD", StringComparison.OrdinalIgnoreCase)
        || status is 204 or 304
        || (status >= 100 && status < 200);

    /// <summary>
    /// Builds the status line and headers for the client. Hop-by-hop, Content-Length and
    /// Content-Encoding headers from upstream are dropped: the body is relayed decompressed and
    /// re-framed by us. <paramref name="contentLength"/> selects Content-Length framing; without it a
    /// body is sent chunked. The connection is always closed after the response.
    /// </summary>
    public static byte[] BuildHead(int status, string? reason, IEnumerable<KeyValuePair<string, string>> headers, long? contentLength, bool hasBody)
    {
        var sb = new StringBuilder();
        sb.Append("HTTP/1.1 ").Append(status).Append(' ').Append(reason ?? "").Append("\r\n");
        foreach (var (key, val) in headers)
        {
            if (HopByHopHeaders.Contains(key)) continue;
            if (key.Equals("Content-Length", StringComparison.OrdinalIgnoreCase)) continue;
            if (key.Equals("Content-Encoding", StringComparison.OrdinalIgnoreCase)) continue;
            sb.Append(key).Append(": ").Append(val).Append("\r\n");
        }
        if (contentLength is long len)
            sb.Append("Content-Length: ").Append(len).Append("\r\n");
        else if (hasBody)
            sb.Append("Transfer-Encoding: chunked\r\n");
        sb.Append("Connection: close\r\n\r\n");
        return Encoding.ASCII.GetBytes(sb.ToString());
    }

    /// <summary>
    /// Copies <paramref name="source"/> to <paramref name="client"/> chunk by chunk, flushing after every
    /// chunk (SslStream only emits a TLS record on flush). <paramref name="onChunk"/> sees every chunk after
    /// it was written. Read and write failures are reported separately so the caller knows which side went away.
    /// </summary>
    public static async Task<PumpResult> PumpAsync(
        Stream source, Stream client, bool chunked,
        Func<ReadOnlyMemory<byte>, ValueTask>? onChunk, CancellationToken ct)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
        long total = 0;
        try
        {
            while (true)
            {
                int n;
                try
                {
                    n = await source.ReadAsync(buffer, ct);
                }
                catch (OperationCanceledException) { return new(PumpOutcome.Cancelled, total); }
                catch (Exception ex) { return new(PumpOutcome.UpstreamFailed, total, ex); }

                if (n == 0) break;

                try
                {
                    if (chunked)
                        await client.WriteAsync(Encoding.ASCII.GetBytes(n.ToString("X") + "\r\n"), ct);
                    await client.WriteAsync(buffer.AsMemory(0, n), ct);
                    if (chunked)
                        await client.WriteAsync(CrLf, ct);
                    await client.FlushAsync(ct);
                }
                catch (OperationCanceledException) { return new(PumpOutcome.Cancelled, total); }
                catch (Exception ex) { return new(PumpOutcome.ClientGone, total, ex); }

                total += n;
                if (onChunk is not null)
                    await onChunk(buffer.AsMemory(0, n));
            }

            if (chunked)
            {
                try
                {
                    await client.WriteAsync(LastChunk, ct);
                    await client.FlushAsync(ct);
                }
                catch (OperationCanceledException) { return new(PumpOutcome.Cancelled, total); }
                catch (Exception ex) { return new(PumpOutcome.ClientGone, total, ex); }
            }
            return new(PumpOutcome.Completed, total);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }
}
