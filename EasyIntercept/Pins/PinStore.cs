using System.Collections.Concurrent;

namespace EasyIntercept.Pins;

public class PinnedResponse
{
    public int StatusCode { get; init; }
    public Dictionary<string, string> Headers { get; init; } = new();
    public string Body { get; init; } = "";
}

public class PinStore
{
    private readonly ConcurrentDictionary<string, PinnedResponse> _pins = new();

    public void Pin(string url, PinnedResponse response) =>
        _pins[url] = response;

    public void Unpin(string url) =>
        _pins.TryRemove(url, out _);

    public bool TryGet(string url, out PinnedResponse? response) =>
        _pins.TryGetValue(url, out response);

    public IReadOnlyDictionary<string, PinnedResponse> GetAll() => _pins;
}
