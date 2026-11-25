using System;

namespace TahoeDesk.Core.Glass;

public sealed class DxgiSamplerStateMachine
{
    private static readonly int[] BackoffMs = [250, 500, 1000];
    private readonly Func<DateTimeOffset> _clock;
    private readonly object _gate = new();

    private int _consecutiveFailures;
    private DateTimeOffset _nextRetryAt = DateTimeOffset.MinValue;

    public DxgiSamplerStateMachine(Func<DateTimeOffset>? clock = null)
    {
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    public SamplerHealth Health { get; private set; } = SamplerHealth.FallbackDegraded;

    public void MarkAcquired()
    {
        lock (_gate)
        {
            _consecutiveFailures = 0;
            _nextRetryAt = DateTimeOffset.MinValue;
            Health = SamplerHealth.Ready;
        }
    }

    public void ReportAccessLost()
    {
        lock (_gate)
        {
            int idx = Math.Min(_consecutiveFailures, BackoffMs.Length - 1);
            int delayMs = BackoffMs[idx];
            _consecutiveFailures++;
            _nextRetryAt = _clock().AddMilliseconds(delayMs);
            Health = SamplerHealth.FallbackDegraded;
        }
    }

    public bool ShouldRetryAcquire()
    {
        lock (_gate)
        {
            if (Health == SamplerHealth.Ready) return false;
            return _clock() >= _nextRetryAt;
        }
    }
}
