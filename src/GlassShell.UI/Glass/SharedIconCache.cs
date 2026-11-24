using System;
using System.Collections.Concurrent;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using GlassShell.Core.Discovery;

namespace GlassShell.UI.Glass;

/// <summary>
/// Single app-wide bounded cache of frozen WPF BitmapSource icons shared across
/// Dock (all monitors), Launchpad, Spotlight, and Folder Stack popups.
/// Eliminates duplicate raw BGRA byte buffers and redundant IShellItemImageFactory calls.
/// </summary>
public static class SharedIconCache
{
    private const int MaxCachedIcons = 256;
    private static readonly ConcurrentDictionary<string, BitmapSource?> Cache = new(StringComparer.OrdinalIgnoreCase);

    public static bool TryGetCached(string path, int sizePx, out BitmapSource? bitmap)
    {
        return Cache.TryGetValue(MakeKey(path, sizePx), out bitmap);
    }

    public static BitmapSource? GetOrExtract(string? path, int sizePx = 96)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;

        string key = MakeKey(path, sizePx);
        if (Cache.TryGetValue(key, out var existing))
        {
            return existing;
        }

        if (Cache.Count >= MaxCachedIcons)
        {
            Cache.Clear();
        }

        var raw = IconExtractor.ExtractRaw(path, sizePx);
        BitmapSource? bmp = null;
        if (raw is not null)
        {
            bmp = BitmapSource.Create(
                raw.Width, raw.Height, 96, 96,
                PixelFormats.Bgra32, null, raw.BgraPixels, raw.Width * 4);
            bmp.Freeze();
        }

        Cache[key] = bmp;
        return bmp;
    }

    private static string MakeKey(string path, int sizePx) => $"{sizePx}|{path}";
}
