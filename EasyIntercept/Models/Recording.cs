using EasyIntercept.AutoResponder;

namespace EasyIntercept.Models;

public class Recording
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public bool Active { get; set; }
    public List<AutoResponderRule> Rules { get; set; } = new();
}
