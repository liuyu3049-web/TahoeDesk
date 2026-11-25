using System;
using System.Collections.Generic;

namespace TahoeDesk.Core.DockPhysics;

public readonly record struct DockIconLayout(double CenterX, double RenderedSize, double LiftY);

public sealed record DockCenteredLayout(
    IReadOnlyList<DockIconLayout> Icons,
    IReadOnlyList<double> DividerCentersX,
    double TotalWidth);

public static class DockMagnificationCalculator
{
    public static IReadOnlyList<DockIconLayout> Compute(
        int itemCount,
        double baseSize,
        double gap,
        double? pointerX,
        bool magnificationEnabled,
        double maxScale,
        double sigmaFactor = 1.45)
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

    /// <summary>
    /// Computes Dock icon & divider positions in a FIXED center-relative coordinate space (0.0 = Dock center).
    /// Because pointerRelCenter is measured in the unshifted screen/container center coordinate space and
    /// compared against immutable rest centers (including divider widths), magnification is strictly symmetric
    /// (small-medium-large-medium-small) and the hovered icon stays centered directly under the pointer.
    /// </summary>
    public static IReadOnlyList<DockIconLayout> ComputeCentered(
        int itemCount,
        double baseSize,
        double gap,
        IReadOnlyList<int> dividerAfterIndices,
        double dividerWidth,
        double? pointerRelCenter,
        bool magnificationEnabled,
        double maxScale,
        double sigmaFactor = 1.35)
    {
        return ComputeCenteredFull(
            itemCount, baseSize, gap, dividerAfterIndices, dividerWidth,
            pointerRelCenter, magnificationEnabled, maxScale, sigmaFactor).Icons;
    }

    public static DockCenteredLayout ComputeCenteredFull(
        int itemCount,
        double baseSize,
        double gap,
        IReadOnlyList<int> dividerAfterIndices,
        double dividerWidth,
        double? pointerRelCenter,
        bool magnificationEnabled,
        double maxScale,
        double sigmaFactor = 1.35)
    {
        if (itemCount <= 0)
        {
            return new DockCenteredLayout([], [], 0.0);
        }

        var dividerSet = new HashSet<int>(dividerAfterIndices);

        // 1. Compute immutable rest centers (including divider slots), centered around 0.0
        var restCenters = new double[itemCount];
        double restCursor = 0.0;
        for (int i = 0; i < itemCount; i++)
        {
            restCenters[i] = restCursor + baseSize * 0.5;
            restCursor += baseSize;
            if (i < itemCount - 1)
            {
                restCursor += gap;
                if (dividerSet.Contains(i))
                {
                    restCursor += dividerWidth;
                }
            }
        }
        double restHalf = restCursor * 0.5;
        for (int i = 0; i < itemCount; i++)
        {
            restCenters[i] -= restHalf;
        }

        // 2. Compute symmetric Gaussian magnified sizes from fixed center-relative pointer
        var sizes = new double[itemCount];
        double sigma = Math.Max(baseSize * sigmaFactor, 1.0);
        for (int i = 0; i < itemCount; i++)
        {
            if (!magnificationEnabled || pointerRelCenter is null || maxScale <= 1.0001)
            {
                sizes[i] = baseSize;
            }
            else
            {
                double dx = restCenters[i] - pointerRelCenter.Value;
                double gaussian = Math.Exp(-(dx * dx) / (2.0 * sigma * sigma));
                double scale = 1.0 + (maxScale - 1.0) * gaussian;
                sizes[i] = baseSize * scale;
            }
        }

        // 3. Lay out magnified icons + dividers sequentially, then center around 0.0
        var rawCenters = new double[itemCount];
        var rawDividers = new List<double>(dividerAfterIndices.Count);
        double cursor = 0.0;
        for (int i = 0; i < itemCount; i++)
        {
            double sz = sizes[i];
            rawCenters[i] = cursor + sz * 0.5;
            cursor += sz;
            if (i < itemCount - 1)
            {
                if (dividerSet.Contains(i))
                {
                    rawDividers.Add(cursor + (gap + dividerWidth) * 0.5);
                    cursor += gap + dividerWidth;
                }
                else
                {
                    cursor += gap;
                }
            }
        }

        double halfTotal = cursor * 0.5;
        var icons = new DockIconLayout[itemCount];
        for (int i = 0; i < itemCount; i++)
        {
            double sz = sizes[i];
            icons[i] = new DockIconLayout(rawCenters[i] - halfTotal, sz, (sz - baseSize) * 0.5);
        }

        for (int d = 0; d < rawDividers.Count; d++)
        {
            rawDividers[d] -= halfTotal;
        }

        return new DockCenteredLayout(icons, rawDividers, cursor);
    }

    public static double ComputeTotalWidth(IReadOnlyList<DockIconLayout> layouts, double gap)
    {
        if (layouts.Count == 0) return 0;
        var last = layouts[^1];
        return last.CenterX + last.RenderedSize * 0.5;
    }
}
