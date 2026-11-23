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
    private const uint ABE_TOP = 1;
    private const int QUNS_RUNNING_D3D_FULL_SCREEN = 3;

    public nint FindPrimaryTaskbar() => FindWindowW("Shell_TrayWnd", null);

    public bool SetWindowVisible(nint hwnd, bool visible)
    {
        if (hwnd == 0) return false;
        int cmd = visible ? SW_SHOW : SW_HIDE;
        ShowWindow(hwnd, cmd);

        // Also hide/restore all secondary monitor taskbars (built-in / multi-display)
        nint sec = FindWindowExW(0, 0, "Shell_SecondaryTrayWnd", null);
        while (sec != 0)
        {
            ShowWindow(sec, cmd);
            sec = FindWindowExW(0, sec, "Shell_SecondaryTrayWnd", null);
        }

        return true;
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

    public static void RestorePrimaryTaskbarNow()
    {
        var hwnd = FindWindowW("Shell_TrayWnd", null);
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
    private static extern nint FindWindowW(string lpClassName, string? lpWindowName);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern nint FindWindowExW(nint hwndParent, nint hwndChildAfter, string lpszClass, string? lpszWindow);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(nint hWnd, int nCmdShow);

    [DllImport("shell32.dll")]
    private static extern nuint SHAppBarMessage(uint dwMessage, ref APPBARDATA pData);

    [DllImport("shell32.dll")]
    private static extern int SHQueryUserNotificationState(out int pquns);
}
