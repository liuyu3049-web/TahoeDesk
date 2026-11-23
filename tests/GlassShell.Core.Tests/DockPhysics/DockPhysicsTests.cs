using GlassShell.Core.Config;
using GlassShell.Core.DockPhysics;
using Xunit;

namespace GlassShell.Core.Tests.DockPhysics;

public sealed class DockPhysicsTests
{
    [Fact]
    public void Magnification_PeaksAtPointerAndDecaysSymmetrically()
    {
        var baseLayout = DockMagnificationCalculator.Compute(5, baseSize: 52, gap: 8, pointerX: null, magnificationEnabled: true, maxScale: 1.6);
        double centerOfMiddle = baseLayout[2].CenterX;

        var hovered = DockMagnificationCalculator.Compute(5, baseSize: 52, gap: 8, pointerX: centerOfMiddle, magnificationEnabled: true, maxScale: 1.6);

        Assert.Equal(52 * 1.6, hovered[2].RenderedSize, precision: 2);
        Assert.Equal(hovered[1].RenderedSize, hovered[3].RenderedSize, precision: 2);
        Assert.True(hovered[2].RenderedSize > hovered[1].RenderedSize);
        Assert.True(hovered[1].RenderedSize > hovered[0].RenderedSize);
        Assert.True(hovered[2].LiftY > hovered[1].LiftY);
    }

    [Fact]
    public void Magnification_WhenDisabled_ReturnsUniformBaseSizes()
    {
        var layout = DockMagnificationCalculator.Compute(4, baseSize: 52, gap: 8, pointerX: 90, magnificationEnabled: false, maxScale: 1.6);
        Assert.All(layout, item =>
        {
            Assert.Equal(52, item.RenderedSize, precision: 2);
            Assert.Equal(0, item.LiftY, precision: 2);
        });
    }

    [Fact]
    public void SpringBounce_LiftsAboveZeroAndSettlesBackToZero()
    {
        var anim = new SpringBounceAnimator();
        anim.TriggerBounce("app1", nowSeconds: 0.0, hops: 1);
        double mid = anim.SampleOffsetY("app1", 0.15);
        double settled = anim.SampleOffsetY("app1", 2.0);
        Assert.True(mid > 5.0);
        Assert.Equal(0.0, settled, precision: 3);
        Assert.False(anim.HasActiveAnimations);
    }

    [Fact]
    public void AutoHide_SmartModeHidesOnOverlap_RevealsAfter450MsBottomDwell()
    {
        var ctrl = new DockAutoHideController();
        Assert.True(ctrl.ShouldHide(DockAutoHideMode.Smart, hasBottomOverlappingWindow: true, isPointerAtBottomEdge: false, bottomEdgeDwellMs: 0, isMenuOrDragActive: false));
        Assert.True(ctrl.ShouldHide(DockAutoHideMode.Smart, hasBottomOverlappingWindow: true, isPointerAtBottomEdge: true, bottomEdgeDwellMs: 200, isMenuOrDragActive: false));
        Assert.False(ctrl.ShouldHide(DockAutoHideMode.Smart, hasBottomOverlappingWindow: true, isPointerAtBottomEdge: true, bottomEdgeDwellMs: 500, isMenuOrDragActive: false));
        Assert.False(ctrl.ShouldHide(DockAutoHideMode.Smart, hasBottomOverlappingWindow: false, isPointerAtBottomEdge: false, bottomEdgeDwellMs: 0, isMenuOrDragActive: false));
        Assert.False(ctrl.ShouldHide(DockAutoHideMode.Always, hasBottomOverlappingWindow: false, isPointerAtBottomEdge: false, bottomEdgeDwellMs: 0, isMenuOrDragActive: true));
    }
}
