namespace EasyIntercept.Proxy;

/// <summary>
/// Decides when a streaming session may push an interim update to the UI: the first time
/// immediately, afterwards at most once per interval or once per N bytes, whichever comes first.
/// </summary>
public sealed class InterimThrottle
{
    public const int DefaultIntervalMs = 200;
    public const int DefaultBytes = 32 * 1024;

    private readonly int _intervalMs;
    private readonly int _bytes;
    private long _lastPushMs = long.MinValue;
    private long _bytesSincePush;

    public InterimThrottle(int intervalMs = DefaultIntervalMs, int bytes = DefaultBytes)
    {
        _intervalMs = intervalMs;
        _bytes = bytes;
    }

    public void Count(long bytes) => _bytesSincePush += bytes;

    public bool ShouldPush(long nowMs)
    {
        if (_lastPushMs != long.MinValue && nowMs - _lastPushMs < _intervalMs && _bytesSincePush < _bytes)
            return false;
        _lastPushMs = nowMs;
        _bytesSincePush = 0;
        return true;
    }
}
