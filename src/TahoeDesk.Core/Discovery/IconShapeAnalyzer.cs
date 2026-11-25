using System;

namespace TahoeDesk.Core.Discovery;

public static class IconShapeAnalyzer
{
    /// <summary>
    /// Detects whether an extracted Windows icon is a transparent-corner silhouette
    /// (needing a macOS Squircle tile backing) vs. an already full-bleed square icon.
    /// </summary>
    public static bool HasTransparentCorners(RawIconBitmap bitmap)
    {
        if (bitmap.Width < 4 || bitmap.Height < 4 || bitmap.BgraPixels.Length < bitmap.Width * bitmap.Height * 4)
        {
            return true;
        }

        int w = bitmap.Width;
        int h = bitmap.Height;
        int insetX = Math.Max(1, w / 16);
        int insetY = Math.Max(1, h / 16);

        int transparentCorners = 0;
        (int X, int Y)[] samplePoints =
        [
            (insetX, insetY),
            (w - 1 - insetX, insetY),
            (insetX, h - 1 - insetY),
            (w - 1 - insetX, h - 1 - insetY)
        ];

        foreach (var (x, y) in samplePoints)
        {
            int alphaIndex = (y * w + x) * 4 + 3;
            if (bitmap.BgraPixels[alphaIndex] < 180)
            {
                transparentCorners++;
            }
        }

        return transparentCorners >= 3;
    }
}
