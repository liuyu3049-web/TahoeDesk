using TahoeDesk.Core.DockPhysics;
using Xunit;

namespace TahoeDesk.Core.Tests.DockPhysics;

public sealed class DockHoverAnimatorTests
{
    [Fact]
    public void EnterAndLeave_SmoothlyRampsHoverAmountWithoutInstantJump()
    {
        var anim = new DockHoverAnimator();
        Assert.Equal(0.0, anim.HoverAmount, precision: 3);

        // First frame after pointer enters must ramp smoothly, NOT jump immediately to 1.0
        double firstFrame = anim.Step(isPointerInside: true, pointerX: 120.0, dtSeconds: 0.016);
        Assert.True(firstFrame > 0.01 && firstFrame < 0.5, $"Expected smooth ramp on frame 1, got {firstFrame}");

        // After ~30 frames (~0.5s), settles near 1.0
        for (int i = 0; i < 35; i++)
        {
            anim.Step(isPointerInside: true, pointerX: 120.0, dtSeconds: 0.016);
        }
        Assert.True(anim.HoverAmount > 0.95);

        // First frame after pointer leaves must shrink smoothly, NOT snap to 0.0
        double leaveFrame1 = anim.Step(isPointerInside: false, pointerX: null, dtSeconds: 0.016);
        Assert.True(leaveFrame1 > 0.5 && leaveFrame1 < 0.99);
        Assert.NotNull(anim.EffectivePointerX);

        // After settling, reaches 0.0 and clears effective pointer
        for (int i = 0; i < 60; i++)
        {
            anim.Step(isPointerInside: false, pointerX: null, dtSeconds: 0.016);
        }
        Assert.Equal(0.0, anim.HoverAmount, precision: 3);
        Assert.Null(anim.EffectivePointerX);
    }
}
