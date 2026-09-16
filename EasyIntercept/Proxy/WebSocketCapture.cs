using System.Net.WebSockets;
using System.Text;
using EasyIntercept.Models;

namespace EasyIntercept.Proxy;

/// <summary>
/// Collects the messages of a relayed WebSocket for the session record. Both relay directions add
/// concurrently. Data messages are capped at <see cref="MaxMessages"/> (a note records how many were
/// dropped); text payloads are cut at <see cref="MaxTextBytes"/>; binary payloads are stored as a size.
/// </summary>
public sealed class WebSocketCapture
{
    public const int MaxMessages = 1000;
    public const int MaxTextBytes = 64 * 1024;

    public const string DirectionOut = "out"; // client → server
    public const string DirectionIn = "in";   // server → client

    private readonly object _lock = new();
    private readonly List<WebSocketMessage> _messages = new();
    private readonly InterimThrottle _throttle;
    private int _dropped;
    private bool _closeRecorded;

    public WebSocketCapture(int interimIntervalMs = InterimThrottle.DefaultIntervalMs, int interimBytes = InterimThrottle.DefaultBytes)
    {
        _throttle = new InterimThrottle(interimIntervalMs, interimBytes);
    }

    public int Count { get { lock (_lock) return _messages.Count; } }
    public int Dropped { get { lock (_lock) return _dropped; } }

    /// <param name="captured">The first bytes of the message, at most <see cref="MaxTextBytes"/>.</param>
    /// <param name="totalBytes">The full message length.</param>
    public void Add(string direction, WebSocketMessageType type, ReadOnlySpan<byte> captured, long totalBytes, long offsetMs)
    {
        var data = FormatPayload(type, captured, totalBytes);
        lock (_lock)
        {
            _throttle.Count(totalBytes);
            if (_messages.Count >= MaxMessages) { _dropped++; return; }
            _messages.Add(new WebSocketMessage(direction, offsetMs, type == WebSocketMessageType.Binary ? "binary" : "text", data));
        }
    }

    /// <summary>Records the close initiated by <paramref name="direction"/>; the peer's close echo is not recorded again.</summary>
    public void AddClose(string direction, WebSocketCloseStatus? status, string? reason, long offsetMs)
    {
        lock (_lock)
        {
            if (_closeRecorded) return;
            _closeRecorded = true;
            var code = status.HasValue ? ((int)status.Value).ToString() : "";
            _messages.Add(new WebSocketMessage(direction, offsetMs, "close", string.IsNullOrEmpty(reason) ? code : $"{code} {reason}".Trim()));
        }
    }

    public bool ShouldPushInterim(long nowMs)
    {
        lock (_lock) return _throttle.ShouldPush(nowMs);
    }

    /// <summary>A copy of the messages so far, with a note when data messages were dropped.</summary>
    public List<WebSocketMessage> Snapshot()
    {
        lock (_lock)
        {
            var list = new List<WebSocketMessage>(_messages.Count + 1);
            list.AddRange(_messages);
            if (_dropped > 0)
            {
                var note = new WebSocketMessage(DirectionIn, list.Count > 0 ? list[^1].OffsetMs : 0, "note",
                    $"[{_dropped} more messages not captured; only the first {MaxMessages} are kept]");
                // Keep the close, if any, as the last entry.
                if (list.Count > 0 && list[^1].Type == "close") list.Insert(list.Count - 1, note);
                else list.Add(note);
            }
            return list;
        }
    }

    public static string FormatPayload(WebSocketMessageType type, ReadOnlySpan<byte> captured, long totalBytes)
    {
        if (type == WebSocketMessageType.Binary)
            return $"[{totalBytes} bytes binary]";
        var text = Encoding.UTF8.GetString(captured);
        return totalBytes > captured.Length
            ? $"{text}… [truncated, {totalBytes} bytes total]"
            : text;
    }
}
