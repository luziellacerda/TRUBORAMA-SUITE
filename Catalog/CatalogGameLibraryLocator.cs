using System.IO;

namespace TurboBoxManager.Catalog;

public sealed record CatalogGameLibraryDiscovery(
    string? SelectedPath,
    IReadOnlyList<string> Matches,
    bool RequiresUserSelection);

/// <summary>
/// Finds the game library only in explicitly approved, shallow locations.
/// It never recursively enumerates a user folder or a drive.
/// </summary>
public static class CatalogGameLibraryLocator
{
    public static CatalogGameLibraryDiscovery Discover(
        string? persistedPath,
        string installFolder,
        string? documentsFolder = null,
        IEnumerable<string>? readyFixedDriveRoots = null)
    {
        documentsFolder ??= Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        readyFixedDriveRoots ??= GetReadyFixedDriveRoots();

        var candidates = new List<Candidate>();
        AddCandidate(candidates, persistedPath, priority: 0);
        AddChildCandidate(candidates, installFolder, priority: 1);
        AddChildCandidate(candidates, TryGetParent(installFolder), priority: 2);
        AddChildCandidate(candidates, documentsFolder, priority: 3);
        foreach (var driveRoot in readyFixedDriveRoots)
            AddChildCandidate(candidates, driveRoot, priority: 4);

        var matches = candidates
            .Where(candidate => IsExistingNamedLibrary(candidate.Path))
            .GroupBy(candidate => candidate.Path, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.OrderBy(candidate => candidate.Priority).First())
            .OrderBy(candidate => candidate.Priority)
            .ThenBy(candidate => candidate.Path, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (matches.Length == 0)
            return new CatalogGameLibraryDiscovery(null, [], false);
        if (matches.Length == 1)
            return new CatalogGameLibraryDiscovery(matches[0].Path, [matches[0].Path], false);

        // Persisted/install/Documents candidates are explicit user context and
        // therefore outrank generic drive-root matches. Multiple drive matches
        // are never chosen by alphabetical or drive-letter order.
        var preferred = matches.FirstOrDefault(candidate => candidate.Priority < 4);
        return preferred is not null
            ? new CatalogGameLibraryDiscovery(
                preferred.Path,
                matches.Select(candidate => candidate.Path).ToArray(),
                false)
            : new CatalogGameLibraryDiscovery(
                null,
                matches.Select(candidate => candidate.Path).ToArray(),
                true);
    }

    private static string[] GetReadyFixedDriveRoots()
    {
        try
        {
            return DriveInfo.GetDrives()
                .Where(drive => drive.DriveType == DriveType.Fixed && IsReady(drive))
                .Select(drive => drive.RootDirectory.FullName)
                .ToArray();
        }
        catch (Exception exception) when (exception is IOException
                                           or UnauthorizedAccessException
                                           or System.Security.SecurityException)
        {
            return [];
        }
    }

    private static bool IsReady(DriveInfo drive)
    {
        try
        {
            return drive.IsReady;
        }
        catch (Exception exception) when (exception is IOException
                                           or UnauthorizedAccessException
                                           or System.Security.SecurityException)
        {
            return false;
        }
    }

    private static void AddChildCandidate(List<Candidate> candidates, string? parent, int priority)
    {
        if (string.IsNullOrWhiteSpace(parent)) return;
        try
        {
            AddCandidate(
                candidates,
                Path.Combine(Path.GetFullPath(parent), CatalogArchiveExtractor.GameLibraryFolderName),
                priority);
        }
        catch (Exception exception) when (exception is ArgumentException
                                           or NotSupportedException
                                           or PathTooLongException)
        {
        }
    }

    private static void AddCandidate(List<Candidate> candidates, string? path, int priority)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        try
        {
            candidates.Add(new Candidate(
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)),
                priority));
        }
        catch (Exception exception) when (exception is ArgumentException
                                           or NotSupportedException
                                           or PathTooLongException)
        {
        }
    }

    private static string? TryGetParent(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        try
        {
            return Directory.GetParent(Path.GetFullPath(path))?.FullName;
        }
        catch (Exception exception) when (exception is ArgumentException
                                           or NotSupportedException
                                           or PathTooLongException)
        {
            return null;
        }
    }

    private static bool IsExistingNamedLibrary(string path)
    {
        return CatalogArchiveExtractor.IsGameLibraryRoot(path);
    }

    private sealed record Candidate(string Path, int Priority);
}
