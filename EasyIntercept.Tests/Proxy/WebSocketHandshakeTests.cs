using System.Net.WebSockets;
using System.Text;
using EasyIntercept.Proxy;
using Xunit;

namespace EasyIntercept.Tests.Proxy;

public class WebSocketHandshakeTests
{
    [Fact]
    public void Accept_key_matches_the_rfc_6455_example() =>
        Assert.Equal("s3pPLMBiTxaQ9kYGzzhZRbK+xOo=", WebSocketRelay.ComputeAccept("dGhlIHNhbXBsZSBub25jZQ=="));

    [Theory]
    [InlineData("https://example.com/socket?x=1", "wss://example.com/socket?x=1")]
    [InlineData("http://example.com:8080/ws", "ws://example.com:8080/ws")]
    [InlineData("HTTP://example.com/ws", "ws://example.com/ws")]
    [InlineData("ws://already", "ws://already")]
    public void Http_urls_map_to_websocket_urls(string input, string expected) =>
        Assert.Equal(expected, WebSocketRelay.ToWebSocketUrl(input));

    [Theory]
    [InlineData("Cookie", true)]
    [InlineData("Authorization", true)]
    [InlineData("Origin", true)]
    [InlineData("X-Custom", true)]
    [InlineData("User-Agent", true)]
    [InlineData("Host", false)]
    [InlineData("Connection", false)]
    [InlineData("Upgrade", false)]
    [InlineData("Sec-WebSocket-Key", false)]
    [InlineData("sec-websocket-version", false)]
    [InlineData("Sec-WebSocket-Extensions", false)]
    [InlineData("Sec-WebSocket-Protocol", false)]
    [InlineData("Proxy-Connection", false)]
    public void Only_end_to_end_headers_are_forwarded_to_the_upstream_handshake(string name, bool forwarded) =>
        Assert.Equal(forwarded, WebSocketRelay.IsForwardedHandshakeHeader(name));

    [Fact]
    public void Requested_sub_protocols_are_split_and_trimmed()
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["Sec-WebSocket-Protocol"] = "graphql-ws, json ,, xml" };
        Assert.Equal(["graphql-ws", "json", "xml"], WebSocketRelay.RequestedSubProtocols(headers));
        Assert.Empty(WebSocketRelay.RequestedSubProtocols(new Dictionary<string, string>()));
    }

    [Fact]
    public void Client_headers_are_applied_to_client_websocket_options()
    {
        var options = new ClientWebSocket().Options;
        WebSocketRelay.ApplyClientHeaders(options, new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Host"] = "example.com",
            ["Sec-WebSocket-Key"] = "abc",
            ["Sec-WebSocket-Protocol"] = "a, b",
            ["Cookie"] = "session=1",
        });
        // ClientWebSocketOptions exposes no getters; the point is that nothing here throws
        // (Host/Sec-WebSocket-* would if they were passed to SetRequestHeader).
        Assert.NotNull(options);
    }

    [Fact]
    public void Handshake_response_is_a_valid_101()
    {
        var (head, headers) = WebSocketRelay.BuildHandshakeResponse("dGhlIHNhbXBsZSBub25jZQ==", "chat");
        var text = Encoding.ASCII.GetString(head);

        Assert.StartsWith("HTTP/1.1 101 Switching Protocols\r\n", text);
        Assert.Contains("Upgrade: websocket\r\n", text);
        Assert.Contains("Connection: Upgrade\r\n", text);
        Assert.Contains("Sec-WebSocket-Accept: s3pPLMBiTxaQ9kYGzzhZRbK+xOo=\r\n", text);
        Assert.Contains("Sec-WebSocket-Protocol: chat\r\n", text);
        Assert.EndsWith("\r\n\r\n", text);
        Assert.Equal("chat", headers["Sec-WebSocket-Protocol"]);

        var (_, noProto) = WebSocketRelay.BuildHandshakeResponse("x", null);
        Assert.False(noProto.ContainsKey("Sec-WebSocket-Protocol"));
    }

    [Fact]
    public void Capture_formats_text_binary_and_truncation()
    {
        Assert.Equal("hello", WebSocketCapture.FormatPayload(WebSocketMessageType.Text, "hello"u8, 5));
        Assert.Equal("[2048 bytes binary]", WebSocketCapture.FormatPayload(WebSocketMessageType.Binary, new byte[100], 2048));
        Assert.Equal("hel… [truncated, 100000 bytes total]", WebSocketCapture.FormatPayload(WebSocketMessageType.Text, "hel"u8, 100_000));
    }

    [Fact]
    public void Capture_keeps_the_first_thousand_messages_and_notes_the_rest()
    {
        var c = new WebSocketCapture();
        for (var i = 0; i < WebSocketCapture.MaxMessages + 5; i++)
            c.Add("in", WebSocketMessageType.Text, Encoding.UTF8.GetBytes(i.ToString()), 1, i);
        c.AddClose("out", WebSocketCloseStatus.NormalClosure, "bye", 2000);

        var snap = c.Snapshot();
        Assert.Equal(WebSocketCapture.MaxMessages + 2, snap.Count);
        Assert.Equal("note", snap[^2].Type);
        Assert.Contains("5 more messages", snap[^2].Data);
        Assert.Equal("close", snap[^1].Type);
        Assert.Equal("1000 bye", snap[^1].Data);
        Assert.Equal(5, c.Dropped);
    }

    [Fact]
    public void Capture_records_only_the_first_close()
    {
        var c = new WebSocketCapture();
        c.AddClose("out", WebSocketCloseStatus.NormalClosure, "bye", 1);
        c.AddClose("in", WebSocketCloseStatus.NormalClosure, "bye", 2);
        var snap = c.Snapshot();
        Assert.Single(snap);
        Assert.Equal("out", snap[0].Direction);
    }

    [Fact]
    public void Capture_close_without_reason_is_just_the_code()
    {
        var c = new WebSocketCapture();
        c.AddClose("in", (WebSocketCloseStatus)4000, null, 1);
        Assert.Equal("4000", c.Snapshot()[0].Data);
    }
}
