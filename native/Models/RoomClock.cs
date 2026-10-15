using System.Diagnostics;

namespace SyncCue.Models;

/// <summary>Monotonic clock sync against room-server (same algorithm as shared/clock.js).</summary>
public sealed class RoomClock
{
    private readonly Func<double> _now;
    private readonly List<(double Rtt, double Offset)> _samples = new();

    public RoomClock(Func<double>? now = null)
    {
        _now = now ?? (() => Stopwatch.GetTimestamp() * 1000.0 / Stopwatch.Frequency);
    }

    public double? Offset { get; private set; }
    public double? Rtt { get; private set; }

    public void Reset()
    {
        _samples.Clear();
        Offset = null;
        Rtt = null;
    }

    public bool Sample(double sentMs, double receivedMs, double serverMs)
    {
        if (!double.IsFinite(sentMs) || !double.IsFinite(receivedMs) || !double.IsFinite(serverMs) || receivedMs < sentMs)
            return false;
        var rtt = receivedMs - sentMs;
        if (rtt > 2000) return false;
        _samples.Add((rtt, serverMs - (sentMs + receivedMs) / 2.0));
        if (_samples.Count > 20) _samples.RemoveRange(0, _samples.Count - 20);
        var best = _samples[0];
        foreach (var s in _samples)
            if (s.Rtt <= best.Rtt) best = s;
        Offset = best.Offset;
        Rtt = best.Rtt;
        return true;
    }

    public double NowMs() => _now();

    public double? ServerNow()
    {
        if (Offset is null) return null;
        return _now() + Offset.Value;
    }

    public double? Remaining(double targetServerMs, double leadMs = 0)
    {
        var now = ServerNow();
        return now is null ? null : targetServerMs - now.Value - leadMs;
    }
}
