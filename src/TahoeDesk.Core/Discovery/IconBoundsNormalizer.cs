using System;

namespace TahoeDesk.Core.Discovery;

public static class IconBoundsNormalizer
{
    // Solid-core alpha threshold ignores faint outer drop-shadow halos (alpha ~20..80)
    // that otherwise prevent Windows icons from trimming down to uniform visual diameter.
    private const byte SolidAlphaThreshold = 110;
    private const byte FallbackAlphaThreshold = 20;

    public static RawIconBitmap TrimTransparentPadding(RawIconBitmap source)
    {
        if (source.Width <= 4 || source.Height <= 4
            || source.BgraPixels.Length < source.Width * source.Height * 4)
        {
            return source;
        }

        if (!TryFindBounds(source, SolidAlphaThreshold, out int minX, out int minY, out int maxX, out int maxY))
        {
            if (!TryFindBounds(source, FallbackAlphaThreshold, out minX, out minY, out maxX, out maxY))
            {
                return source;
            }
        }

        int w = source.Width;
        int h = source.Height;
        int contentW = maxX - minX + 1;
        int contentH = maxY - minY + 1;
        int maxDim = Math.Max(contentW, contentH);
        int pad = Math.Max(1, (int)Math.Round(maxDim * 0.04));
        int outSize = maxDim + pad * 2;

        if (outSize >= Math.Min(w, h) && Math.Abs(w - h) <= 2)
        {
            return source;
        }

        var dst = new byte[outSize * outSize * 4];
        int offsetX = (outSize - contentW) / 2;
        int offsetY = (outSize - contentH) / 2;

        // Include up to 2px of anti-aliased edge pixels around the solid core so edges stay silky smooth
        int copyMinX = Math.Max(0, minX - 1);
        int copyMinY = Math.Max(0, minY - 1);
        int copyMaxX = Math.Min(w - 1, maxX + 1);
        int copyMaxY = Math.Min(h - 1, maxY + 1);
        int copyW = copyMaxX - copyMinX + 1;
        int copyH = copyMaxY - copyMinY + 1;

        int dstStartX = Math.Max(0, offsetX - (minX - copyMinX));
        int dstStartY = Math.Max(0, offsetY - (minY - copyMinY));
        int clampedW = Math.Min(copyW, outSize - dstStartX);
        int clampedH = Math.Min(copyH, outSize - dstStartY);

        for (int y = 0; y < clampedH; y++)
        {
            int srcRow = ((copyMinY + y) * w + copyMinX) * 4;
            int dstRow = ((dstStartY + y) * outSize + dstStartX) * 4;
            Buffer.BlockCopy(source.BgraPixels, srcRow, dst, dstRow, clampedW * 4);
        }

        return new RawIconBitmap(outSize, outSize, dst);
    }

    private static bool TryFindBounds(
        RawIconBitmap source,
        byte alphaThreshold,
        out int minX, out int minY, out int maxX, out int maxY)
    {
        int w = source.Width;
        int h = source.Height;
        var src = source.BgraPixels;
        minX = w;
        minY = h;
        maxX = -1;
        maxY = -1;

        for (int y = 0; y < h; y++)
        {
            int rowOffset = y * w * 4;
            for (int x = 0; x < w; x++)
            {
                if (src[rowOffset + x * 4 + 3] > alphaThreshold)
                {
                    if (x < minX) minX = x;
                    if (x > maxX) maxX = x;
                    if (y < minY) minY = y;
                    if (y > maxY) maxY = y;
                }
            }
        }

        return maxX >= minX && maxY >= minY;
    }
}
