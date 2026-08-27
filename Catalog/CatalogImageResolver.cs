using System.IO;

namespace TurboBoxManager.Catalog;

/// <summary>
/// Resolves manifest image references without downloading or decoding them.
/// Local references are constrained to the Assets directory and are validated
/// on every resolution so a stale URI never bypasses later path checks.
/// </summary>
public sealed class CatalogImageResolver
{
    private readonly string _manifestDirectory;
    private readonly string _allowedAssetRoot;
    private readonly bool _usePackResources;
    private readonly string? _fallbackImage;

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
        _fallbackImage = fallbackImage;
    }

    public string FallbackImageSource => _usePackResources
        ? ResolvePackResource(_fallbackImage) ?? string.Empty
        : ResolveLocal(_fallbackImage) ?? string.Empty;

    // Kept for source compatibility. This resolver intentionally retains no
    // validated URI because every request must recheck the filesystem.
    public int CachedReferenceCount { get; }

    public string Resolve(string? imageReference)
    {
        if (string.IsNullOrWhiteSpace(imageReference)) return FallbackImageSource;
        return ResolveCore(imageReference.Trim());
    }

    private string ResolveCore(string imageReference)
    {
        if (_usePackResources)
            return ResolvePackResource(imageReference) ?? FallbackImageSource;

        if (Uri.TryCreate(imageReference, UriKind.Absolute, out var absoluteUri))
        {
            if (absoluteUri.IsFile)
                return ResolveLocal(absoluteUri.LocalPath) ?? FallbackImageSource;

            // Catalog artwork is package content, never an ambient network
            // request. Rejecting HTTP(S) prevents a modified visual catalog
            // from becoming a tracking beacon or an unverified image source.
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
            if (normalized.StartsWith('/')
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

            if (!IsWithinRoot(fullPath, _allowedAssetRoot)
                || !File.Exists(fullPath)
                || HasReparsePointInPath(fullPath))
                return null;
            return new Uri(fullPath).AbsoluteUri;
        }
        catch (Exception exception) when (exception is ArgumentException
                                           or IOException
                                           or UnauthorizedAccessException
                                           or System.Security.SecurityException
                                           or NotSupportedException)
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

    private static bool HasReparsePointInPath(string candidatePath)
    {
        var candidate = Path.GetFullPath(candidatePath);
        var root = Path.GetPathRoot(candidate);
        if (string.IsNullOrEmpty(root)) return true;

        var current = root;
        if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) return true;
        foreach (var segment in Path.GetRelativePath(root, candidate).Split(
                     [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                     StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) return true;
        }
        return false;
    }
}
