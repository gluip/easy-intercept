using System.Buffers;
using System.Text;

namespace EasyIntercept.Proxy;

/// <summary>
/// Accumulates a response body for the session record while it streams past. Text-like bodies are
/// decoded incrementally (so interim snapshots never end in a torn UTF-8 sequence); everything else is
/// kept as bytes for the final classification. Capped at <see cref="MaxCaptureBytes"/>; the client
/// still receives everything.
/// </summary>
public sealed class SessionCapture
{
    public const int MaxCaptureBytes = 16 * 1024 * 1024;
    public const int MaxImageBytes = 5 * 1024 * 1024;
    /// <summary>Bodies up to this size are shown as text whatever the content type says.</summary>
    public const int SmallBodyIsText = 4096;

    private readonly string _contentType;
    private readonly bool _isImage;
    private readonly bool _isTextLike;
    private readonly InterimThrottle _throttle;

    private readonly MemoryStream _bytes = new();
    private readonly StringBuilder _text = new();
    private readonly Decoder _decoder = Encoding.UTF8.GetDecoder();
    private long _captured;
    private long _total;
    private bool _flushed;

    public SessionCapture(string? contentType, int interimIntervalMs = InterimThrottle.DefaultIntervalMs, int interimBytes = InterimThrottle.DefaultBytes)
    {
        _contentType = contentType ?? "";
        var lower = _contentType.ToLowerInvariant();
        _isImage = lower.StartsWith("image/") && !lower.Contains("svg");
        _isTextLike = !_isImage && IsTextLike(lower);
        _throttle = new InterimThrottle(interimIntervalMs, interimBytes);
    }

    public static bool IsTextLike(string? contentType)
    {
        if (string.IsNullOrEmpty(contentType)) return false;
        var lower = contentType.ToLowerInvariant();
        return lower.Contains("text/")
            || lower.Contains("json")
            || lower.Contains("xml")
            || lower.Contains("javascript")
            || lower.Contains("event-stream")
            || lower.Contains("ndjson")
            || lower.Contains("x-www-form-urlencoded");
    }

    public long TotalBytes => _total;
    public long CapturedBytes => _captured;
    public bool Truncated => _total > _captured;
    public bool IsText => _isTextLike;

    public void Append(ReadOnlySpan<byte> chunk)
    {
        _total += chunk.Length;
        _throttle.Count(chunk.Length);

        var room = MaxCaptureBytes - _captured;
        if (room <= 0) return;
        if (chunk.Length > room) chunk = chunk[..(int)room];
        _captured += chunk.Length;

        if (_isTextLike)
            Decode(chunk, flush: false);
        else
            _bytes.Write(chunk);
    }

    /// <summary>Interim UI updates only make sense for text; binary bodies are pushed once, at the end.</summary>
    public bool ShouldPushInterim(long nowMs) => _isTextLike && _throttle.ShouldPush(nowMs);

    /// <summary>
    /// The body as stored in the session. Interim snapshots return the text decoded so far; the final
    /// call flushes the decoder and applies the image/binary/small-body rules.
    /// </summary>
    public string BodyText(bool interim = false)
    {
        if (_isTextLike)
        {
            if (!interim && !_flushed)
            {
                Decode(ReadOnlySpan<byte>.Empty, flush: true);
                _flushed = true;
            }
            return Truncated && !interim ? _text + TruncationMarker() : _text.ToString();
        }

        if (interim) return "";

        if (_isImage)
        {
            if (_total > 0 && _total <= MaxImageBytes && !Truncated)
                return $"data:{_contentType};base64,{Convert.ToBase64String(_bytes.GetBuffer(), 0, (int)_bytes.Length)}";
            return $"[{_total} bytes image]";
        }

        if (_total <= SmallBodyIsText)
            return Encoding.UTF8.GetString(_bytes.GetBuffer(), 0, (int)_bytes.Length);
        return $"[{_total} bytes]";
    }

    private string TruncationMarker() => $"\n\n[EasyIntercept: capture truncated at {_captured} bytes, {_total} bytes total]";

    private void Decode(ReadOnlySpan<byte> chunk, bool flush)
    {
        var chars = ArrayPool<char>.Shared.Rent(Math.Max(16, Encoding.UTF8.GetMaxCharCount(chunk.Length)));
        try
        {
            var n = _decoder.GetChars(chunk, chars, flush);
            _text.Append(chars, 0, n);
        }
        finally
        {
            ArrayPool<char>.Shared.Return(chars);
        }
    }
}
