using System.Collections.Concurrent;
using System.Text.RegularExpressions;

namespace EasyIntercept.AutoResponder;

public class AutoResponderStore
{
    private readonly ConcurrentDictionary<Guid, AutoResponderRule> _rules = new();

    public void AddOrUpdate(AutoResponderRule rule) => _rules[rule.Id] = rule;

    public void Remove(Guid id) => _rules.TryRemove(id, out _);

    public AutoResponderRule? Get(Guid id) =>
        _rules.TryGetValue(id, out var rule) ? rule : null;

    public IEnumerable<AutoResponderRule> GetAll() => _rules.Values.ToArray();

    public AutoResponderRule? Match(string method, string url)
    {
        foreach (var rule in _rules.Values)
        {
            if (!rule.Enabled) continue;
            if (rule.Method != "*" && !rule.Method.Equals(method, StringComparison.OrdinalIgnoreCase)) continue;
            try
            {
                if (Regex.IsMatch(url, rule.UrlPattern, RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(200)))
                    return rule;
            }
            catch (RegexMatchTimeoutException) { }
            catch (ArgumentException) { }
        }
        return null;
    }
}
