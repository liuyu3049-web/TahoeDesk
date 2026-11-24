using System;
using System.Runtime.InteropServices;

namespace GlassShell.Watchdog;

internal static class Program
{
    private const uint SYNCHRONIZE = 0x00100000;
    private const uint INFINITE = 0xFFFFFFFF;
    private const uint WAIT_OBJECT_0 = 0x00000000;
    private const int SW_SHOW = 5;
    private const uint ABM_SETSTATE = 0x0000000A;

    [STAThread]
    private static int Main(string[] args)
    {
        int parentPid = 0;
        string? cleanEventName = null;
        int? restoreAutoHideState = null;
        bool restoreOnly = false;

        for (int i = 0; i < args.Length; i++)
        {
            if (string.Equals(args[i], "--restore-taskbar", StringComparison.OrdinalIgnoreCase))
            {
                restoreOnly = true;
            }
            else if (string.Equals(args[i], "--parent-pid", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
                int.TryParse(args[++i], out parentPid);
            }
            else if (string.Equals(args[i], "--clean-event", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
                cleanEventName = args[++i];
            }
            else if (string.Equals(args[i], "--restore-autohide", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
                if (int.TryParse(args[++i], out int st)) restoreAutoHideState = st;
            }
        }

        if (restoreOnly || parentPid <= 0)
        {
            RestoreTaskbar(restoreAutoHideState ?? 0);
            return 0;
        }

        nint parentHandle = OpenProcess(SYNCHRONIZE, false, (uint)parentPid);
        if (parentHandle == 0)
        {
            RestoreTaskbar(restoreAutoHideState);
            return 0;
        }

        try
        {
            WaitForSingleObject(parentHandle, INFINITE);

            bool cleanExit = false;
            if (!string.IsNullOrWhiteSpace(cleanEventName))
            {
                nint ev = OpenEventW(SYNCHRONIZE, false, cleanEventName);
                if (ev != 0)
                {
                    try
                    {
                        cleanExit = WaitForSingleObject(ev, 0) == WAIT_OBJECT_0;
                    }
                    finally
                    {
                        CloseHandle(ev);
                    }
                }
            }

            if (!cleanExit)
            {
                RestoreTaskbar(restoreAutoHideState);
            }
        }
        finally
        {
            CloseHandle(parentHandle);
        }

        return 0;
    }

    private static void RestoreTaskbar(int? restoreAutoHideState)
    {
        nint firstTray = FindWindowExW(0, 0, "Shell_TrayWnd", null);

        if (restoreAutoHideState.HasValue && firstTray != 0)
        {
            var data = new APPBARDATA
            {
                cbSize = (uint)Marshal.SizeOf<APPBARDATA>(),
                hWnd = firstTray,
                lParam = restoreAutoHideState.Value
            };
            SHAppBarMessage(ABM_SETSTATE, ref data);
        }

        nint tray = firstTray;
        while (tray != 0)
        {
            ShowWindow(tray, SW_SHOW);
            tray = FindWindowExW(0, tray, "Shell_TrayWnd", null);
        }

        nint secondary = FindWindowExW(0, 0, "Shell_SecondaryTrayWnd", null);
        while (secondary != 0)
        {
            ShowWindow(secondary, SW_SHOW);
            secondary = FindWindowExW(0, secondary, "Shell_SecondaryTrayWnd", null);
        }

        var anim = new ANIMATIONINFO { cbSize = (uint)Marshal.SizeOf<ANIMATIONINFO>(), iMinAnimate = 1 };
        SystemParametersInfoW(0x0049 /* SPI_SETANIMATION */, anim.cbSize, ref anim, 0x0002 /* SPIF_SENDCHANGE */);

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

    [StructLayout(LayoutKind.Sequential)]
    private struct ANIMATIONINFO
    {
        public uint cbSize;
        public int iMinAnimate;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern nint FindWindowExW(nint hwndParent, nint hwndChildAfter, string lpszClass, string? lpszWindow);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint OpenProcess(uint dwDesiredAccess, [MarshalAs(UnmanagedType.Bool)] bool bInheritHandle, uint dwProcessId);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint OpenEventW(uint dwDesiredAccess, [MarshalAs(UnmanagedType.Bool)] bool bInheritHandle, string lpName);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(nint hHandle, uint dwMilliseconds);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(nint hObject);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(nint hWnd, int nCmdShow);

    [DllImport("shell32.dll")]
    private static extern nuint SHAppBarMessage(uint dwMessage, ref APPBARDATA pData);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SystemParametersInfoW(uint uiAction, uint uiParam, ref ANIMATIONINFO pvParam, uint fWinIni);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern uint RegisterWindowMessageW(string lpString);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SendNotifyMessageW(nint hWnd, uint msg, nint wParam, nint lParam);
}
