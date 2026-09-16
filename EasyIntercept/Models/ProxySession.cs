namespace EasyIntercept.Models;

public record ProxySession
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public DateTime Timestamp { get; init; } = DateTime.UtcNow;
    public string Method { get; init; } = "";
    public string Url { get; init; } = "";
    public Dictionary<string, string> RequestHeaders { get; init; } = new();
    public string RequestBody { get; init; } = "";
    public int ResponseStatus { get; init; }
    public Dictionary<string, string> ResponseHeaders { get; init; } = new();
    public string ResponseBody { get; init; } = "";
    public long DurationMs { get; init; }

    /// <summary>
    /// False while a streaming response (or an open WebSocket) is still being relayed to the client;
    /// the session file is only (re)written once it is true. Defaults to true so older files,
    /// auto-responder and error sessions, which never stream, need no migration.
    /// </summary>
    public bool ResponseComplete { get; init; } = true;

    /// <summary>Milliseconds from request start until the upstream response headers arrived. 0 when unknown.</summary>
    public long TimeToFirstByteMs { get; init; }

    /// <summary>Messages of a WebSocket session, in order; null for ordinary HTTP sessions.</summary>
    public List<WebSocketMessage>? WebSocketMessages { get; init; }
}

/// <param name="Direction">"out" = client → server, "in" = server → client.</param>
/// <param name="OffsetMs">Milliseconds since the session started.</param>
/// <param name="Type">"text", "binary" or "close".</param>
/// <param name="Data">Text payload, a placeholder for binary, or "{status} {reason}" for close.</param>
public record WebSocketMessage(string Direction, long OffsetMs, string Type, string Data);
