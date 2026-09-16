using System.Text;
using EasyIntercept.Proxy;
using Xunit;

namespace EasyIntercept.Tests.Proxy;

public class SessionCaptureTests
{
    [Theory]
    [InlineData("text/event-stream")]
    [InlineData("application/json; charset=utf-8")]
    [InlineData("application/x-ndjson")]
    [InlineData("text/plain")]
    [InlineData("application/xml")]
    [InlineData("application/javascript")]
    public void Text_like_content_types_are_decoded(string contentType)
    {
        var c = new SessionCapture(contentType);
        c.Append(Encoding.UTF8.GetBytes("data: hi\n\n"));
        Assert.True(c.IsText);
        Assert.Equal("data: hi\n\n", c.BodyText());
    }

    [Fact]
    public void Small_binary_bodies_are_still_shown_as_text()
    {
        var c = new SessionCapture("application/octet-stream");
        c.Append(Encoding.UTF8.GetBytes("tiny"));
        Assert.Equal("tiny", c.BodyText());
    }

    [Fact]
    public void Large_binary_bodies_become_a_size_placeholder()
    {
        var c = new SessionCapture("application/octet-stream");
        c.Append(new byte[5000]);
        Assert.Equal("[5000 bytes]", c.BodyText());
        Assert.Equal("", c.BodyText(interim: true));
        Assert.False(c.ShouldPushInterim(0));
    }

    [Fact]
    public void Empty_body_is_empty_string()
    {
        Assert.Equal("", new SessionCapture("application/octet-stream").BodyText());
        Assert.Equal("", new SessionCapture("application/json").BodyText());
    }

    [Fact]
    public void Images_become_data_urls_up_to_the_limit()
    {
        var small = new SessionCapture("image/png");
        small.Append([1, 2, 3]);
        Assert.Equal("data:image/png;base64," + Convert.ToBase64String([1, 2, 3]), small.BodyText());

        var big = new SessionCapture("image/png");
        big.Append(new byte[SessionCapture.MaxImageBytes + 1]);
        Assert.Equal($"[{SessionCapture.MaxImageBytes + 1} bytes image]", big.BodyText());
    }

    [Fact]
    public void Svg_is_text_not_image()
    {
        var c = new SessionCapture("image/svg+xml");
        c.Append(Encoding.UTF8.GetBytes("<svg/>"));
        Assert.Equal("<svg/>", c.BodyText());
    }

    [Fact]
    public void Multibyte_character_split_across_chunks_is_decoded_correctly()
    {
        var euro = Encoding.UTF8.GetBytes("€"); // 3 bytes
        var c = new SessionCapture("text/plain");
        c.Append(euro[..1]);
        Assert.Equal("", c.BodyText(interim: true)); // never a torn replacement char in an interim snapshot
        c.Append(euro[1..]);
        Assert.Equal("€", c.BodyText(interim: true));
        Assert.Equal("€", c.BodyText());
    }

    [Fact]
    public void Final_text_flushes_a_dangling_partial_sequence_once()
    {
        var c = new SessionCapture("text/plain");
        c.Append(Encoding.UTF8.GetBytes("ok")[..2]);
        c.Append(Encoding.UTF8.GetBytes("€")[..2]);
        var final = c.BodyText();
        Assert.StartsWith("ok", final);
        Assert.Equal(final, c.BodyText()); // idempotent
    }

    [Fact]
    public void Capture_is_capped_but_total_keeps_counting()
    {
        var c = new SessionCapture("text/plain");
        var chunk = new byte[1024 * 1024];
        Array.Fill(chunk, (byte)'x');
        for (var i = 0; i < 17; i++) c.Append(chunk);

        Assert.Equal(17L * 1024 * 1024, c.TotalBytes);
        Assert.Equal(SessionCapture.MaxCaptureBytes, c.CapturedBytes);
        Assert.True(c.Truncated);
        var text = c.BodyText();
        Assert.Contains("capture truncated at 16777216 bytes, 17825792 bytes total", text);
        Assert.Equal(SessionCapture.MaxCaptureBytes, text.IndexOf("\n\n[EasyIntercept", StringComparison.Ordinal));
    }

    [Fact]
    public void Capped_binary_keeps_the_true_total()
    {
        var c = new SessionCapture("application/octet-stream");
        c.Append(new byte[SessionCapture.MaxCaptureBytes]);
        c.Append(new byte[10]);
        Assert.Equal($"[{SessionCapture.MaxCaptureBytes + 10} bytes]", c.BodyText());
    }

    [Fact]
    public void Interim_pushes_are_throttled_by_time_or_bytes()
    {
        var c = new SessionCapture("text/plain", interimIntervalMs: 200, interimBytes: 100);
        c.Append(new byte[1]);
        Assert.True(c.ShouldPushInterim(0));        // first push is immediate
        c.Append(new byte[1]);
        Assert.False(c.ShouldPushInterim(50));      // too soon, too little
        Assert.False(c.ShouldPushInterim(199));
        Assert.True(c.ShouldPushInterim(200));      // interval elapsed
        c.Append(new byte[100]);
        Assert.True(c.ShouldPushInterim(201));      // byte threshold reached
        Assert.False(c.ShouldPushInterim(202));
    }

    [Fact]
    public void Interim_text_grows_with_each_chunk()
    {
        var c = new SessionCapture("text/event-stream");
        c.Append(Encoding.UTF8.GetBytes("data: 1\n\n"));
        Assert.Equal("data: 1\n\n", c.BodyText(interim: true));
        c.Append(Encoding.UTF8.GetBytes("data: 2\n\n"));
        Assert.Equal("data: 1\n\ndata: 2\n\n", c.BodyText(interim: true));
    }
}
