using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace GlassShell.UI.Dock;

/// <summary>
/// Captures a lightweight static window snapshot right before minimization (EVENT_SYSTEM_MINIMIZESTART)
/// using Win32 PrintWindow(PW_RENDERFULLCONTENT) with clean GDI resource disposal.
/// </summary>
public static class WindowThumbnailCapturer
{
    private const uint PW_RENDERFULLCONTENT = 0x00000002;
    private const int HALFTONE = 4;
    private const int SRCCOPY = 0x00CC0020;

    public static BitmapSource? CaptureThumbnail(nint hwnd, int targetWidth = 160, int targetHeight = 112)
    {
        if (hwnd == 0 || !IsWindow(hwnd) || IsIconic(hwnd)) return null;
        if (!GetWindowRect(hwnd, out RECT rc)) return null;

        int srcW = rc.Right - rc.Left;
        int srcH = rc.Bottom - rc.Top;
        if (srcW < 64 || srcH < 48 || srcW > 8192 || srcH > 8192) return null;

        nint screenDc = GetDC(0);
        if (screenDc == 0) return null;

        nint fullDc = 0, fullBmp = 0, oldFull = 0;
        nint thumbDc = 0, thumbBmp = 0, oldThumb = 0;

        try
        {
            fullDc = CreateCompatibleDC(screenDc);
            fullBmp = CreateCompatibleBitmap(screenDc, srcW, srcH);
            if (fullDc == 0 || fullBmp == 0) return null;
            oldFull = SelectObject(fullDc, fullBmp);

            if (!PrintWindow(hwnd, fullDc, PW_RENDERFULLCONTENT))
            {
                return null;
            }

            // Preserve aspect ratio inside targetWidth x targetHeight
            double scale = Math.Min((double)targetWidth / srcW, (double)targetHeight / srcH);
            int dstW = Math.Max(24, (int)Math.Round(srcW * scale));
            int dstH = Math.Max(18, (int)Math.Round(srcH * scale));

            thumbDc = CreateCompatibleDC(screenDc);
            thumbBmp = CreateCompatibleBitmap(screenDc, dstW, dstH);
            if (thumbDc == 0 || thumbBmp == 0) return null;
            oldThumb = SelectObject(thumbDc, thumbBmp);

            SetStretchBltMode(thumbDc, HALFTONE);
            StretchBlt(thumbDc, 0, 0, dstW, dstH, fullDc, 0, 0, srcW, srcH, SRCCOPY);

            var bmp = Imaging.CreateBitmapSourceFromHBitmap(
                thumbBmp,
                IntPtr.Zero,
                Int32Rect.Empty,
                BitmapSizeOptions.FromEmptyOptions());
            bmp.Freeze();
            return bmp;
        }
        catch
        {
            return null;
        }
        finally
        {
            if (oldThumb != 0 && thumbDc != 0) SelectObject(thumbDc, oldThumb);
            if (thumbBmp != 0) DeleteObject(thumbBmp);
            if (thumbDc != 0) DeleteDC(thumbDc);

            if (oldFull != 0 && fullDc != 0) SelectObject(fullDc, oldFull);
            if (fullBmp != 0) DeleteObject(fullBmp);
            if (fullDc != 0) DeleteDC(fullDc);

            ReleaseDC(0, screenDc);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindow(nint hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(nint hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(nint hWnd, out RECT lpRect);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PrintWindow(nint hwnd, nint hdcBlt, uint nFlags);

    [DllImport("user32.dll")]
    private static extern nint GetDC(nint hWnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(nint hWnd, nint hDC);

    [DllImport("gdi32.dll")]
    private static extern nint CreateCompatibleDC(nint hdc);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteDC(nint hdc);

    [DllImport("gdi32.dll")]
    private static extern nint CreateCompatibleBitmap(nint hdc, int nWidth, int nHeight);

    [DllImport("gdi32.dll")]
    private static extern nint SelectObject(nint hdc, nint hgdiobj);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(nint hObject);

    [DllImport("gdi32.dll")]
    private static extern int SetStretchBltMode(nint hdc, int mode);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool StretchBlt(nint hdcDest, int xDest, int yDest, int wDest, int hDest, nint hdcSrc, int xSrc, int ySrc, int wSrc, int hSrc, int rop);
}
