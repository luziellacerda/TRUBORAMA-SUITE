namespace TurboBoxManager.Catalog;

/// <summary>Only applies to entries inside the downloaded archive, never application assets.</summary>
internal static class CatalogPackageMediaPolicy
{
    internal static bool IsImagesPath(string relativePath, bool isDirectory = false)
    {
        var segments = relativePath.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        var directoryCount = isDirectory ? segments.Length : segments.Length - 1;
        for (var index = 0; index < directoryCount; index++)
            if (segments[index].Equals("images", StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }
}
