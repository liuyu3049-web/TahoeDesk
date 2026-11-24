using System;
using System.Runtime.InteropServices;

namespace GlassShell.Watchdog;

internal static class Program
{
    private const uint SYNCHRONIZE = 0x00100000;
    private const uint INFINITE = 0xFFFFFFFF;
    private const uint WAIT_OBJECT_0 = 0x00000000;
    private const int SW_SHOW = 5;

    [STAThread]
    private static int Main(string[] args)
    {
        int parentPid = 0;
        string? cleanEventName = null;
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
        }

        if (restoreOnly || parentPid <= 0)
        {
            RestoreTaskbar();
            return 0;
        }

        nint parentHandle = OpenProcess(SYNCHRONIZE, false, (uint)parentPid);
        if (parentHandle == 0)
        {
            RestoreTaskbar();
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
                RestoreTaskbar();
            }
        }
        finally
        {
            CloseHandle(parentHandle);
        }

        return 0;
    }

    private static void RestoreTaskbar()
    {
        nint tray = FindWindowExW(0, 0, "Shell_TrayWnd", null);
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

        // Restore native Windows minimize animation in case GlassShell exited uncleanly while suppressing top-left ghost
        var anim = new ANIMATIONINFO { cbSize = (uint)Marshal.SizeOf<ANIMATIONINFO>(), iMinAnimate = 1 };
        SystemParametersInfoW(0x0049 /* SPI_SETANIMATION */, anim.cbSize, ref anim, 0);

        // Ask all running tray apps to re-register their icons back onto Explorer's native taskbar
        uint msg = RegisterWindowMessageW("TaskbarCreated");
        if (msg != 0)
        {
            SendNotifyMessageW(new nint(0xFFFF), msg, 0, 0);
        }
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

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern nint FindWindowW(string lpClassName, string? lpWindowName);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(nint hWnd, int nCmdShow);

    [StructLayout(LayoutKind.Sequential)]
    private struct ANIMATIONINFO
    {
        public uint cbSize;
        public int iMinAnimate;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SystemParametersInfoW(uint uiAction, uint uiParam, ref ANIMATIONINFO pvParam, uint fWinIni);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern uint RegisterWindowMessageW(string lpString);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SendNotifyMessageW(nint hWnd, uint msg, nint wParam, nint lParam);
}
