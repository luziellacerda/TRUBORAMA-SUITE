using System.Collections.Concurrent;
using System.IO;
using System.Windows.Media.Imaging;

namespace TurboBoxManager;

internal static class CatalogThumbnailLoader
{
    private static readonly ConcurrentDictionary<string, WeakReference<BitmapSource>> Cache =
        new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<string, Lazy<BitmapSource?>> PendingLoads =
        new(StringComparer.Ordinal);

    internal static BitmapSource? Load(string? source, int decodePixelWidth)
    {
        if (string.IsNullOrWhiteSpace(source)) return null;
        var decodeWidth = Math.Clamp(decodePixelWidth, 64, 1024);

        var key = $"{decodeWidth}|{source}";
        if (Cache.TryGetValue(key, out var weak)
            && weak.TryGetTarget(out var existing))
            return existing;

        var pending = PendingLoads.GetOrAdd(
            key,
            _ => new Lazy<BitmapSource?>(
                () => Decode(source, decodeWidth),
                LazyThreadSafetyMode.ExecutionAndPublication));
        try
        {
            var bitmap = pending.Value;
            if (bitmap is null) return null;

            Cache[key] = new WeakReference<BitmapSource>(bitmap);
            ScavengeCache();
            return bitmap;
        }
        finally
        {
            ((ICollection<KeyValuePair<string, Lazy<BitmapSource?>>>)PendingLoads).Remove(
                new KeyValuePair<string, Lazy<BitmapSource?>>(key, pending));
        }
    }

    private static BitmapImage? Decode(string source, int decodeWidth)
    {
        try
        {
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
            bitmap.DecodePixelWidth = decodeWidth;
            bitmap.UriSource = new Uri(source, UriKind.Absolute);
            bitmap.EndInit();
            bitmap.Freeze();
            return bitmap;
        }
        catch (Exception exception) when (exception is IOException
                                           or UnauthorizedAccessException
                                           or NotSupportedException
                                           or FileFormatException
                                           or ArgumentException
                                           or InvalidOperationException
                                           or UriFormatException)
        {
            return null;
        }
    }

    private static void ScavengeCache()
    {
        if (Cache.Count <= 256) return;
        foreach (var stale in Cache.Where(entry => !entry.Value.TryGetTarget(out _)).Take(64))
            Cache.TryRemove(stale.Key, out _);
    }
}
