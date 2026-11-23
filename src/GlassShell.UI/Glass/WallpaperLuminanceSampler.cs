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

    public static bool ShouldUseDarkInkForTopBar()
    {
        try
        {
            var sb = new StringBuilder(512);
            if (!SystemParametersInfoW(SPI_GETDESKWALLPAPER, (uint)sb.Capacity, sb, 0))
            {
                return false;
            }

            string path = sb.ToString();
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                return false;
            }

            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var decoder = BitmapDecoder.Create(fs, BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.OnLoad);
            if (decoder.Frames.Count == 0) return false;

            // Downscale for ultra-fast sampling of top 4% strip
            var frame = decoder.Frames[0];
            double scale = Math.Min(1.0, 240.0 / Math.Max(1, frame.PixelWidth));
            var scaled = new TransformedBitmap(frame, new ScaleTransform(scale, scale));
            var bgra = new FormatConvertedBitmap(scaled, PixelFormats.Bgra32, null, 0);

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

            if (count <= 0) return false;
            return LuminanceClassifier.ShouldUseDarkInk(
                (byte)(sumR / count),
                (byte)(sumG / count),
                (byte)(sumB / count));
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
