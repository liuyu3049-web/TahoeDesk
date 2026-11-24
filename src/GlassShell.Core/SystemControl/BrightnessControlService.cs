using System;
using System.Runtime.InteropServices;

namespace GlassShell.Core.SystemControl;

/// <summary>
/// Display brightness controller supporting external monitors via DDC/CI (dxva2.dll)
/// and internal laptop panels via \\.\LCD IOCTL, degrading gracefully when unsupported.
/// </summary>
public sealed class BrightnessControlService
{
    private const uint MONITOR_DEFAULTTOPRIMARY = 1;
    private const uint GENERIC_READ = 0x80000000;
    private const uint GENERIC_WRITE = 0x40000000;
    private const uint FILE_SHARE_READ = 0x00000001;
    private const uint FILE_SHARE_WRITE = 0x00000002;
    private const uint OPEN_EXISTING = 3;
    private const uint IOCTL_VIDEO_QUERY_DISPLAY_BRIGHTNESS = 0x00230498;
    private const uint IOCTL_VIDEO_SET_DISPLAY_BRIGHTNESS = 0x0023049C;

    public bool TryGetBrightness(out int percent)
    {
        if (TryGetDdcBrightness(out percent)) return true;
        if (TryGetLcdBrightness(out percent)) return true;
        percent = 0;
        return false;
    }

    public bool TrySetBrightness(int percent)
    {
        int clamped = SystemControlHelpers.ClampPercent(percent);
        bool any = false;
        if (TrySetDdcBrightness(clamped)) any = true;
        if (TrySetLcdBrightness(clamped)) any = true;
        return any;
    }

    private static bool TryGetDdcBrightness(out int percent)
    {
        percent = 0;
        try
        {
            nint hMon = MonitorFromPoint(new POINT { X = 0, Y = 0 }, MONITOR_DEFAULTTOPRIMARY);
            if (hMon == 0 || !GetNumberOfPhysicalMonitorsFromHMONITOR(hMon, out uint count) || count == 0)
            {
                return false;
            }

            var monitors = new PHYSICAL_MONITOR[count];
            if (!GetPhysicalMonitorsFromHMONITOR(hMon, count, monitors))
            {
                return false;
            }

            try
            {
                if (GetMonitorBrightness(monitors[0].hPhysicalMonitor, out uint min, out uint cur, out uint max) && max > min)
                {
                    percent = SystemControlHelpers.ClampPercent((int)Math.Round((cur - min) * 100.0 / (max - min)));
                    return true;
                }
                return false;
            }
            finally
            {
                DestroyPhysicalMonitors(count, monitors);
            }
        }
        catch
        {
            return false;
        }
    }

    private static bool TrySetDdcBrightness(int percent)
    {
        try
        {
            nint hMon = MonitorFromPoint(new POINT { X = 0, Y = 0 }, MONITOR_DEFAULTTOPRIMARY);
            if (hMon == 0 || !GetNumberOfPhysicalMonitorsFromHMONITOR(hMon, out uint count) || count == 0)
            {
                return false;
            }

            var monitors = new PHYSICAL_MONITOR[count];
            if (!GetPhysicalMonitorsFromHMONITOR(hMon, count, monitors))
            {
                return false;
            }

            try
            {
                if (GetMonitorBrightness(monitors[0].hPhysicalMonitor, out uint min, out _, out uint max) && max > min)
                {
                    uint target = min + (uint)Math.Round((max - min) * (percent / 100.0));
                    return SetMonitorBrightness(monitors[0].hPhysicalMonitor, target);
                }
                return false;
            }
            finally
            {
                DestroyPhysicalMonitors(count, monitors);
            }
        }
        catch
        {
            return false;
        }
    }

