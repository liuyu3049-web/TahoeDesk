using GlassShell.Core.Glass;
using Xunit;

namespace GlassShell.Core.Tests.Glass;

public sealed class LuminanceClassifierTests
{
    [Fact]
    public void BrightSkyWallpaper_ReturnsDarkInkTrue()
    {
        // Bright light-blue sky similar to user's wallpaper
        Assert.True(LuminanceClassifier.ShouldUseDarkInk(185, 208, 212));
        Assert.True(LuminanceClassifier.ShouldUseDarkInk(245, 245, 245));
    }

    [Fact]
    public void DarkNightWallpaper_ReturnsDarkInkFalse()
    {
        Assert.False(LuminanceClassifier.ShouldUseDarkInk(24, 30, 48));
        Assert.False(LuminanceClassifier.ShouldUseDarkInk(80, 95, 110));
    }
}
