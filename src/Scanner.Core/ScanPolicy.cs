namespace Scanner.Core;

// Monotonic milliseconds supplied by the caller make boundary behavior deterministic.
public sealed class DuplicateGate
{
    private readonly Dictionary<string, long> _lastSent = new(StringComparer.Ordinal);
    public bool CanSend(string payload, long nowMs) => !_lastSent.TryGetValue(payload, out var last) || nowMs - last >= 500;
    public void MarkSent(string payload, long nowMs)
    {
        foreach (var expired in _lastSent.Where(p => nowMs - p.Value >= 500).Select(p => p.Key).ToArray()) _lastSent.Remove(expired);
        _lastSent[payload] = nowMs;
    }
    public void Clear() => _lastSent.Clear();
}

public sealed class CameraDemand
{
    private long? _idleSince;
    public bool ShouldConnect(int clients, bool alwaysOn, bool connected, long nowMs)
    {
        if (clients > 0 || alwaysOn) { _idleSince = null; return true; }
        if (!connected) { _idleSince = null; return false; }
        _idleSince ??= nowMs;
        return nowMs - _idleSince.Value < 60_000;
    }
    public int IdleSecondsRemaining(long nowMs) => _idleSince is { } start ? Math.Max(0, (int)Math.Ceiling((60_000 - (nowMs - start)) / 1000.0)) : 60;
}
