using System;
using System.Collections.Concurrent;
using System.IO;
using System.Windows.Media.Imaging;

namespace RemoteFlattener;

/// <summary>
/// Thread-safe cache for decoded wallpaper thumbnails. Bitmaps are frozen and
/// can be used from any thread.  Keyed by file path + last-write-time (local)
/// or a prefix of base64 data (remote).  Survives TreeWindow close/reopen so
/// subsequent Win+Tab presses are near-instant.
/// </summary>
internal static class WallpaperCache
{
    private static readonly ConcurrentDictionary<string, BitmapImage> _cache = new();

    /// <summary>
    /// Gets or loads a wallpaper bitmap from a local file path.
    /// Returns null if the file doesn't exist or can't be read.
    /// </summary>
    public static BitmapImage? GetOrLoadFromFile(string path)
    {
        if (string.IsNullOrEmpty(path)) return null;
        if (!File.Exists(path)) return null;

        // Include last-write time in key so cache invalidates on wallpaper change.
        var lastWrite = File.GetLastWriteTimeUtc(path).Ticks;
        var key = $"file:{path}|{lastWrite}";

        return _cache.GetOrAdd(key, _ =>
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            return DecodeBitmap(stream);
        });
    }

    /// <summary>
    /// Gets or decodes a wallpaper bitmap from base64-encoded JPEG data.
    /// Returns null if data is null/empty or can't be decoded.
    /// </summary>
    public static BitmapImage? GetOrLoadFromBase64(string base64Data)
    {
        if (string.IsNullOrEmpty(base64Data)) return null;

        // Use first 64 chars as cache key (enough to uniquely identify the image
        // without hashing the entire string on every call).
        var key = $"b64:{(base64Data.Length > 64 ? base64Data[..64] : base64Data)}|{base64Data.Length}";

        try
        {
            return _cache.GetOrAdd(key, _ =>
            {
                var bytes = Convert.FromBase64String(base64Data);
                using var ms = new MemoryStream(bytes);
                return DecodeBitmap(ms);
            });
        }
        catch (Exception ex)
        {
            Logging.AppLogger.Log($"WallpaperCache: failed to decode base64 wallpaper ({base64Data.Length} chars): {ex.Message}");
            return null;
        }
    }

    /// <summary>Evicts all cached entries (e.g. on settings change).</summary>
    public static void Clear() => _cache.Clear();

    private static BitmapImage DecodeBitmap(Stream stream)
    {
        var bmp = new BitmapImage();
        bmp.BeginInit();
        bmp.StreamSource = stream;
        bmp.DecodePixelWidth = 128;
        bmp.CacheOption = BitmapCacheOption.OnLoad;
        bmp.EndInit();
        bmp.Freeze();
        return bmp;
    }
}
