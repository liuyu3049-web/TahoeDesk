using TahoeDesk.Core.Discovery;
using Xunit;

namespace TahoeDesk.Core.Tests.Discovery;

public sealed class IconShapeAnalyzerTests
{
    [Fact]
    public void SilhouetteWithTransparentCorners_ReturnsTrue()
    {
        var pixels = new byte[16 * 16 * 4];
        // Fill center 8x8 opaque, leave corners transparent (alpha=0)
        for (int y = 4; y < 12; y++)
            for (int x = 4; x < 12; x++)
                pixels[(y * 16 + x) * 4 + 3] = 255;

        Assert.True(IconShapeAnalyzer.HasTransparentCorners(new RawIconBitmap(16, 16, pixels)));
    }

    [Fact]
    public void FullBleedSquareIcon_ReturnsFalse()
    {
        var pixels = new byte[16 * 16 * 4];
        for (int i = 3; i < pixels.Length; i += 4) pixels[i] = 255;

        Assert.False(IconShapeAnalyzer.HasTransparentCorners(new RawIconBitmap(16, 16, pixels)));
    }
}
