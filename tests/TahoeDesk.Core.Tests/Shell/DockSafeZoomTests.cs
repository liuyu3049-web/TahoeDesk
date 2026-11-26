using TahoeDesk.Core.Discovery;
using TahoeDesk.Core.Shell;
using Xunit;

namespace TahoeDesk.Core.Tests.Shell;

public sealed class DockSafeZoomTests
{
    [Fact]
    public void ComputeDockSafeVisibleRect_PreservesWindowWidthAndExpandsVerticallyBetweenMenuBarAndDock()
    {
        var mon = new MonitorContext(
            MonitorHandle: new nint(1),
            Bounds: new ScreenRect(0, 0, 2560, 1440),
            DpiScale: 1.5,
            IsPrimary: true,
            WorkArea: new ScreenRect(0, 42, 2560, 1440));

        var currentVisible = new ScreenRect(320, 160, 1820, 960);
        var safe = DockSafeZoomManager.ComputeDockSafeVisibleRect(mon, topBarPx: 42, dockReservePx: 114, currentVisible);

        Assert.Equal(320, safe.Left);
        Assert.Equal(1820, safe.Right);
        Assert.Equal(42, safe.Top);
        Assert.Equal(1326, safe.Bottom);
        Assert.True(DockSafeZoomManager.IsVerticallyZoomed(new ScreenRect(400, 44, 1900, 1324), safe));
    }

    [Fact]
    public void ComputeDockSafeVisibleRect_ProtectsMenuBarEvenWhenWorkAreaTopWasResetToZero()
    {
        var mon = new MonitorContext(
            MonitorHandle: new nint(1),
            Bounds: new ScreenRect(0, 0, 1920, 1080),
            DpiScale: 1.0,
            IsPrimary: true,
            WorkArea: new ScreenRect(0, 0, 1920, 1080));

        var safe = DockSafeZoomManager.ComputeDockSafeVisibleRect(mon, topBarPx: 28, dockReservePx: 92);

        Assert.Equal(28, safe.Top);
        Assert.Equal(988, safe.Bottom);
    }

    [Fact]
    public void ComputeOuterBoundsForVisibleTarget_CompensatesDwmShadowFrameInsets()
    {
        var targetVisible = new ScreenRect(0, 28, 1920, 988);
        var currentWindow = new ScreenRect(100, 100, 1114, 707);
        var currentExtended = new ScreenRect(107, 100, 1107, 700); // 7px invisible L/R/B shadow borders

        var outer = DockSafeZoomManager.ComputeOuterBoundsForVisibleTarget(targetVisible, currentWindow, currentExtended);

        Assert.Equal(-7, outer.Left);
        Assert.Equal(28, outer.Top);
        Assert.Equal(1927, outer.Right);
        Assert.Equal(995, outer.Bottom);
    }

    [Fact]
    public void IsApproximatelyRect_MatchesWithinToleranceAndRejectsDraggedWindow()
    {
        var target = new ScreenRect(0, 28, 1920, 988);
        Assert.True(DockSafeZoomManager.IsApproximatelyRect(new ScreenRect(2, 30, 1918, 986), target, tolerancePx: 16));
        Assert.False(DockSafeZoomManager.IsApproximatelyRect(new ScreenRect(120, 80, 1400, 800), target, tolerancePx: 16));
    }

    [Fact]
    public void WindowTracker_IdentifiesKnownScreenshotOverlays()
    {
        Assert.True(WindowTracker.IsKnownCaptureOverlay("Windows.UI.Core.CoreWindow", "ScreenClippingHost"));
        Assert.True(WindowTracker.IsKnownCaptureOverlay("WeChatScreenshotWnd", "Weixin"));
        Assert.True(WindowTracker.IsKnownCaptureOverlay("Qt680QWindowIcon", "Weixin", isTopmost: true));
        Assert.False(WindowTracker.IsKnownCaptureOverlay("Qt680QWindowIcon", "Weixin", isTopmost: false));
        Assert.True(WindowTracker.IsKnownCaptureOverlay("Qt5QWindow", "Snipaste"));
        Assert.True(WindowTracker.IsKnownCaptureOverlay("Qt6QWindow", "PixPin"));
        Assert.False(WindowTracker.IsKnownCaptureOverlay("Chrome_WidgetWin_1", "chrome", isTopmost: false));
    }
}
