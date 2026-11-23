using System;
using GlassShell.Core.Config;
using GlassShell.Core.Glass;
using Xunit;

namespace GlassShell.Core.Tests.Glass;

public sealed class DxgiSamplerStateMachineTests
{
    [Fact]
    public void AccessLost_SwitchesToFallbackDegraded_AndRespectsExponentialBackoff()
    {
        var now = new DateTimeOffset(2026, 11, 23, 10, 0, 0, TimeSpan.Zero);
        var sm = new DxgiSamplerStateMachine(() => now);
        sm.MarkAcquired();
        Assert.Equal(SamplerHealth.Ready, sm.Health);

        sm.ReportAccessLost();
        Assert.Equal(SamplerHealth.FallbackDegraded, sm.Health);
        Assert.False(sm.ShouldRetryAcquire());

        now = now.AddMilliseconds(260);
        Assert.True(sm.ShouldRetryAcquire());

        sm.ReportAccessLost(); // 2nd failure -> 500ms backoff
        now = now.AddMilliseconds(300);
        Assert.False(sm.ShouldRetryAcquire());
        now = now.AddMilliseconds(250);
        Assert.True(sm.ShouldRetryAcquire());

        sm.ReportAccessLost(); // 3rd failure -> 1000ms cap
        now = now.AddMilliseconds(900);
        Assert.False(sm.ShouldRetryAcquire());
        now = now.AddMilliseconds(150);
        Assert.True(sm.ShouldRetryAcquire());

        sm.MarkAcquired();
        Assert.Equal(SamplerHealth.Ready, sm.Health);
    }

    [Fact]
    public void FromPreset_ProducesIncreasingTintAlphaFromClearToFrosted()
    {
        var clear = GlassRenderParams.FromPreset(GlassOpacityPreset.Clear, false, 20f);
        var balanced = GlassRenderParams.FromPreset(GlassOpacityPreset.Balanced, false, 20f);
        var frosted = GlassRenderParams.FromPreset(GlassOpacityPreset.Frosted, false, 20f);
        Assert.True(clear.TintAlpha < balanced.TintAlpha && balanced.TintAlpha < frosted.TintAlpha);
        Assert.Equal(20f, clear.CornerRadiusPx);
        Assert.True(clear.RefractionBandPx > 0f);
    }
}
