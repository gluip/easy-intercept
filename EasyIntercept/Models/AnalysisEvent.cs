namespace EasyIntercept.Models;

public class AnalysisEvent
{
    public int Sequence { get; set; }
    public string FileName { get; set; } = "";
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
    public string Method { get; set; } = "";
    public string Url { get; set; } = "";
    public string Host { get; set; } = "";
    public long DurationMs { get; set; }
    public Dictionary<string, string> RequestHeaders { get; set; } = new();
    public string RequestContentType { get; set; } = "";
    public int RequestBodyByteLength { get; set; }
    public string RequestBody { get; set; } = "";
    public string? RequestBodySkippedReason { get; set; }
    public int ResponseStatus { get; set; }
    public Dictionary<string, string> ResponseHeaders { get; set; } = new();
    public string ResponseContentType { get; set; } = "";
    public int ResponseBodyByteLength { get; set; }
    public string ResponseBody { get; set; } = "";
    public string? ResponseBodySkippedReason { get; set; }
}