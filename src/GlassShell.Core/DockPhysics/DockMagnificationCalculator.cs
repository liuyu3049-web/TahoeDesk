using System;
using System.Collections.Generic;

namespace GlassShell.Core.DockPhysics;

public readonly record struct DockIconLayout(double CenterX, double RenderedSize, double LiftY);

public static class DockMagnificationCalculator
{
    public static IReadOnlyList<DockIconLayout> Compute(
        int itemCount,
        double baseSize,
        double gap,
        double? pointerX,
        bool magnificationEnabled,
        double maxScale,
        double sigmaFactor = 1.65)
    {
        if (itemCount <= 0) return [];

        double step = baseSize + gap;
        var sizes = new double[itemCount];
        double sigma = Math.Max(baseSize * sigmaFactor, 1.0);

        for (int i = 0; i < itemCount; i++)
        {
            double restCenterX = i * step + baseSize * 0.5;
            if (!magnificationEnabled || pointerX is null || maxScale <= 1.0)
            {
                sizes[i] = baseSize;
            }
            else
            {
                double dx = restCenterX - pointerX.Value;
                double gaussian = Math.Exp(-(dx * dx) / (2.0 * sigma * sigma));
                double scale = 1.0 + (maxScale - 1.0) * gaussian;
                sizes[i] = baseSize * scale;
            }
        }

        // Lay out sequentially without overlap so magnified icons push neighbors gracefully
        var result = new DockIconLayout[itemCount];
        double cursor = 0.0;
        for (int i = 0; i < itemCount; i++)
        {
            double sz = sizes[i];
            double centerX = cursor + sz * 0.5;
            double liftY = (sz - baseSize) * 0.5;
            result[i] = new DockIconLayout(centerX, sz, liftY);
            cursor += sz + gap;
        }

        return result;
    }

    public static double ComputeTotalWidth(IReadOnlyList<DockIconLayout> layouts, double gap)
    {
        if (layouts.Count == 0) return 0;
        var last = layouts[^1];
        return last.CenterX + last.RenderedSize * 0.5;
    }
}
