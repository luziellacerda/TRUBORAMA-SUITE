using System.IO;
using System.Security;
using System.Text.Json;

namespace Turborama.UiPreview;

internal static class LocalAssetPolicy
{
    private const int MaximumRelativePathLength = 240;

    public static string NormalizeBaseDirectory(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var fullPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        if (!Path.IsPathFullyQualified(fullPath)
            || IsUnc(fullPath)
            || !Directory.Exists(fullPath))
            throw new SecurityException("Invalid local package directory.");

        var root = Path.GetPathRoot(fullPath);
        if (string.IsNullOrWhiteSpace(root)
            || new DriveInfo(root).DriveType != DriveType.Fixed)
            throw new SecurityException("Package must be on a local fixed volume.");

        ValidateDirectoryChain(fullPath);
        return fullPath;
    }

    public static string ResolveAssetFile(
        string baseDirectory,
        string relativePath,
        string requiredPrefix,
        IReadOnlySet<string> allowedExtensions,
        long maximumBytes)
    {
        var normalizedBase = NormalizeBaseDirectory(baseDirectory);
        if (!relativePath.StartsWith(requiredPrefix, StringComparison.Ordinal))
            throw new SecurityException("Asset is outside its approved directory.");

        var fullPath = ResolvePackageFile(
            normalizedBase,
            relativePath,
            maximumBytes);
        var extension = Path.GetExtension(relativePath);
        if (!allowedExtensions.Contains(extension))
            throw new SecurityException("Asset extension is not approved.");
        return fullPath;
    }

    public static string ResolvePackageFile(
        string baseDirectory,
        string relativePath,
        long maximumBytes)
    {
        var normalizedBase = NormalizeBaseDirectory(baseDirectory);
        ValidateCanonicalRelativePath(relativePath);

        var candidate = Path.GetFullPath(Path.Combine(
            normalizedBase,
            relativePath.Replace('/', Path.DirectorySeparatorChar)));
        var expectedPrefix = normalizedBase + Path.DirectorySeparatorChar;
        if (!candidate.StartsWith(expectedPrefix, StringComparison.OrdinalIgnoreCase))
            throw new SecurityException("Path escapes the package directory.");

        var roundTrip = Path.GetRelativePath(normalizedBase, candidate)
            .Replace(Path.DirectorySeparatorChar, '/');
        if (!roundTrip.Equals(relativePath, StringComparison.Ordinal))
            throw new SecurityException("Path is not canonical.");

        var info = new FileInfo(candidate);
        if (!info.Exists
            || info.Length is <= 0
            || info.Length > maximumBytes
            || (info.Attributes & FileAttributes.ReparsePoint) != 0)
            throw new SecurityException("Local file is unavailable.");

        ValidateParentsWithinBase(normalizedBase, info.Directory);
        return candidate;
    }

    public static IReadOnlyList<string> EnumeratePackageFiles(
        string baseDirectory,
        int maximumFiles)
    {
        var normalizedBase = NormalizeBaseDirectory(baseDirectory);
        var files = new List<string>();
        var pending = new Stack<DirectoryInfo>();
        pending.Push(new DirectoryInfo(normalizedBase));
        while (pending.Count != 0)
        {
            var directory = pending.Pop();
            foreach (var childDirectory in directory.EnumerateDirectories())
            {
                if ((childDirectory.Attributes & FileAttributes.ReparsePoint) != 0)
                    throw new SecurityException("Reparse directories are not approved.");
                pending.Push(childDirectory);
            }

            foreach (var file in directory.EnumerateFiles())
            {
                if ((file.Attributes & FileAttributes.ReparsePoint) != 0)
                    throw new SecurityException("Reparse files are not approved.");
                files.Add(file.FullName);
                if (files.Count > maximumFiles)
                    throw new SecurityException("Package contains too many files.");
            }
        }
        return files;
    }

    public static byte[] ReadBoundedFile(string path, int maximumBytes)
    {
        var info = new FileInfo(path);
        if (!info.Exists
            || info.Length is <= 0
            || info.Length > maximumBytes
            || (info.Attributes & FileAttributes.ReparsePoint) != 0)
            throw new IOException("File is unavailable or outside its size limit.");

        var buffer = GC.AllocateUninitializedArray<byte>(checked((int)info.Length));
        try
        {
            using var stream = new FileStream(
                info.FullName,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 64 * 1024,
                FileOptions.SequentialScan);
            stream.ReadExactly(buffer);
            if (stream.ReadByte() != -1)
                throw new IOException("File changed during read.");
            return buffer;
        }
        catch
        {
            Array.Clear(buffer);
            throw;
        }
    }

    private static void ValidateCanonicalRelativePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)
            || path.Length > MaximumRelativePathLength
            || Path.IsPathFullyQualified(path)
            || path[0] == '/'
            || path[^1] == '/'
            || path.Contains('\\')
            || path.Contains(':')
            || path.Contains("//", StringComparison.Ordinal)
            || path.Any(char.IsControl))
            throw new SecurityException("Relative path is not canonical.");

        foreach (var segment in path.Split('/'))
        {
            if (segment.Length == 0
                || segment is "." or ".."
                || segment.EndsWith(' ')
                || segment.EndsWith('.'))
                throw new SecurityException("Relative path segment is not canonical.");
        }
    }

    private static bool IsUnc(string path)
        => path.StartsWith("\\\\", StringComparison.Ordinal)
           || (Uri.TryCreate(path, UriKind.Absolute, out var uri) && uri.IsUnc);

    private static void ValidateDirectoryChain(string fullPath)
    {
        DirectoryInfo? current = new(fullPath);
        while (current is not null)
        {
            if ((current.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new SecurityException("Reparse directories are not approved.");
            current = current.Parent;
        }
    }

    private static void ValidateParentsWithinBase(
        string normalizedBase,
        DirectoryInfo? directory)
    {
        while (directory is not null
               && directory.FullName.StartsWith(
                   normalizedBase,
                   StringComparison.OrdinalIgnoreCase))
        {
            if ((directory.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new SecurityException("Reparse directories are not approved.");
            if (directory.FullName.Equals(normalizedBase, StringComparison.OrdinalIgnoreCase))
                return;
            directory = directory.Parent;
        }
        throw new SecurityException("File parent is outside the package directory.");
    }
}

internal static class StrictJson
{
    public static JsonDocument Parse(ReadOnlyMemory<byte> bytes, int maximumDepth)
    {
        var document = JsonDocument.Parse(bytes, new JsonDocumentOptions
        {
            AllowTrailingCommas = false,
            CommentHandling = JsonCommentHandling.Disallow,
            MaxDepth = maximumDepth
        });
        try
        {
            ValidateNoDuplicateProperties(document.RootElement);
            return document;
        }
        catch
        {
            document.Dispose();
            throw;
        }
    }

    public static void RequireExactMembers(JsonElement element, params string[] names)
    {
        if (element.ValueKind != JsonValueKind.Object)
            throw new JsonException("Object expected.");
        var remaining = new HashSet<string>(names, StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
        {
            if (!remaining.Remove(property.Name))
                throw new JsonException("Unexpected or duplicate member.");
        }
        if (remaining.Count != 0)
            throw new JsonException("Required member is missing.");
    }

    private static void ValidateNoDuplicateProperties(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
            {
                var names = new HashSet<string>(StringComparer.Ordinal);
                foreach (var property in element.EnumerateObject())
                {
                    if (!names.Add(property.Name))
                        throw new JsonException("Duplicate member.");
                    ValidateNoDuplicateProperties(property.Value);
                }
                break;
            }
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                    ValidateNoDuplicateProperties(item);
                break;
        }
    }
}
