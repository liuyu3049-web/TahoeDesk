using GlassShell.Core.Discovery;
using Xunit;

namespace GlassShell.Core.Tests.Discovery;

public sealed class IconBoundsNormalizerTests
{
    [Fact]
    public void TrimTransparentPadding_CropsExcessTransparentMarginToUniformSquare()
    {
        // 64x64 canvas with only center 20x20 opaque logo (heavy transparent padding)
        var pixels = new byte[64 * 64 * 4];
        for (int y = 22; y < 42; y++)
            for (int x = 22; x < 42; x++)
            {
                int idx = (y * 64 + x) * 4;
                pixels[idx + 0] = 255;
                pixels[idx + 3] = 255;
            }

        var trimmed = IconBoundsNormalizer.TrimTransparentPadding(new RawIconBitmap(64, 64, pixels));

        // Should be square and tightly bounded (~20-24px instead of 64px)
        Assert.Equal(trimmed.Width, trimmed.Height);
        Assert.InRange(trimmed.Width, 20, 26);
    }

    [Fact]
    public void TrimTransparentPadding_IgnoresFaintOuterDropShadowHalo()
    {
        // 64x64 canvas: faint shadow halo (alpha=40) from 4..60, actual solid icon (alpha=255) only 20..44
        var pixels = new byte[64 * 64 * 4];
        for (int y = 4; y < 60; y++)
            for (int x = 4; x < 60; x++)
                pixels[(y * 64 + x) * 4 + 3] = 40;

        for (int y = 20; y < 44; y++)
            for (int x = 20; x < 44; x++)
            {
                int idx = (y * 64 + x) * 4;
                pixels[idx + 2] = 255;
                pixels[idx + 3] = 255;
            }

        var trimmed = IconBoundsNormalizer.TrimTransparentPadding(new RawIconBitmap(64, 64, pixels));
        Assert.Equal(trimmed.Width, trimmed.Height);
        Assert.InRange(trimmed.Width, 24, 30);
    }

    [Fact]
    public void TrimTransparentPadding_FullyTransparentIcon_ReturnsOriginalSafely()
    {
        var empty = new RawIconBitmap(32, 32, new byte[32 * 32 * 4]);
        var res = IconBoundsNormalizer.TrimTransparentPadding(empty);
        Assert.Equal(32, res.Width);
        Assert.Equal(32, res.Height);
    }
}
