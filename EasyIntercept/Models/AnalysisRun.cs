namespace EasyIntercept.Models;

public class AnalysisRun
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    public string HostFilter { get; set; } = "";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? StoppedAt { get; set; }
    public int EventCount { get; set; }
}