using TahoeDesk.Core.SystemControl;
using Xunit;

namespace TahoeDesk.Core.Tests.SystemControl;

public sealed class SystemControlTests
{
    [Theory]
    [InlineData(-15, 0)]
    [InlineData(0, 0)]
    [InlineData(42, 42)]
    [InlineData(120, 100)]
    public void ClampPercent_EnforcesZeroToHundredBounds(int input, int expected)
    {
        Assert.Equal(expected, SystemControlHelpers.ClampPercent(input));
    }

    [Fact]
    public void ScalarPercentConversion_RoundTripsAccurately()
    {
        Assert.Equal(0.5f, SystemControlHelpers.PercentToScalar(50), 3);
        Assert.Equal(75, SystemControlHelpers.ScalarToPercent(0.75f));
        Assert.Equal(100, SystemControlHelpers.ScalarToPercent(1.5f));
    }

    [Fact]
    public void MediaSessionInfo_FormatsFallbackSubtitleCleanly()
    {
        Assert.Equal("未在播放", MediaSessionInfo.Empty.FormattedSubtitle);
        var noArtist = new MediaSessionInfo(true, "Track 1", "", true, null);
        Assert.Equal("正在播放", noArtist.FormattedSubtitle);
        var withArtist = new MediaSessionInfo(true, "晴天", "周杰伦", true, null);
        Assert.Equal("周杰伦", withArtist.FormattedSubtitle);
    }

    [Fact]
    public void UnavailableStates_HaveSafeDefaults()
    {
        Assert.False(AudioState.Unavailable.IsAvailable);
        Assert.Empty(AudioState.Unavailable.Devices);
        Assert.False(RadioState.Unavailable.HasWifi);
        Assert.False(RadioState.Unavailable.HasBluetooth);
    }
}
