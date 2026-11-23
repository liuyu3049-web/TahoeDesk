using GlassShell.Core.Shell;
using Xunit;

namespace GlassShell.Core.Tests.Shell;

public sealed class FullscreenDetectorTests
{
    private readonly MonitorContext _monitor = new(1, new(0, 0, 2560, 1440), 1.5, true);

    [Theory]
    [InlineData("Progman")]
    [InlineData("WorkerW")]
    [InlineData("Shell_TrayWnd")]
    [InlineData("Shell_SecondaryTrayWnd")]
    public void DesktopAndShellWindows_AreNeverFullscreen(string className)
    {
        var win = new ForegroundWindowInfo(100, 500, className, "explorer", _monitor.Bounds, false, false);
        Assert.False(FullscreenDetector.IsFullscreen(win, _monitor, isD3DFullscreen: false));
    }

    [Fact]
    public void BorderlessWindowCoveringEntireMonitor_IsFullscreen()
    {
        var win = new ForegroundWindowInfo(200, 600, "UnrealWindow", "Game", _monitor.Bounds, false, false);
        Assert.True(FullscreenDetector.IsFullscreen(win, _monitor, isD3DFullscreen: false));
    }

    [Fact]
    public void MaximizedWindowBelowTopBar_IsNotFullscreen()
    {
        var win = new ForegroundWindowInfo(201, 601, "Chrome_WidgetWin_1", "chrome", new(0, 42, 2560, 1440), false, false);
        Assert.False(FullscreenDetector.IsFullscreen(win, _monitor, isD3DFullscreen: false));
    }

    [Fact]
    public void MinimizedOrCloakedWindow_IsNeverFullscreen()
    {
        var minimized = new ForegroundWindowInfo(202, 602, "GameWindow", "Game", _monitor.Bounds, true, false);
        var cloaked = new ForegroundWindowInfo(203, 603, "GameWindow", "Game", _monitor.Bounds, false, true);
        Assert.False(FullscreenDetector.IsFullscreen(minimized, _monitor, isD3DFullscreen: false));
        Assert.False(FullscreenDetector.IsFullscreen(cloaked, _monitor, isD3DFullscreen: false));
    }
}
