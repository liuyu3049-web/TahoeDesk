using TahoeDesk.Core.Shell;
using Xunit;

namespace TahoeDesk.Core.Tests.Shell;

public sealed class WinKeyStateMachineTests
{
    [Fact]
    public void SingleLWinPressAndRelease_TriggersLaunchpad()
    {
        var sm = new WinKeyStateMachine();

        Assert.Equal(WinKeyAction.PassThrough, sm.OnKeyDown(WinKeyStateMachine.VkLWin, false, true, false));
        Assert.Equal(WinKeyAction.SuppressStartAndTrigger, sm.OnKeyUp(WinKeyStateMachine.VkLWin, false, true, false));
    }

    [Fact]
    public void SingleRWinPressWithKeyRepeats_StillTriggersLaunchpad()
    {
        var sm = new WinKeyStateMachine();

        Assert.Equal(WinKeyAction.PassThrough, sm.OnKeyDown(WinKeyStateMachine.VkRWin, false, true, false));
        Assert.Equal(WinKeyAction.PassThrough, sm.OnKeyDown(WinKeyStateMachine.VkRWin, false, true, false));
        Assert.Equal(WinKeyAction.PassThrough, sm.OnKeyDown(WinKeyStateMachine.VkRWin, false, true, false));
        Assert.Equal(WinKeyAction.SuppressStartAndTrigger, sm.OnKeyUp(WinKeyStateMachine.VkRWin, false, true, false));
    }

    [Theory]
    [InlineData('E')]
    [InlineData('D')]
    [InlineData('L')]
    [InlineData('V')]
    [InlineData('N')]
    public void WinPlusOtherKeyChord_NeverTriggersLaunchpad(int chordKey)
    {
        var sm = new WinKeyStateMachine();

        Assert.Equal(WinKeyAction.PassThrough, sm.OnKeyDown(WinKeyStateMachine.VkLWin, false, true, false));
        Assert.Equal(WinKeyAction.PassThrough, sm.OnKeyDown(chordKey, false, true, false));
        Assert.Equal(WinKeyAction.PassThrough, sm.OnKeyUp(chordKey, false, true, false));
        Assert.Equal(WinKeyAction.PassThrough, sm.OnKeyUp(WinKeyStateMachine.VkLWin, false, true, false));
    }

    [Fact]
    public void WinHeldWithMouseClick_NeverTriggersLaunchpad()
    {
        var sm = new WinKeyStateMachine();

        Assert.Equal(WinKeyAction.PassThrough, sm.OnKeyDown(WinKeyStateMachine.VkLWin, false, true, false));
        sm.OnMouseButtonDown();
        Assert.Equal(WinKeyAction.PassThrough, sm.OnKeyUp(WinKeyStateMachine.VkLWin, false, true, false));
    }

    [Fact]
    public void DisabledOrFullscreen_AlwaysPassesThrough()
    {
        var sm = new WinKeyStateMachine();

        Assert.Equal(WinKeyAction.PassThrough, sm.OnKeyDown(WinKeyStateMachine.VkLWin, false, enabled: false, isFullscreenActive: false));
        Assert.Equal(WinKeyAction.PassThrough, sm.OnKeyUp(WinKeyStateMachine.VkLWin, false, enabled: false, isFullscreenActive: false));

        Assert.Equal(WinKeyAction.PassThrough, sm.OnKeyDown(WinKeyStateMachine.VkLWin, false, enabled: true, isFullscreenActive: true));
        Assert.Equal(WinKeyAction.PassThrough, sm.OnKeyUp(WinKeyStateMachine.VkLWin, false, enabled: true, isFullscreenActive: true));
    }

    [Fact]
    public void InjectedKeyEvents_IgnoredWithoutCorruptingState()
    {
        var sm = new WinKeyStateMachine();

        Assert.Equal(WinKeyAction.PassThrough, sm.OnKeyDown(WinKeyStateMachine.VkLWin, false, true, false));
        Assert.Equal(WinKeyAction.PassThrough, sm.OnKeyDown(0xFF, isInjected: true, true, false));
        Assert.Equal(WinKeyAction.PassThrough, sm.OnKeyUp(0xFF, isInjected: true, true, false));
        Assert.Equal(WinKeyAction.SuppressStartAndTrigger, sm.OnKeyUp(WinKeyStateMachine.VkLWin, false, true, false));
    }

    [Fact]
    public void GlobalHotkeyManager_ParsesStandardChords()
    {
        Assert.True(GlobalHotkeyManager.TryParseChord("Alt+Space", out uint mods, out uint vk));
        Assert.Equal(GlobalHotkeyManager.MOD_ALT | GlobalHotkeyManager.MOD_NOREPEAT, mods);
        Assert.Equal(GlobalHotkeyManager.VK_SPACE, vk);

        Assert.True(GlobalHotkeyManager.TryParseChord("Ctrl+Shift+K", out uint mods2, out uint vk2));
        Assert.Equal(GlobalHotkeyManager.MOD_CONTROL | GlobalHotkeyManager.MOD_SHIFT | GlobalHotkeyManager.MOD_NOREPEAT, mods2);
        Assert.Equal((uint)'K', vk2);

        Assert.False(GlobalHotkeyManager.TryParseChord("Space", out _, out _));
    }
}
