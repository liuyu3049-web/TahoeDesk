using System;
using System.Runtime.InteropServices;

namespace GlassShell.Core.Shell;

public sealed class Win32TaskbarNative : ITaskbarNative
{
    private const int SW_HIDE = 0;
    private const int SW_SHOW = 5;
    private const uint ABM_NEW = 0x00000000;
    private const uint ABM_REMOVE = 0x00000001;
    private const uint ABM_QUERYPOS = 0x00000002;
    private const uint ABM_SETPOS = 0x00000003;
    private const uint ABM_GETSTATE = 0x00000004;
    private const uint ABM_SETSTATE = 0x0000000A;
    public const int ABS_AUTOHIDE = 0x0000001;
    private const uint ABE_TOP = 1;
    private const int QUNS_RUNNING_D3D_FULL_SCREEN = 3;

    public nint FindPrimaryTaskbar() => FindExplorerPrimaryTaskbar();

    /// <summary>
    /// Locates Windows Explorer's real Shell_TrayWnd while skipping GlassShell's own tray proxy window.
    /// </summary>
    public static nint FindExplorerPrimaryTaskbar()
    {
        uint selfPid = (uint)Environment.ProcessId;
        nint candidate = FindWindowExW(0, 0, "Shell_TrayWnd", null);
        while (candidate != 0)
        {
            GetWindowThreadProcessId(candidate, out uint pid);
            if (pid != 0 && pid != selfPid)
            {
                return candidate;
            }
            candidate = FindWindowExW(0, candidate, "Shell_TrayWnd", null);
        }
        return 0;
    }

