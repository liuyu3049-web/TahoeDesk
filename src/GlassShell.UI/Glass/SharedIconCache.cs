using System;
using System.Collections.Concurrent;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using GlassShell.Core.Discovery;

namespace GlassShell.UI.Glass;

public sealed record CachedIconEntry(BitmapSource Bitmap, bool HasTransparentCorners);

/// <summary>
/// Single app-wide bounded cache of frozen WPF BitmapSource icons (plus corner-shape metadata) shared across
/// Dock (all monitors), Launchpad, Spotlight, and Folder Stack popups.
/// </summary>
public static class SharedIconCache
{
    private const int MaxCachedIcons = 256;
    private static readonly ConcurrentDictionary<string, CachedIconEntry?> Cache = new(StringComparer.OrdinalIgnoreCase);

    public static bool TryGetCached(string path, int sizePx, out BitmapSource? bitmap)
    {
        if (Cache.TryGetValue(MakeKey(path, sizePx), out var entry))
        {
            bitmap = entry?.Bitmap;
            return true;
        }
        bitmap = null;
        return false;
    }

    public static BitmapSource? GetOrExtract(string? path, int sizePx = 96)
        => GetOrExtractWithShape(path, sizePx)?.Bitmap;

    public static CachedIconEntry? GetOrExtractWithShape(string? path, int sizePx = 96)
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
        CachedIconEntry? entry = null;
        if (raw is not null)
        {
            bool transparentCorners = IconShapeAnalyzer.HasTransparentCorners(raw);
            var bmp = BitmapSource.Create(
                raw.Width, raw.Height, 96, 96,
                PixelFormats.Bgra32, null, raw.BgraPixels, raw.Width * 4);
            bmp.Freeze();
            entry = new CachedIconEntry(bmp, transparentCorners);
        }

        Cache[key] = entry;
        return entry;
    }

    private static string MakeKey(string path, int sizePx) => $"{sizePx}|{path}";
}
