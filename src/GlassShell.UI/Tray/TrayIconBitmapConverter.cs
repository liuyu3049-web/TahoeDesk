using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace GlassShell.UI.Tray;

/// <summary>
/// Safely converts cross-process Win32 HICON handles into frozen WPF BitmapSources without leaking GDI handles,
/// while detecting blank/transparent blink frames (used by WeChat/QQ message alerts) so they never replace valid icons.
/// </summary>
public static class TrayIconBitmapConverter
{
    private const uint WM_GETICON = 0x007F;
    private const nint ICON_SMALL = 0;
    private const nint ICON_SMALL2 = 2;
    private const int GCL_HICONSM = -34;
    private const int GCL_HICON = -14;

    public static BitmapSource? ConvertFromHIcon(nint hIcon, nint fallbackOwnerHwnd, out bool isBlankTransparentFrame)
    {
        isBlankTransparentFrame = false;

        if (hIcon != 0)
        {
            var converted = TryConvertSingle(hIcon);
            if (converted is not null)
            {
                if (IsAllTransparent(converted))
                {
                    isBlankTransparentFrame = true;
                    return null;
                }
                return converted;
            }
        }
        else
        {
            // Explicit hIcon == 0 during NIM_MODIFY is often a blank blink frame
            isBlankTransparentFrame = true;
        }

        if (fallbackOwnerHwnd != 0)
        {
            nint winIcon = TryQueryWindowIcon(fallbackOwnerHwnd);
            if (winIcon != 0)
            {
                var fallback = TryConvertSingle(winIcon);
                if (fallback is not null && !IsAllTransparent(fallback))
                {
                    return fallback;
                }
            }
        }

        return null;
    }

    public static BitmapSource? ConvertFromHIcon(nint hIcon, nint fallbackOwnerHwnd = 0)
        => ConvertFromHIcon(hIcon, fallbackOwnerHwnd, out _);

    private static bool IsAllTransparent(BitmapSource bmp)
    {
        try
        {
            int w = bmp.PixelWidth;
            int h = bmp.PixelHeight;
            if (w <= 0 || h <= 0 || w > 256 || h > 256) return false;

            BitmapSource bgra = bmp.Format == PixelFormats.Bgra32
                ? bmp
                : new FormatConvertedBitmap(bmp, PixelFormats.Bgra32, null, 0);

            int stride = w * 4;
            byte[] pixels = new byte[stride * h];
            bgra.CopyPixels(pixels, stride, 0);

            for (int i = 3; i < pixels.Length; i += 4)
            {
                if (pixels[i] > 16)
                {
                    return false;
                }
            }
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static BitmapSource? TryConvertSingle(nint rawHIcon)
    {
        nint copy = CopyIcon(rawHIcon);
        nint target = copy != 0 ? copy : rawHIcon;
        try
        {
            var bmp = Imaging.CreateBitmapSourceFromHIcon(
                target,
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
            if (copy != 0)
            {
                DestroyIcon(copy);
            }
        }
    }

    private static nint TryQueryWindowIcon(nint hwnd)
    {
        if (SendMessageTimeoutW(hwnd, WM_GETICON, ICON_SMALL2, 0, 0x0002, 50, out nint res) != 0 && res != 0) return res;
        if (SendMessageTimeoutW(hwnd, WM_GETICON, ICON_SMALL, 0, 0x0002, 50, out res) != 0 && res != 0) return res;
        nint cls = GetClassLongPtrW(hwnd, GCL_HICONSM);
        if (cls != 0) return cls;
        return GetClassLongPtrW(hwnd, GCL_HICON);
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint CopyIcon(nint hIcon);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(nint hIcon);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern nint SendMessageTimeoutW(nint hWnd, uint msg, nint wParam, nint lParam, uint fuFlags, uint uTimeout, out nint lpdwResult);

    [DllImport("user32.dll", EntryPoint = "GetClassLongPtrW")]
    private static extern nint GetClassLongPtrW(nint hWnd, int nIndex);
}
