namespace EasyIntercept.Models;

public class AnalysisEventSummary
{
    public int Sequence { get; set; }
    public string FileName { get; set; } = "";
    public DateTime Timestamp { get; set; }
    public string Method { get; set; } = "";
    public string Url { get; set; } = "";
    public string Host { get; set; } = "";
    public int ResponseStatus { get; set; }
    public long DurationMs { get; set; }
}