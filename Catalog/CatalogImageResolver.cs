using System.Collections.Concurrent;
using System.IO;

namespace TurboBoxManager.Catalog;

/// <summary>
/// Resolves manifest image references without downloading or decoding them.
/// Local references are constrained to the Assets directory, and results are
/// cached so large catalogs do not repeat path and URI validation.
/// </summary>
public sealed class CatalogImageResolver
{
    private readonly string _manifestDirectory;
    private readonly string _allowedAssetRoot;
    private readonly bool _usePackResources;
    private readonly ConcurrentDictionary<string, string> _cache = new(StringComparer.Ordinal);

    public CatalogImageResolver(
        string manifestPath,
        string? fallbackImage,
        bool usePackResources = false)
    {
        var fullManifestPath = Path.GetFullPath(manifestPath);
        _manifestDirectory = Path.GetDirectoryName(fullManifestPath)
            ?? throw new ArgumentException("O manifesto precisa ter uma pasta válida.", nameof(manifestPath));
        _allowedAssetRoot = Directory.GetParent(_manifestDirectory)?.FullName ?? _manifestDirectory;
        _usePackResources = usePackResources;
        FallbackImageSource = usePackResources
            ? ResolvePackResource(fallbackImage) ?? string.Empty
            : ResolveLocal(fallbackImage) ?? string.Empty;
    }

    public string FallbackImageSource { get; }

    public int CachedReferenceCount => _cache.Count;

    public string Resolve(string? imageReference)
    {
        if (string.IsNullOrWhiteSpace(imageReference)) return FallbackImageSource;
        return _cache.GetOrAdd(imageReference.Trim(), ResolveCore);
    }

    private string ResolveCore(string imageReference)
    {
        if (_usePackResources)
            return ResolvePackResource(imageReference) ?? FallbackImageSource;

        if (Uri.TryCreate(imageReference, UriKind.Absolute, out var absoluteUri))
        {
            if (absoluteUri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
                return absoluteUri.AbsoluteUri;

            if (absoluteUri.IsFile)
                return ResolveLocal(absoluteUri.LocalPath) ?? FallbackImageSource;

            return FallbackImageSource;
        }

        return ResolveLocal(imageReference) ?? FallbackImageSource;
    }

    private static string? ResolvePackResource(string? imageReference)
    {
        if (string.IsNullOrWhiteSpace(imageReference)) return null;
        try
        {
            var normalized = imageReference.Trim().Replace('\\', '/');
            if (normalized.StartsWith("/", StringComparison.Ordinal)
                || Uri.TryCreate(normalized, UriKind.Absolute, out _)
                || normalized.Split('/').Any(segment =>
                    segment.Length == 0 || segment is "." or ".."))
                return null;

            return $"pack://application:,,,/Assets/Catalog/{normalized}";
        }
        catch (Exception exception) when (exception is ArgumentException or UriFormatException)
        {
            return null;
        }
    }

    private string? ResolveLocal(string? imageReference)
    {
        if (string.IsNullOrWhiteSpace(imageReference)) return null;

        try
        {
            var normalizedReference = imageReference.Replace('/', Path.DirectorySeparatorChar);
            var fullPath = Path.IsPathRooted(normalizedReference)
                ? Path.GetFullPath(normalizedReference)
                : Path.GetFullPath(Path.Combine(_manifestDirectory, normalizedReference));

            if (!IsWithinRoot(fullPath, _allowedAssetRoot) || !File.Exists(fullPath)) return null;
            return new Uri(fullPath).AbsoluteUri;
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or NotSupportedException)
        {
            return null;
        }
    }

    private static bool IsWithinRoot(string candidatePath, string rootPath)
    {
        var normalizedRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(rootPath))
                             + Path.DirectorySeparatorChar;
        var normalizedCandidate = Path.GetFullPath(candidatePath);
        return normalizedCandidate.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase);
    }
}
