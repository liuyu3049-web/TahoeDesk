using System;
using System.Collections.Generic;

namespace GlassShell.Core.DockPhysics;

public readonly record struct GenieSliceRect(double Left, double Top, double Width, double Height);

/// <summary>
/// Computes the authentic macOS Genie Effect curved S-funnel horizontal slices for minimizing a window
/// into a specific Dock icon (progress 0 -> 1) and restoring it out of that Dock icon (progress 1 -> 0).
/// </summary>
public static class GenieMeshCalculator
{
    public static IReadOnlyList<GenieSliceRect> ComputeSlices(
        double winLeft,
        double winTop,
        double winWidth,
        double winHeight,
        double dockCenterX,
        double dockCenterY,
        double progress,
        int sliceCount = 28,
        double dockMouthWidth = 34.0)
    {
        int n = Math.Max(4, sliceCount);
        double t = Math.Clamp(progress, 0.0, 1.0);
        double winRight = winLeft + Math.Max(1.0, winWidth);
        double winBottom = winTop + Math.Max(1.0, winHeight);
        double totalDrop = Math.Max(20.0, dockCenterY - winTop);

        // Phase 1: Curved S-funnel pinches the lower edges toward the Dock icon mouth
        double pinchPhase = SmoothStep(Math.Clamp(t / 0.46, 0.0, 1.0));
        // Phase 2: Bottom edge reaches Dock mouth fast, then top edge glides down the curved rails
        double bottomArrival = SmoothStep(Math.Clamp(t / 0.36, 0.0, 1.0));
        double topSlide = Math.Pow(Math.Clamp((t - 0.16) / 0.84, 0.0, 1.0), 1.65);

        double curTop = Lerp(winTop, dockCenterY - 2.0, topSlide);
        double curBottom = Lerp(winBottom, dockCenterY, bottomArrival);
        double curSpan = Math.Max(1.0, curBottom - curTop);

        double mouthLeft = dockCenterX - dockMouthWidth * 0.5;
        double mouthRight = dockCenterX + dockMouthWidth * 0.5;

        var result = new List<GenieSliceRect>(n);
        for (int i = 0; i < n; i++)
        {
            double v0 = (double)i / n;
            double v1 = (double)(i + 1) / n;

            double y0 = curTop + v0 * curSpan;
            double y1 = curTop + v1 * curSpan;
            double yMid = (y0 + y1) * 0.5;

            // Global S-curve rail position between window top and Dock mouth
            double railU = Math.Clamp((yMid - winTop) / totalDrop, 0.0, 1.0);
            double curve = SmoothStep(railU) * pinchPhase;
            // As top nears Dock mouth, collapse remaining width smoothly into the icon
            double finalCollapse = Math.Clamp((t - 0.72) / 0.28, 0.0, 1.0);
            double edgeT = Math.Clamp(curve + (1.0 - curve) * finalCollapse * finalCollapse, 0.0, 1.0);

            double left = Lerp(winLeft, mouthLeft, edgeT);
            double right = Lerp(winRight, mouthRight, edgeT);
            double width = Math.Max(1.0, right - left);
            double height = Math.Max(0.8, y1 - y0 + 0.6); // +0.6px overlap eliminates sub-pixel seams!

            result.Add(new GenieSliceRect(left, y0, width, height));
        }

        return result;
    }

    private static double SmoothStep(double x) => x * x * (3.0 - 2.0 * x);
    private static double Lerp(double a, double b, double t) => a + (b - a) * t;
}
