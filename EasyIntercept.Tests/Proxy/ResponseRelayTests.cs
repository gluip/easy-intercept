using System.Text;
using EasyIntercept.Proxy;
using Xunit;

namespace EasyIntercept.Tests.Proxy;

public class ResponseRelayTests
{
    /// <summary>Yields one predefined chunk per ReadAsync call, like a network stream would.</summary>
    private sealed class ChunkedSource(params string[] chunks) : Stream
    {
        private int _i;
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            await Task.Yield();
            if (_i >= chunks.Length) return 0;
            var bytes = Encoding.UTF8.GetBytes(chunks[_i++]);
            bytes.CopyTo(buffer);
            return bytes.Length;
        }
        public override bool CanRead => true; public override bool CanSeek => false; public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long o, SeekOrigin s) => throw new NotSupportedException();
        public override void SetLength(long v) => throw new NotSupportedException();
        public override void Write(byte[] b, int o, int c) => throw new NotSupportedException();
    }

    private sealed class ThrowingSource : Stream
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) => throw new IOException("upstream reset");
        public override int Read(byte[] buffer, int offset, int count) => throw new IOException("upstream reset");
        public override bool CanRead => true; public override bool CanSeek => false; public override bool CanWrite => false;
        public override long Length => 0; public override long Position { get => 0; set { } }
        public override void Flush() { } public override long Seek(long o, SeekOrigin s) => 0; public override void SetLength(long v) { }
        public override void Write(byte[] b, int o, int c) { }
    }

    /// <summary>Counts flushes and can fail after N writes to simulate a client that went away.</summary>
    private sealed class SpyStream(int failAfterWrites = int.MaxValue) : MemoryStream
    {
        public int Flushes;
        private int _writes;
        public override Task FlushAsync(CancellationToken ct) { Flushes++; return Task.CompletedTask; }
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
        {
            if (++_writes > failAfterWrites) throw new IOException("client gone");
            return base.WriteAsync(buffer, ct);
        }
    }

    [Fact]
    public async Task Chunked_framing_matches_rfc_9112()
    {
        var client = new SpyStream();
        var result = await ResponseRelay.PumpAsync(new ChunkedSource("ab", "cde"), client, chunked: true, null, default);

        Assert.Equal(PumpOutcome.Completed, result.Outcome);
        Assert.Equal(5, result.BytesRelayed);
        Assert.Equal("2\r\nab\r\n3\r\ncde\r\n0\r\n\r\n", Encoding.ASCII.GetString(client.ToArray()));
    }

    [Fact]
    public async Task Unchunked_body_is_copied_verbatim()
    {
        var client = new SpyStream();
        await ResponseRelay.PumpAsync(new ChunkedSource("ab", "cde"), client, chunked: false, null, default);
        Assert.Equal("abcde", Encoding.ASCII.GetString(client.ToArray()));
    }

    [Fact]
    public async Task Empty_chunked_body_sends_only_the_terminator()
    {
        var client = new SpyStream();
        await ResponseRelay.PumpAsync(new ChunkedSource(), client, chunked: true, null, default);
        Assert.Equal("0\r\n\r\n", Encoding.ASCII.GetString(client.ToArray()));
    }

    [Fact]
    public async Task Flushes_after_every_chunk_and_reports_each_chunk()
    {
        var client = new SpyStream();
        var seen = new List<string>();
        await ResponseRelay.PumpAsync(new ChunkedSource("a", "bb", "ccc"), client, chunked: true,
            chunk => { seen.Add(Encoding.ASCII.GetString(chunk.Span)); return ValueTask.CompletedTask; }, default);

        Assert.Equal(["a", "bb", "ccc"], seen);
        Assert.Equal(4, client.Flushes); // one per chunk plus the terminator
    }

    [Fact]
    public async Task Client_write_failure_is_reported_as_client_gone_with_bytes_so_far()
    {
        // chunked: size line, data, CRLF = 3 writes per chunk; fail inside the second chunk
        var client = new SpyStream(failAfterWrites: 4);
        var result = await ResponseRelay.PumpAsync(new ChunkedSource("ab", "cd", "ef"), client, chunked: true, null, default);

        Assert.Equal(PumpOutcome.ClientGone, result.Outcome);
        Assert.Equal(2, result.BytesRelayed);
        Assert.IsType<IOException>(result.Error);
    }

    [Fact]
    public async Task Upstream_read_failure_is_reported_and_no_terminator_is_sent()
    {
        var client = new SpyStream();
        var result = await ResponseRelay.PumpAsync(new ThrowingSource(), client, chunked: true, null, default);

        Assert.Equal(PumpOutcome.UpstreamFailed, result.Outcome);
        Assert.Equal("upstream reset", result.Error?.Message);
        Assert.Empty(client.ToArray()); // a truncated chunked body must not look complete
    }

    [Fact]
    public async Task Cancellation_stops_the_pump()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var result = await ResponseRelay.PumpAsync(new ChunkedSource("a"), new SpyStream(), chunked: true, null, cts.Token);
        Assert.Equal(PumpOutcome.Cancelled, result.Outcome);
    }

    [Fact]
    public void Head_drops_framing_headers_from_upstream_and_uses_chunked_when_length_unknown()
    {
        var head = Encoding.ASCII.GetString(ResponseRelay.BuildHead(200, "OK",
        [
            new("Content-Type", "text/event-stream"),
            new("Transfer-Encoding", "chunked"),
            new("Content-Length", "123"),
            new("Content-Encoding", "gzip"),
            new("Connection", "keep-alive"),
            new("Keep-Alive", "timeout=5"),
            new("Set-Cookie", "a=1"),
            new("Set-Cookie", "b=2"),
        ], contentLength: null, hasBody: true));

        Assert.StartsWith("HTTP/1.1 200 OK\r\n", head);
        Assert.Contains("Content-Type: text/event-stream\r\n", head);
        Assert.Contains("Set-Cookie: a=1\r\nSet-Cookie: b=2\r\n", head);
        Assert.Contains("Transfer-Encoding: chunked\r\n", head);
        Assert.DoesNotContain("Content-Length", head);
        Assert.DoesNotContain("gzip", head);
        Assert.DoesNotContain("keep-alive", head, StringComparison.OrdinalIgnoreCase);
        Assert.EndsWith("Connection: close\r\n\r\n", head);
        Assert.Single(head.Split("Transfer-Encoding")[1..]); // exactly one
    }

    [Fact]
    public void Head_uses_content_length_when_known()
    {
        var head = Encoding.ASCII.GetString(ResponseRelay.BuildHead(200, "OK", [new("Content-Type", "application/json")], 42, hasBody: true));
        Assert.Contains("Content-Length: 42\r\n", head);
        Assert.DoesNotContain("Transfer-Encoding", head);
    }

    [Fact]
    public void Head_without_body_has_no_framing()
    {
        var head = Encoding.ASCII.GetString(ResponseRelay.BuildHead(204, "No Content", [new("X-A", "1")], null, hasBody: false));
        Assert.Equal("HTTP/1.1 204 No Content\r\nX-A: 1\r\nConnection: close\r\n\r\n", head);
    }

    [Fact]
    public void Head_survives_an_empty_reason_phrase()
    {
        var head = Encoding.ASCII.GetString(ResponseRelay.BuildHead(200, "", [], 0, hasBody: true));
        Assert.StartsWith("HTTP/1.1 200 \r\n", head);
    }

    [Theory]
    [InlineData("HEAD", 200, true)]
    [InlineData("GET", 204, true)]
    [InlineData("GET", 304, true)]
    [InlineData("GET", 100, true)]
    [InlineData("GET", 200, false)]
    [InlineData("POST", 404, false)]
    [InlineData("GET", 500, false)]
    public void HasNoBody_follows_rfc_9110(string method, int status, bool expected) =>
        Assert.Equal(expected, ResponseRelay.HasNoBody(method, status));
}
