using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace TahoeDesk.Core.Shell;

/// <summary>
/// Implements macOS-style title-bar double-click "Dock-safe zoom": fills the monitor below the top Menu Bar
/// while stopping cleanly above the bottom Dock capsule so the Dock stays visible and unobstructed.
/// Clicking the native top-right Maximize button keeps standard Windows SW_MAXIMIZE (covering the Dock and triggering Smart Auto-Hide).
/// </summary>
public sealed class DockSafeZoomManager
{
    private const int DWMWA_EXTENDED_FRAME_BOUNDS = 9;
    private const uint SWP_NOZORDER = 0x0004;
    private const uint SWP_NOACTIVATE = 0x0010;
    private const uint SWP_FRAMECHANGED = 0x0020;

    private readonly object _gate = new();
    private readonly Dictionary<nint, ScreenRect> _preZoomBounds = new();

    public static ScreenRect ComputeDockSafeVisibleRect(MonitorContext monitor, int topBarPx, int dockReservePx)
    {
        int top = Math.Max(monitor.WorkArea.Top, monitor.Bounds.Top + Math.Max(0, topBarPx));
        int bottom = Math.Max(top + 240, monitor.Bounds.Bottom - Math.Max(0, dockReservePx));
        bottom = Math.Min(bottom, monitor.Bounds.Bottom);
        return new ScreenRect(monitor.Bounds.Left, top, monitor.Bounds.Right, bottom);
    }

    public static ScreenRect ComputeOuterBoundsForVisibleTarget(
        ScreenRect targetVisible,
        ScreenRect currentWindowRect,
        ScreenRect currentExtendedFrameRect)
    {
        // Compensate for Windows 10/11 DWM invisible resize/shadow border insets (~7px L/R/B)
        // so the VISIBLE edges align pixel-flush with screen left/right, MenuBar bottom, and Dock top.
        int insetLeft = Math.Clamp(currentExtendedFrameRect.Left - currentWindowRect.Left, 0, 24);
        int insetTop = Math.Clamp(currentExtendedFrameRect.Top - currentWindowRect.Top, 0, 24);
        int insetRight = Math.Clamp(currentWindowRect.Right - currentExtendedFrameRect.Right, 0, 24);
        int insetBottom = Math.Clamp(currentWindowRect.Bottom - currentExtendedFrameRect.Bottom, 0, 24);

        return new ScreenRect(
            targetVisible.Left - insetLeft,
            targetVisible.Top - insetTop,
            targetVisible.Right + insetRight,
            targetVisible.Bottom + insetBottom);
    }

    public static bool IsApproximatelyRect(ScreenRect actual, ScreenRect target, int tolerancePx = 16)
    {
        return Math.Abs(actual.Left - target.Left) <= tolerancePx
            && Math.Abs(actual.Top - target.Top) <= tolerancePx
            && Math.Abs(actual.Right - target.Right) <= tolerancePx
            && Math.Abs(actual.Bottom - target.Bottom) <= tolerancePx;
    }

    public bool ToggleZoom(nint hwnd, MonitorContext monitor, int topBarPx, int dockReservePx)
    {
        if (hwnd == 0 || !IsWindow(hwnd) || IsIconic(hwnd)) return false;

        if (!GetWindowRect(hwnd, out RECT rawWin)) return false;
        var winRect = new ScreenRect(rawWin.Left, rawWin.Top, rawWin.Right, rawWin.Bottom);
        var extRect = TryGetExtendedFrameBounds(hwnd, out var dwmRect) ? dwmRect : winRect;

        var targetVisible = ComputeDockSafeVisibleRect(monitor, topBarPx, dockReservePx);

        lock (_gate)
        {
            PruneClosedWindowsLocked();

            if (IsApproximatelyRect(extRect, targetVisible, tolerancePx: 18)
                && _preZoomBounds.TryGetValue(hwnd, out var savedOuter))
            {
                _preZoomBounds.Remove(hwnd);
                return SetWindowPos(
                    hwnd,
                    0,
                    savedOuter.Left,
                    savedOuter.Top,
                    Math.Max(200, savedOuter.Width),
                    Math.Max(160, savedOuter.Height),
                    SWP_NOZORDER | SWP_NOACTIVATE);
            }

            // Save current un-zoomed outer bounds before expanding into Dock-safe area
            _preZoomBounds[hwnd] = winRect;
        }

        var targetOuter = ComputeOuterBoundsForVisibleTarget(targetVisible, winRect, extRect);
        return SetWindowPos(
            hwnd,
            0,
            targetOuter.Left,
            targetOuter.Top,
            Math.Max(200, targetOuter.Width),
            Math.Max(160, targetOuter.Height),
            SWP_NOZORDER | SWP_NOACTIVATE | SWP_FRAMECHANGED);
    }

    private void PruneClosedWindowsLocked()
    {
        if (_preZoomBounds.Count <= 24) return;
        var dead = new List<nint>();
        foreach (var kv in _preZoomBounds)
        {
            if (!IsWindow(kv.Key)) dead.Add(kv.Key);
        }
        foreach (var h in dead) _preZoomBounds.Remove(h);
    }

    private static bool TryGetExtendedFrameBounds(nint hwnd, out ScreenRect rect)
    {
        if (DwmGetWindowAttribute(hwnd, DWMWA_EXTENDED_FRAME_BOUNDS, out RECT rc, Marshal.SizeOf<RECT>()) == 0
            && rc.Right > rc.Left && rc.Bottom > rc.Top)
        {
            rect = new ScreenRect(rc.Left, rc.Top, rc.Right, rc.Bottom);
            return true;
        }
        rect = default;
        return false;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindow(nint hwnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(nint hwnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(nint hwnd, out RECT lpRect);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(nint hWnd, nint hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(nint hwnd, int dwAttribute, out RECT pvAttribute, int cbAttribute);
}
