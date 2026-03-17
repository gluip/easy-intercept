namespace EasyIntercept.Models;

public class ProxySession
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
}
