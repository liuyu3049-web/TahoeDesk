using System;
using System.Collections.Concurrent;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using TahoeDesk.Core.Discovery;

namespace TahoeDesk.UI.Glass;

public sealed record CachedIconEntry(BitmapSource Bitmap, bool HasTransparentCorners);

/// <summary>
/// Single app-wide bounded cache of frozen WPF BitmapSource icons (plus corner-shape metadata) shared across
/// Dock (all monitors), Launchpad, Spotlight, and Folder Stack popups.
/// Supports both Win32/UWP shell icon extraction AND direct high-res custom image files (.png/.jpg/.ico/.bmp/.webp).
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

    public static void Invalidate(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        foreach (string key in Cache.Keys)
        {
            if (key.EndsWith($"|{path}", StringComparison.OrdinalIgnoreCase))
            {
                Cache.TryRemove(key, out _);
            }
        }
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

        CachedIconEntry? entry = IsDirectImageFile(path)
            ? TryLoadDirectImage(path, sizePx)
            : TryExtractShellIcon(path, sizePx);

        Cache[key] = entry;
        return entry;
    }

    private static bool IsDirectImageFile(string path)
    {
        if (!File.Exists(path)) return false;
        string ext = Path.GetExtension(path);
        return ext.Equals(".png", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".jpg", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".jpeg", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".bmp", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".ico", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".webp", StringComparison.OrdinalIgnoreCase);
    }

    private static CachedIconEntry? TryLoadDirectImage(string path, int sizePx)
    {
        try
        {
            byte[] bytes;
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                bytes = new byte[fs.Length];
                fs.ReadExactly(bytes);
            }

            using var ms = new MemoryStream(bytes, writable: false);
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.StreamSource = ms;
            bmp.DecodePixelWidth = Math.Max(96, sizePx);
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.EndInit();
            bmp.Freeze();
            return new CachedIconEntry(bmp, HasTransparentCorners: false);
        }
        catch
        {
            return null;
        }
    }

    private static CachedIconEntry? TryExtractShellIcon(string path, int sizePx)
    {
        var raw = IconExtractor.ExtractRaw(path, sizePx);
        if (raw is null) return null;

        bool transparentCorners = IconShapeAnalyzer.HasTransparentCorners(raw);
        var bmp = BitmapSource.Create(
            raw.Width, raw.Height, 96, 96,
            PixelFormats.Bgra32, null, raw.BgraPixels, raw.Width * 4);
        bmp.Freeze();
        return new CachedIconEntry(bmp, transparentCorners);
    }

    private static string MakeKey(string path, int sizePx) => $"{sizePx}|{path}";
}