    private static bool TryGetLcdBrightness(out int percent)
    {
        percent = 0;
        nint handle = CreateFileW(@"\\.\LCD", GENERIC_READ | GENERIC_WRITE, FILE_SHARE_READ | FILE_SHARE_WRITE, 0, OPEN_EXISTING, 0, 0);
        if (handle == 0 || handle == new nint(-1)) return false;
        try
        {
            var data = new DISPLAY_BRIGHTNESS();
            if (DeviceIoControl(handle, IOCTL_VIDEO_QUERY_DISPLAY_BRIGHTNESS, IntPtr.Zero, 0, ref data, (uint)Marshal.SizeOf<DISPLAY_BRIGHTNESS>(), out _, IntPtr.Zero))
            {
                percent = SystemControlHelpers.ClampPercent(data.ucACBrightness > 0 ? data.ucACBrightness : data.ucDCBrightness);
                return true;
            }
            return false;
        }
        catch
        {
            return false;
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    private static bool TrySetLcdBrightness(int percent)
    {
        nint handle = CreateFileW(@"\\.\LCD", GENERIC_READ | GENERIC_WRITE, FILE_SHARE_READ | FILE_SHARE_WRITE, 0, OPEN_EXISTING, 0, 0);
        if (handle == 0 || handle == new nint(-1)) return false;
        try
        {
            byte level = (byte)SystemControlHelpers.ClampPercent(percent);
            var data = new DISPLAY_BRIGHTNESS { ucDisplayPolicy = 3, ucACBrightness = level, ucDCBrightness = level };
            return DeviceIoControlSet(handle, IOCTL_VIDEO_SET_DISPLAY_BRIGHTNESS, ref data, (uint)Marshal.SizeOf<DISPLAY_BRIGHTNESS>(), IntPtr.Zero, 0, out _, IntPtr.Zero);
        }
        catch
        {
            return false;
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X; public int Y; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct PHYSICAL_MONITOR
    {
        public nint hPhysicalMonitor;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string szPhysicalMonitorDescription;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DISPLAY_BRIGHTNESS
    {
        public byte ucDisplayPolicy;
        public byte ucACBrightness;
        public byte ucDCBrightness;
    }

    [DllImport("user32.dll")]
    private static extern nint MonitorFromPoint(POINT pt, uint dwFlags);

    [DllImport("dxva2.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNumberOfPhysicalMonitorsFromHMONITOR(nint hMonitor, out uint pdwNumberOfPhysicalMonitors);

    [DllImport("dxva2.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetPhysicalMonitorsFromHMONITOR(nint hMonitor, uint dwPhysicalMonitorArraySize, [Out] PHYSICAL_MONITOR[] pPhysicalMonitorArray);

    [DllImport("dxva2.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyPhysicalMonitors(uint dwPhysicalMonitorArraySize, [In] PHYSICAL_MONITOR[] pPhysicalMonitorArray);

    [DllImport("dxva2.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorBrightness(nint hMonitor, out uint pdwMinimumBrightness, out uint pdwCurrentBrightness, out uint pdwMaximumBrightness);

    [DllImport("dxva2.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetMonitorBrightness(nint hMonitor, uint dwNewBrightness);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint CreateFileW(string lpFileName, uint dwDesiredAccess, uint dwShareMode, nint lpSecurityAttributes, uint dwCreationDisposition, uint dwFlagsAndAttributes, nint hTemplateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(nint hDevice, uint dwIoControlCode, IntPtr lpInBuffer, uint nInBufferSize, ref DISPLAY_BRIGHTNESS lpOutBuffer, uint nOutBufferSize, out uint lpBytesReturned, IntPtr lpOverlapped);

    [DllImport("kernel32.dll", EntryPoint = "DeviceIoControl", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControlSet(nint hDevice, uint dwIoControlCode, ref DISPLAY_BRIGHTNESS lpInBuffer, uint nInBufferSize, IntPtr lpOutBuffer, uint nOutBufferSize, out uint lpBytesReturned, IntPtr lpOverlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(nint hObject);
}
