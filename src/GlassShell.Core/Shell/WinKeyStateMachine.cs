namespace GlassShell.Core.Shell;

public enum WinKeyAction
{
    PassThrough,
    SuppressStartAndTrigger
}

/// <summary>
/// Pure state machine that distinguishes a clean single-press LWin/RWin tap (triggers Launchpad)
/// from every Win+* keyboard shortcut or Win+Mouse chord (which must pass straight through to Windows).
/// </summary>
public sealed class WinKeyStateMachine
{
    public const int VkLWin = 0x5B;
    public const int VkRWin = 0x5C;

    private int _activeWinVk;
    private bool _chordOccurred;

    public WinKeyAction OnKeyDown(int vkCode, bool isInjected, bool enabled, bool isFullscreenActive)
    {
        if (isInjected)
        {
            return WinKeyAction.PassThrough;
        }

        if (IsWinKey(vkCode))
        {
            if (_activeWinVk == 0)
            {
                _activeWinVk = vkCode;
                _chordOccurred = !enabled || isFullscreenActive;
            }
            else if (_activeWinVk != vkCode)
            {
                // Both LWin and RWin pressed together -> treat as chord
                _chordOccurred = true;
            }
            // Key auto-repeat of the same Win key while held is intentionally ignored (not a chord)
            return WinKeyAction.PassThrough;
        }

        // Any non-Win key pressed while Win is held marks this sequence as a Windows chord (e.g. Win+E, Win+Shift+S)
        if (_activeWinVk != 0)
        {
            _chordOccurred = true;
        }

        return WinKeyAction.PassThrough;
    }

    public WinKeyAction OnKeyUp(int vkCode, bool isInjected, bool enabled, bool isFullscreenActive)
    {
        if (isInjected)
        {
            return WinKeyAction.PassThrough;
        }

        if (IsWinKey(vkCode))
        {
            bool shouldTrigger = _activeWinVk == vkCode
                && !_chordOccurred
                && enabled
                && !isFullscreenActive;

            _activeWinVk = 0;
            _chordOccurred = false;

            return shouldTrigger ? WinKeyAction.SuppressStartAndTrigger : WinKeyAction.PassThrough;
        }

        return WinKeyAction.PassThrough;
    }

    public void OnMouseButtonDown()
    {
        if (_activeWinVk != 0)
        {
            _chordOccurred = true;
        }
    }

    public void Reset()
    {
        _activeWinVk = 0;
        _chordOccurred = false;
    }

    private static bool IsWinKey(int vkCode) => vkCode is VkLWin or VkRWin;
}
