using GlassShell.Core.Config;

namespace GlassShell.Core.DockPhysics;

public sealed class DockAutoHideController
{
    public const double DefaultTriggerDelayMs = 450.0;

    public bool ShouldHide(
        DockAutoHideMode mode,
        bool hasBottomOverlappingWindow,
        bool isPointerAtBottomEdge,
        double bottomEdgeDwellMs,
        bool isMenuOrDragActive)
    {
        if (isMenuOrDragActive || mode == DockAutoHideMode.Off)
        {
            return false;
        }

        bool wantsHidden = mode == DockAutoHideMode.Always || (mode == DockAutoHideMode.Smart && hasBottomOverlappingWindow);
        if (!wantsHidden)
        {
            return false;
        }

        if (isPointerAtBottomEdge && bottomEdgeDwellMs >= DefaultTriggerDelayMs)
        {
            return false;
        }

        return true;
    }
}
