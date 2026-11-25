using System;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;

namespace TahoeDesk.Core.Discovery;

public sealed class IconExtractor
{
    private readonly ConcurrentDictionary<string, RawIconBitmap?> _cache = new(StringComparer.OrdinalIgnoreCase);

    public RawIconBitmap? ExtractHighResIcon(string path, int targetPx = 128)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        string key = $"{path}|{targetPx}";
        return _cache.GetOrAdd(key, _ => ExtractInternal(path, targetPx));
    }

    /// <summary>
    /// Stateless extraction without retaining raw BGRA byte buffers in memory;
    /// preferred when the caller immediately converts to a shared frozen BitmapSource cache.
    /// </summary>
    public static RawIconBitmap? ExtractRaw(string path, int targetPx = 96)
    {
        return string.IsNullOrWhiteSpace(path) ? null : ExtractInternal(path, targetPx);
    }

    private static RawIconBitmap? ExtractInternal(string path, int targetPx)
    {
        try
        {
            Guid iid = typeof(IShellItemImageFactory).GUID;
            int hr = SHCreateItemFromParsingName(path, 0, ref iid, out IShellItemImageFactory? factory);
            if (hr != 0 || factory is null) return null;

            try
            {
                var size = new SIZE { cx = targetPx, cy = targetPx };
                bool isShellApp = path.StartsWith("shell:", StringComparison.OrdinalIgnoreCase);
                hr = isShellApp
                    ? factory.GetImage(size, SIIGBF_BIGGERSIZEOK, out nint hBitmap)
                    : factory.GetImage(size, SIIGBF_BIGGERSIZEOK | SIIGBF_ICONONLY, out hBitmap);
                if (hr != 0 || hBitmap == 0)
                {
                    hr = factory.GetImage(size, SIIGBF_BIGGERSIZEOK, out hBitmap);
                }
                if (hr != 0 || hBitmap == 0)
                {
                    hr = factory.GetImage(size, 0, out hBitmap);
                }

                if (hr != 0 || hBitmap == 0) return null;
                try
                {
                    var raw = ReadHBitmapToBgra(hBitmap);
                    return raw is not null ? IconBoundsNormalizer.TrimTransparentPadding(raw) : null;
                }
                finally
                {
                    DeleteObject(hBitmap);
                }
            }
            finally
            {
                Marshal.ReleaseComObject(factory);
            }
        }
        catch
        {
            return null;
        }
    }

    private static RawIconBitmap? ReadHBitmapToBgra(nint hBitmap)
    {
        if (GetObjectW(hBitmap, Marshal.SizeOf<BITMAP>(), out BITMAP bm) == 0 || bm.bmWidth <= 0 || bm.bmHeight <= 0)
        {
            return null;
        }

        int width = bm.bmWidth;
        int height = bm.bmHeight;
        var pixels = new byte[width * height * 4];

        var bmi = new BITMAPINFO
        {
            bmiHeader = new BITMAPINFOHEADER
            {
                biSize = (uint)Marshal.SizeOf<BITMAPINFOHEADER>(),
                biWidth = width,
                biHeight = -height, // top-down DIB
                biPlanes = 1,
                biBitCount = 32,
                biCompression = 0 // BI_RGB
            }
        };

        nint hdc = GetDC(0);
        try
        {
            int lines = GetDIBits(hdc, hBitmap, 0, (uint)height, pixels, ref bmi, 0);
            return lines > 0 ? new RawIconBitmap(width, height, pixels) : null;
        }
        finally
        {
            ReleaseDC(0, hdc);
        }
    }

    private const uint SIIGBF_BIGGERSIZEOK = 0x01;
    private const uint SIIGBF_ICONONLY = 0x04;

    [StructLayout(LayoutKind.Sequential)]
    private struct SIZE { public int cx; public int cy; }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAP
    {
        public int bmType;
        public int bmWidth;
        public int bmHeight;
        public int bmWidthBytes;
        public ushort bmPlanes;
        public ushort bmBitsPixel;
        public nint bmBits;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public uint biSize;
        public int biWidth;
        public int biHeight;
        public ushort biPlanes;
        public ushort biBitCount;
        public uint biCompression;
        public uint biSizeImage;
        public int biXPelsPerMeter;
        public int biYPelsPerMeter;
        public uint biClrUsed;
        public uint biClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFO
    {
        public BITMAPINFOHEADER bmiHeader;
        public uint bmiColors;
    }

    [ComImport]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b")]
    private interface IShellItemImageFactory
    {
        [PreserveSig]
        int GetImage(SIZE size, uint flags, out nint phbm);
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHCreateItemFromParsingName(string pszPath, nint pbc, ref Guid riid, out IShellItemImageFactory? ppv);

    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetObjectW(nint hgdiobj, int cbBuffer, out BITMAP lpvObject);

    [DllImport("gdi32.dll")]
    private static extern int GetDIBits(nint hdc, nint hbm, uint start, uint cLines, [Out] byte[] lpvBits, ref BITMAPINFO lpbmi, uint usage);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(nint ho);

    [DllImport("user32.dll")]
    private static extern nint GetDC(nint hwnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(nint hwnd, nint hdc);
}
