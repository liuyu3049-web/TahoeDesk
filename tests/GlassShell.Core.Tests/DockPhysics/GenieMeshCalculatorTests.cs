using GlassShell.Core.DockPhysics;
using Xunit;

namespace GlassShell.Core.Tests.DockPhysics;

public sealed class GenieMeshCalculatorTests
{
    [Fact]
    public void ComputeSlices_StartsFullWindowCurvesInMiddleAndConvergesIntoDockMouth()
    {
        // Progress 0: exact full window
        var start = GenieMeshCalculator.ComputeSlices(200, 100, 800, 600, dockCenterX: 600, dockCenterY: 1000, progress: 0.0, sliceCount: 20);
        Assert.Equal(20, start.Count);
        Assert.Equal(800.0, start[0].Width, 1);
        Assert.Equal(800.0, start[^1].Width, 1);

        // Progress 0.45: unmistakable Genie funnel — top slice stays significantly wider than bottom slice!
        var mid = GenieMeshCalculator.ComputeSlices(200, 100, 800, 600, dockCenterX: 600, dockCenterY: 1000, progress: 0.45, sliceCount: 20);
        Assert.True(mid[0].Width > mid[^1].Width * 2.0, $"Expected Genie funnel top ({mid[0].Width}) >> bottom ({mid[^1].Width})");

        // Progress 1.0: all slices converged cleanly inside Dock icon mouth
        var end = GenieMeshCalculator.ComputeSlices(200, 100, 800, 600, dockCenterX: 600, dockCenterY: 1000, progress: 1.0, sliceCount: 20);
        Assert.True(end[0].Width <= 36.0);
        Assert.True(end[0].Top >= 995.0);
    }
}
