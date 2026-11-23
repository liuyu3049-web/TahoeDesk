using System;

namespace GlassShell.Core.Discovery;

public static class IconBoundsNormalizer
{
    private const byte AlphaThreshold = 16;

    /// <summary>
    /// Crops excess transparent padding around a Windows icon bitmap and re-centers the visible
    /// silhouette into a uniform square with a tiny 4% breathing margin so all Dock logos share
    /// consistent visual diameter.
    /// </summary>
    public static RawIconBitmap TrimTransparentPadding(RawIconBitmap source)
    {
        if (source.Width <= 4 || source.Height <= 4
            || source.BgraPixels.Length < source.Width * source.Height * 4)
        {
            return source;
        }

        int w = source.Width;
        int h = source.Height;
        var src = source.BgraPixels;

        int minX = w, minY = h, maxX = -1, maxY = -1;
        for (int y = 0; y < h; y++)
        {
            int rowOffset = y * w * 4;
            for (int x = 0; x < w; x++)
            {
                if (src[rowOffset + x * 4 + 3] > AlphaThreshold)
                {
                    if (x < minX) minX = x;
                    if (x > maxX) maxX = x;
                    if (y < minY) minY = y;
                    if (y > maxY) maxY = y;
                }
            }
        }

        if (maxX < minX || maxY < minY)
        {
            return source;
        }

        int contentW = maxX - minX + 1;
        int contentH = maxY - minY + 1;
        int maxDim = Math.Max(contentW, contentH);
        int pad = Math.Max(1, (int)Math.Round(maxDim * 0.04));
        int outSize = maxDim + pad * 2;

        // If already tight enough (>=90% filled), return as-is
        if (outSize >= Math.Min(w, h) && Math.Abs(w - h) <= 2)
        {
            return source;
        }

        var dst = new byte[outSize * outSize * 4];
        int offsetX = (outSize - contentW) / 2;
        int offsetY = (outSize - contentH) / 2;

        for (int y = 0; y < contentH; y++)
        {
            int srcRow = ((minY + y) * w + minX) * 4;
            int dstRow = ((offsetY + y) * outSize + offsetX) * 4;
            Buffer.BlockCopy(src, srcRow, dst, dstRow, contentW * 4);
        }

        return new RawIconBitmap(outSize, outSize, dst);
    }
}
