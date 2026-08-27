using System.IO;
using System.Windows.Media.Imaging;

namespace Turborama.UiPreview;

internal sealed class PreviewImageCache
{
    private const int Capacity = 32;
    private readonly Dictionary<string, CacheEntry> _entries =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly LinkedList<string> _usage = new();

    public BitmapSource Load(string path)
    {
        if (_entries.TryGetValue(path, out var existing))
        {
            _usage.Remove(existing.Node);
            _usage.AddFirst(existing.Node);
            return existing.Source;
        }

        var source = LoadImage(path);
        var node = _usage.AddFirst(path);
        _entries.Add(path, new CacheEntry(source, node));
        while (_entries.Count > Capacity)
        {
            var last = _usage.Last
                       ?? throw new InvalidOperationException("Invalid image cache state.");
            _usage.RemoveLast();
            _entries.Remove(last.Value);
        }
        return source;
    }

    public void Clear()
    {
        _entries.Clear();
        _usage.Clear();
    }

    private static BitmapImage LoadImage(string path)
    {
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 64 * 1024,
            FileOptions.SequentialScan);
        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.CreateOptions = BitmapCreateOptions.PreservePixelFormat;
        bitmap.DecodePixelWidth = 420;
        bitmap.StreamSource = stream;
        bitmap.EndInit();
        bitmap.Freeze();
        return bitmap;
    }

    private sealed record CacheEntry(BitmapSource Source, LinkedListNode<string> Node);
}
