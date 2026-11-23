using System;
using System.Collections.Concurrent;

namespace GlassShell.Core.DockPhysics;

public sealed class SpringBounceAnimator
{
    private const double HopDurationSeconds = 0.42;
    private const double BaseHopHeightDip = 22.0;

    private readonly record struct BounceState(double StartSeconds, int Hops);
    private readonly ConcurrentDictionary<string, BounceState> _active = new(StringComparer.OrdinalIgnoreCase);

    public bool HasActiveAnimations => !_active.IsEmpty;

    public void TriggerBounce(string itemId, double nowSeconds, int hops = 2)
    {
        if (string.IsNullOrWhiteSpace(itemId)) return;
        _active[itemId] = new BounceState(nowSeconds, Math.Max(1, hops));
    }

    public double SampleOffsetY(string itemId, double nowSeconds)
    {
        if (!_active.TryGetValue(itemId, out var state)) return 0.0;

        double elapsed = nowSeconds - state.StartSeconds;
        double totalDuration = state.Hops * HopDurationSeconds;
        if (elapsed < 0 || elapsed >= totalDuration)
        {
            _active.TryRemove(itemId, out _);
            return 0.0;
        }

        int hopIndex = (int)(elapsed / HopDurationSeconds);
        double localT = (elapsed - hopIndex * HopDurationSeconds) / HopDurationSeconds;
        double damping = Math.Pow(0.72, hopIndex);
        return Math.Sin(localT * Math.PI) * BaseHopHeightDip * damping;
    }
}
