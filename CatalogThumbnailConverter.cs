using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.Windows.Data;
using System.Windows.Media.Imaging;

namespace TurboBoxManager;

public sealed class CatalogThumbnailConverter : IValueConverter
{
    private static readonly ConcurrentDictionary<string, WeakReference<BitmapSource>> Cache =
        new(StringComparer.Ordinal);

    public object? Convert(
        object value,
        Type targetType,
        object parameter,
        CultureInfo culture)
    {
        if (value is not string source || string.IsNullOrWhiteSpace(source)) return null;
        var decodeWidth = 384;
        if (parameter is string text
            && int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
            decodeWidth = Math.Clamp(parsed, 64, 1024);

        var key = $"{decodeWidth}|{source}";
        if (Cache.TryGetValue(key, out var weak)
            && weak.TryGetTarget(out var existing))
            return existing;

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
            Cache[key] = new WeakReference<BitmapSource>(bitmap);
            if (Cache.Count > 256)
            {
                foreach (var stale in Cache.Where(entry => !entry.Value.TryGetTarget(out _)).Take(64))
                    Cache.TryRemove(stale.Key, out _);
            }
            return bitmap;
        }
        catch (Exception exception) when (exception is IOException
                                           or UnauthorizedAccessException
                                           or NotSupportedException
                                           or FileFormatException
                                           or ArgumentException)
        {
            return null;
        }
    }

    public object ConvertBack(
        object value,
        Type targetType,
        object parameter,
        CultureInfo culture) => Binding.DoNothing;
}
