using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using GlassShell.Core.Glass;

namespace GlassShell.UI.Glass;

public static class WallpaperLuminanceSampler
{
    private const uint SPI_GETDESKWALLPAPER = 0x0073;
    private static string? _cachedPath;
    private static DateTime _cachedWriteUtc;
    private static bool _cachedDarkInk;

    public static bool ShouldUseDarkInkForTopBar()
    {
        try
        {
            var sb = new StringBuilder(512);
            if (!SystemParametersInfoW(SPI_GETDESKWALLPAPER, (uint)sb.Capacity, sb, 0))
            {
                return false;
            }

            string path = sb.ToString().Trim('\0', ' ');
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                return false;
            }

            DateTime writeUtc = File.GetLastWriteTimeUtc(path);
            if (string.Equals(_cachedPath, path, StringComparison.OrdinalIgnoreCase) && _cachedWriteUtc == writeUtc)
            {
                return _cachedDarkInk;
            }

            // Decode directly downscaled to 160px width (~57 KB instead of ~33 MB for 4K wallpapers!)
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.UriSource = new Uri(path, UriKind.Absolute);
            bmp.DecodePixelWidth = 160;
            bmp.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.EndInit();
            bmp.Freeze();

            var bgra = new FormatConvertedBitmap(bmp, PixelFormats.Bgra32, null, 0);
            int w = bgra.PixelWidth;
            int h = Math.Max(1, (int)Math.Ceiling(bgra.PixelHeight * 0.04));
            int stride = w * 4;
            var pixels = new byte[stride * h];
            bgra.CopyPixels(new System.Windows.Int32Rect(0, 0, w, h), pixels, stride, 0);

            long sumR = 0, sumG = 0, sumB = 0;
            int count = w * h;
            for (int i = 0; i < pixels.Length; i += 4)
            {
                sumB += pixels[i];
                sumG += pixels[i + 1];
                sumR += pixels[i + 2];
            }

            bool darkInk = count > 0 && LuminanceClassifier.ShouldUseDarkInk(
                (byte)(sumR / count),
                (byte)(sumG / count),
                (byte)(sumB / count));

            _cachedPath = path;
            _cachedWriteUtc = writeUtc;
            _cachedDarkInk = darkInk;
            return darkInk;
        }
        catch
        {
            return false;
        }
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SystemParametersInfoW(uint uiAction, uint uiParam, StringBuilder pvParam, uint fWinIni);
}
