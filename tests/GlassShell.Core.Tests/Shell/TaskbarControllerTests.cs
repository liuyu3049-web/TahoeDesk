using GlassShell.Core.Shell;
using Xunit;

namespace GlassShell.Core.Tests.Shell;

public sealed class TaskbarControllerTests
{
    private sealed class FakeTaskbarNative(nint taskbarHwnd) : ITaskbarNative
    {
        public nint TaskbarHwnd { get; set; } = taskbarHwnd;
        public bool TaskbarVisible { get; set; } = true;
        public bool AppBarRegistered { get; set; }
        public int RegisteredHeightPx { get; set; }
        public bool D3DFullscreen { get; set; }

        public nint FindPrimaryTaskbar() => TaskbarHwnd;

        public bool SetWindowVisible(nint hwnd, bool visible)
        {
            if (hwnd != TaskbarHwnd || hwnd == 0) return false;
            TaskbarVisible = visible;
            return true;
        }

        public bool RegisterTopAppBar(nint appBarHwnd, int heightPx, ScreenRect monitorBounds)
        {
            AppBarRegistered = true;
            RegisteredHeightPx = heightPx;
            return true;
        }

        public void UnregisterAppBar(nint appBarHwnd)
        {
            AppBarRegistered = false;
            RegisteredHeightPx = 0;
        }

        public bool IsD3DFullscreenActive() => D3DFullscreen;
    }

    [Fact]
    public void Acquire_HidesTaskbarAndRegisters28DipTopAppBar()
    {
        var fake = new FakeTaskbarNative(taskbarHwnd: 42);
        var ctrl = new TaskbarController(fake);
        ctrl.Acquire(appBarHwnd: 99, new(1, new(0, 0, 1920, 1080), 1.5, true), topBarDip: 28);
        Assert.True(ctrl.IsAcquired);
        Assert.False(fake.TaskbarVisible);
        Assert.True(fake.AppBarRegistered);
        Assert.Equal(42, fake.RegisteredHeightPx); // 28 * 1.5
    }

    [Fact]
    public void HandleTaskbarShownEvent_ReHidesWhenNotPeeking_KeepsVisibleWhenPeeking()
    {
        var fake = new FakeTaskbarNative(42);
        var ctrl = new TaskbarController(fake);
        ctrl.Acquire(99, new(1, new(0, 0, 1920, 1080), 1.0, true));

        fake.TaskbarVisible = true;
        ctrl.HandleTaskbarShownEvent();
        Assert.False(fake.TaskbarVisible);

        ctrl.BeginTrayPeek();
        Assert.True(ctrl.IsTrayPeekActive);
        Assert.True(fake.TaskbarVisible);
        ctrl.HandleTaskbarShownEvent();
        Assert.True(fake.TaskbarVisible);

        ctrl.EndTrayPeek();
        Assert.False(ctrl.IsTrayPeekActive);
        Assert.False(fake.TaskbarVisible);
    }

    [Fact]
    public void HandleExplorerRestart_ReResolvesTaskbarAndReHidesWhenNotPeeking()
    {
        var fake = new FakeTaskbarNative(42);
        var ctrl = new TaskbarController(fake);
        ctrl.Acquire(99, new(1, new(0, 0, 1920, 1080), 1.0, true));

        fake.TaskbarHwnd = 88;
        fake.TaskbarVisible = true;
        ctrl.HandleExplorerRestart();
        Assert.False(fake.TaskbarVisible);
    }

    [Fact]
    public void FullscreenEntry_CancelsTrayPeekAndHidesBars_ExitRestores()
    {
        var fake = new FakeTaskbarNative(42);
        var ctrl = new TaskbarController(fake);
        bool? lastBarsVisible = null;
        ctrl.BarsVisibilityChanged += v => lastBarsVisible = v;
        ctrl.Acquire(99, new(1, new(0, 0, 1920, 1080), 1.0, true));

        ctrl.BeginTrayPeek();
        ctrl.UpdateFullscreenState(true);
        Assert.True(ctrl.IsFullscreenYieldActive);
        Assert.False(ctrl.IsTrayPeekActive);
        Assert.False(fake.TaskbarVisible);
        Assert.False(lastBarsVisible);

        ctrl.UpdateFullscreenState(false);
        Assert.False(ctrl.IsFullscreenYieldActive);
        Assert.True(lastBarsVisible);
    }

    [Fact]
    public void Release_IsIdempotentAndRestoresTaskbarAndUnregistersAppBar()
    {
        var fake = new FakeTaskbarNative(42);
        var ctrl = new TaskbarController(fake);
        ctrl.Acquire(99, new(1, new(0, 0, 1920, 1080), 1.0, true));
        ctrl.Release();
        ctrl.Release();
        Assert.False(ctrl.IsAcquired);
        Assert.True(fake.TaskbarVisible);
        Assert.False(fake.AppBarRegistered);
    }
}
