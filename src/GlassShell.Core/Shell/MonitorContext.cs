using System;
using System.Runtime.InteropServices;

namespace GlassShell.Core.Shell;

public readonly record struct ScreenRect(int Left, int Top, int Right, int Bottom)
{
    public int Width => Right - Left;
    public int Height => Bottom - Top;
}

public sealed record MonitorContext(nint MonitorHandle, ScreenRect Bounds, double DpiScale, bool IsPrimary)
{
    public double WidthDip => Bounds.Width / DpiScale;
    public double HeightDip => Bounds.Height / DpiScale;

    public static MonitorContext GetPrimaryMonitor()
    {
        var pt = new POINT { X = 0, Y = 0 };
        var hMonitor = MonitorFromPoint(pt, MONITOR_DEFAULTTOPRIMARY);
        var info = new MONITORINFO { cbSize = (uint)Marshal.SizeOf<MONITORINFO>() };
        var bounds = GetMonitorInfoW(hMonitor, ref info)
            ? new ScreenRect(info.rcMonitor.Left, info.rcMonitor.Top, info.rcMonitor.Right, info.rcMonitor.Bottom)
            : new ScreenRect(0, 0, 1920, 1080);

        double dpiScale = 1.0;
        if (GetDpiForMonitor(hMonitor, MDT_EFFECTIVE_DPI, out uint dpiX, out _) == 0 && dpiX >= 96)
        {
            dpiScale = dpiX / 96.0;
        }

        return new MonitorContext(hMonitor, bounds, dpiScale, IsPrimary: true);
    }

    private const uint MONITOR_DEFAULTTOPRIMARY = 0x00000001;
    private const int MDT_EFFECTIVE_DPI = 0;

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X; public int Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public uint cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    [DllImport("user32.dll")]
    private static extern nint MonitorFromPoint(POINT pt, uint dwFlags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfoW(nint hMonitor, ref MONITORINFO lpmi);

    [DllImport("shcore.dll")]
    private static extern int GetDpiForMonitor(nint hMonitor, int dpiType, out uint dpiX, out uint dpiY);
}

public readonly record struct ForegroundWindowInfo(
    nint Hwnd,
    string ClassName,
    string ProcessName,
    ScreenRect Bounds,
    bool IsMinimized,
    bool IsCloaked);
