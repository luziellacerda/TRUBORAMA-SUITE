using System.IO;

namespace TurboBoxManager.Catalog;

internal static class CatalogExtractionCompletionCleanup
{
    internal static bool DeleteArchivePreservingRecoveryMarker(
        string archivePath,
        string downloadRoot,
        string destinationPath)
    {
        var canonicalDestination = PathIdentity.Canonicalize(destinationPath);
        var markerPath = Path.Combine(
            canonicalDestination,
            CatalogArchiveExtractor.CompletionMarkerFileName);

        // The marker is only an untrusted recovery hint. The extractor remains
        // responsible for authenticating the redownloaded archive and validating
        // both the marker inventory and the published tree before recovery.
        using var destinationLease = PathIdentity.OpenDirectoryTree(canonicalDestination);
        using var marker = destinationLease.OpenFile(
            markerPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            4 * 1024,
            FileOptions.SequentialScan);
        var markerIdentity = PathIdentity.CaptureFileIdentity(
            marker.SafeFileHandle,
            markerPath);
        destinationLease.RetainFile(
            marker.SafeFileHandle,
            markerPath,
            markerIdentity);
        destinationLease.Revalidate();

        var archiveDeleted = PathIdentity.DeleteFileExact(archivePath, downloadRoot);
        destinationLease.Revalidate();
        return archiveDeleted;
    }
}
