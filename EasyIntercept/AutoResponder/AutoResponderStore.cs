using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using EasyIntercept.Persistence;

namespace EasyIntercept.AutoResponder;

public class AutoResponderStore
{
    private readonly ConcurrentDictionary<Guid, AutoResponderRule> _rules = new();
    private readonly JsonPersistence _persistence;

    public AutoResponderStore(JsonPersistence persistence)
    {
        _persistence = persistence;
    }

    public void Init()
    {
        _rules.Clear();
        foreach (var rule in _persistence.LoadAutoResponderRules())
            _rules[rule.Id] = rule;
    }

    public void ReloadFromDisk() => Init();

    public void AddOrUpdate(AutoResponderRule rule)
    {
        _rules[rule.Id] = rule;
        _persistence.SaveAutoResponderRule(rule);
    }

    public void Remove(Guid id)
    {
        _rules.TryRemove(id, out _);
        _persistence.DeleteAutoResponderRule(id);
    }

    public AutoResponderRule? Get(Guid id) =>
        _rules.TryGetValue(id, out var rule) ? rule : null;

    public IEnumerable<AutoResponderRule> GetAll() => _rules.Values.ToArray();

    public AutoResponderRule? Match(string method, string url, string requestBody,
        IEnumerable<AutoResponderRule>? extraRules = null)
    {
        // Manual rules first (highest priority)
        var match = MatchRules(_rules.Values, method, url, requestBody);
        if (match != null) return match;

        // Then extra rules (e.g. active recording)
        if (extraRules != null)
            return MatchRules(extraRules, method, url, requestBody);

        return null;
    }

    private static AutoResponderRule? MatchRules(IEnumerable<AutoResponderRule> rules,
        string method, string url, string requestBody)
    {
        foreach (var rule in rules)
        {
            if (!rule.Enabled) continue;
            if (rule.Method != "*" && !rule.Method.Equals(method, StringComparison.OrdinalIgnoreCase)) continue;
            try
            {
                if (!Regex.IsMatch(url, rule.UrlPattern, RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(200)))
                    continue;
            }
            catch (RegexMatchTimeoutException) { continue; }
            catch (ArgumentException) { continue; }

            if (!string.IsNullOrEmpty(rule.BodyPattern))
            {
                if (rule.BodyPatternIsRegex)
                {
                    try
                    {
                        if (!Regex.IsMatch(requestBody, rule.BodyPattern, RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(200)))
                            continue;
                    }
                    catch { continue; }
                }
                else
                {
                    if (!requestBody.Contains(rule.BodyPattern, StringComparison.OrdinalIgnoreCase))
                        continue;
                }
            }

            return rule;
        }
        return null;
    }
}
