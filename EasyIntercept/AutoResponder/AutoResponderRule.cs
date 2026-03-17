namespace EasyIntercept.AutoResponder;

public class AutoResponderRule
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    public string Method { get; set; } = "*";
    public string UrlPattern { get; set; } = "";
    public bool Enabled { get; set; } = true;
    public int StatusCode { get; set; } = 200;
    public string ContentType { get; set; } = "application/json";
    public Dictionary<string, string> Headers { get; set; } = new();
    public string Body { get; set; } = "";
}