    /// <summary>
    /// Reads Windows taskbar state flags (e.g. ABS_AUTOHIDE) directly from Explorer's Shell_TrayWnd.
    /// </summary>
    public static int GetTaskbarAutoHideState()
    {
        try
        {
            var data = new APPBARDATA
            {
                cbSize = (uint)Marshal.SizeOf<APPBARDATA>(),
                hWnd = FindExplorerPrimaryTaskbar()
            };
            return unchecked((int)SHAppBarMessage(ABM_GETSTATE, ref data));
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>
    /// Sets Windows taskbar auto-hide state via documented SHAppBarMessage(ABM_SETSTATE).
    /// Enabling ABS_AUTOHIDE releases the reserved bottom taskbar strip on ALL monitors so maximized windows truly fill the screen.
    /// </summary>
    public static void SetTaskbarAutoHideState(int state)
    {
        try
        {
            var data = new APPBARDATA
            {
                cbSize = (uint)Marshal.SizeOf<APPBARDATA>(),
                hWnd = FindExplorerPrimaryTaskbar(),
                lParam = state
            };
            SHAppBarMessage(ABM_SETSTATE, ref data);
        }
        catch { }
    }

    public bool IsAnyNativeTaskbarVisible()
    {
        uint selfPid = (uint)Environment.ProcessId;

        nint primary = FindWindowExW(0, 0, "Shell_TrayWnd", null);
        while (primary != 0)
        {
            GetWindowThreadProcessId(primary, out uint pid);
            if (pid != 0 && pid != selfPid && IsWindowVisible(primary))
            {
                return true;
            }
            primary = FindWindowExW(0, primary, "Shell_TrayWnd", null);
        }

        nint sec = FindWindowExW(0, 0, "Shell_SecondaryTrayWnd", null);
        while (sec != 0)
        {
            if (IsWindowVisible(sec)) return true;
            sec = FindWindowExW(0, sec, "Shell_SecondaryTrayWnd", null);
        }

        return false;
    }

    public bool SetWindowVisible(nint hwnd, bool visible)
    {
        int cmd = visible ? SW_SHOW : SW_HIDE;
        uint selfPid = (uint)Environment.ProcessId;
        bool any = false;

        // Hide/show EVERY Explorer Shell_TrayWnd (skipping our own proxy)
        nint primary = FindWindowExW(0, 0, "Shell_TrayWnd", null);
        while (primary != 0)
        {
            GetWindowThreadProcessId(primary, out uint pid);
            if (pid != 0 && pid != selfPid)
            {
                ShowWindow(primary, cmd);
                any = true;
            }
            primary = FindWindowExW(0, primary, "Shell_TrayWnd", null);
        }

        if (!any && hwnd != 0)
        {
            ShowWindow(hwnd, cmd);
            any = true;
        }

        // Also hide/restore ALL secondary monitor taskbars (laptop built-in + external displays)
        nint sec = FindWindowExW(0, 0, "Shell_SecondaryTrayWnd", null);
        while (sec != 0)
        {
            ShowWindow(sec, cmd);
            any = true;
            sec = FindWindowExW(0, sec, "Shell_SecondaryTrayWnd", null);
        }

        return any;
    }

    public bool RegisterTopAppBar(nint appBarHwnd, int heightPx, ScreenRect monitorBounds)
    {
        if (appBarHwnd == 0 || heightPx <= 0) return false;

        // Always remove any previous AppBar registration on this HWND first so we never stack twice.
        UnregisterAppBar(appBarHwnd);

        var data = new APPBARDATA
        {
            cbSize = (uint)Marshal.SizeOf<APPBARDATA>(),
            hWnd = appBarHwnd,
            uEdge = ABE_TOP,
            rc = new RECT
            {
                Left = monitorBounds.Left,
                Top = monitorBounds.Top,
                Right = monitorBounds.Right,
                Bottom = monitorBounds.Top + heightPx
            }
        };

        SHAppBarMessage(ABM_NEW, ref data);
        SHAppBarMessage(ABM_QUERYPOS, ref data);

        // If another top AppBar/reserved strip already pushed data.rc.Top down below monitorBounds.Top,
        // do NOT double-stack a second reservation on top of it (which causes a huge empty gap).
        if (data.rc.Top > monitorBounds.Top + 4)
        {
            UnregisterAppBar(appBarHwnd);
            return false;
        }

        data.rc.Top = monitorBounds.Top;
        data.rc.Bottom = monitorBounds.Top + heightPx;
        SHAppBarMessage(ABM_SETPOS, ref data);
        return true;
    }

    public void UnregisterAppBar(nint appBarHwnd)
    {
        if (appBarHwnd == 0) return;
        var data = new APPBARDATA
        {
            cbSize = (uint)Marshal.SizeOf<APPBARDATA>(),
            hWnd = appBarHwnd
        };
        SHAppBarMessage(ABM_REMOVE, ref data);
    }

    public bool IsD3DFullscreenActive()
    {
        return SHQueryUserNotificationState(out int state) == 0
            && state == QUNS_RUNNING_D3D_FULL_SCREEN;
    }

    public static void RestorePrimaryTaskbarNow(int? restoreState = null)
    {
        if (restoreState.HasValue)
        {
            SetTaskbarAutoHideState(restoreState.Value);
        }

        var hwnd = FindExplorerPrimaryTaskbar();
        if (hwnd != 0)
        {
            ShowWindow(hwnd, SW_SHOW);
        }

        nint sec = FindWindowExW(0, 0, "Shell_SecondaryTrayWnd", null);
        while (sec != 0)
        {
            ShowWindow(sec, SW_SHOW);
            sec = FindWindowExW(0, sec, "Shell_SecondaryTrayWnd", null);
        }

        uint msg = RegisterWindowMessageW("TaskbarCreated");
        if (msg != 0)
        {
            SendNotifyMessageW(new nint(0xFFFF), msg, 0, 0);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct APPBARDATA
    {
        public uint cbSize;
        public nint hWnd;
        public uint uCallbackMessage;
        public uint uEdge;
        public RECT rc;
        public nint lParam;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern nint FindWindowExW(nint hwndParent, nint hwndChildAfter, string lpszClass, string? lpszWindow);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(nint hWnd);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern uint RegisterWindowMessageW(string lpString);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SendNotifyMessageW(nint hWnd, uint msg, nint wParam, nint lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(nint hWnd, int nCmdShow);

    [DllImport("shell32.dll")]
    private static extern nuint SHAppBarMessage(uint dwMessage, ref APPBARDATA pData);

    [DllImport("shell32.dll")]
    private static extern int SHQueryUserNotificationState(out int pquns);
}
