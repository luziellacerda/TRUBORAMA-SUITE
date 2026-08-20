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
    private readonly ConcurrentDictionary<string, string> _cache = new(StringComparer.Ordinal);

    public CatalogImageResolver(string manifestPath, string? fallbackImage)
    {
        var fullManifestPath = Path.GetFullPath(manifestPath);
        _manifestDirectory = Path.GetDirectoryName(fullManifestPath)
            ?? throw new ArgumentException("O manifesto precisa ter uma pasta válida.", nameof(manifestPath));
        _allowedAssetRoot = Directory.GetParent(_manifestDirectory)?.FullName ?? _manifestDirectory;
        FallbackImageSource = ResolveLocal(fallbackImage) ?? string.Empty;
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
