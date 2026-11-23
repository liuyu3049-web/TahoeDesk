using System;

namespace GlassShell.Core.DockPhysics;

public sealed class DockHoverAnimator
{
    private const double EnterRate = 14.0;
    private const double LeaveRate = 11.0;
    private const double PointerFollowRate = 24.0;

    private double _hoverAmount;
    private double? _lastPointerX;
    private double _smoothedPointerX;

    public double HoverAmount => _hoverAmount;
    public double? EffectivePointerX => _hoverAmount > 0.003 ? _smoothedPointerX : null;

    public double Step(bool isPointerInside, double? pointerX, double dtSeconds)
    {
        double dt = Math.Clamp(dtSeconds, 0.001, 0.05);

        if (isPointerInside && pointerX.HasValue)
        {
            if (_lastPointerX is null)
            {
                _smoothedPointerX = pointerX.Value;
            }
            else
            {
                double followAlpha = 1.0 - Math.Exp(-PointerFollowRate * dt);
                _smoothedPointerX += (pointerX.Value - _smoothedPointerX) * followAlpha;
            }

            _lastPointerX = pointerX.Value;
            double enterAlpha = 1.0 - Math.Exp(-EnterRate * dt);
            _hoverAmount += (1.0 - _hoverAmount) * enterAlpha;
            if (_hoverAmount > 0.997) _hoverAmount = 1.0;
        }
        else
        {
            double leaveAlpha = 1.0 - Math.Exp(-LeaveRate * dt);
            _hoverAmount += (0.0 - _hoverAmount) * leaveAlpha;
            if (_hoverAmount <= 0.003)
            {
                _hoverAmount = 0.0;
                _lastPointerX = null;
            }
        }

        return _hoverAmount;
    }
}
