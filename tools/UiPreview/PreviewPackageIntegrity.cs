using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;

namespace Turborama.UiPreview;

internal static class PreviewPackageIntegrity
{
    public const string ManifestFileName = "ui-preview-manifest.json";
    public const string Marker = "LOCAL-ADMIN-PREVIEW-NOT-FOR-DISTRIBUTION";
    private const int MaximumManifestBytes = 4 * 1024 * 1024;
    private const int MaximumFiles = 2_500;
    private const long MaximumFileBytes = 1024L * 1024 * 1024;
    private const long MaximumPackageBytes = 2L * 1024 * 1024 * 1024;

    public static Task VerifyAsync(
        string baseDirectory,
        string expectedManifestSha256,
        string expectedCommit,
        DateTimeOffset expectedExpiryUtc)
        => Task.Run(() => Verify(
            baseDirectory,
            expectedManifestSha256,
            expectedCommit,
            expectedExpiryUtc));

    internal static void Verify(
        string baseDirectory,
        string expectedManifestSha256,
        string expectedCommit,
        DateTimeOffset expectedExpiryUtc)
    {
        if (!PreviewBuildInfo.IsCanonicalCommit(expectedCommit))
            throw new InvalidDataException("Invalid build identity.");

        var normalizedBase = LocalAssetPolicy.NormalizeBaseDirectory(baseDirectory);
        var manifestPath = LocalAssetPolicy.ResolvePackageFile(
            normalizedBase,
            ManifestFileName,
            MaximumManifestBytes);
        var manifestBytes = LocalAssetPolicy.ReadBoundedFile(
            manifestPath,
            MaximumManifestBytes);
        byte[] expectedManifestHash = [];
        byte[] actualManifestHash = [];
        try
        {
            expectedManifestHash = ParseSha256(expectedManifestSha256);
            actualManifestHash = SHA256.HashData(manifestBytes);
            if (!CryptographicOperations.FixedTimeEquals(
                    expectedManifestHash,
                    actualManifestHash))
                throw new InvalidDataException("Manifest hash mismatch.");

            using var document = StrictJson.Parse(manifestBytes, maximumDepth: 8);
            var root = document.RootElement;
            StrictJson.RequireExactMembers(
                root,
                "schemaVersion",
                "marker",
                "commit",
                "expiresAtUtc",
                "files");
            if (root.GetProperty("schemaVersion").GetInt32() != 1
                || !string.Equals(
                    root.GetProperty("marker").GetString(),
                    Marker,
                    StringComparison.Ordinal)
                || !string.Equals(
                    root.GetProperty("commit").GetString(),
                    expectedCommit,
                    StringComparison.Ordinal)
                || ParseCanonicalUtc(root.GetProperty("expiresAtUtc").GetString())
                    != expectedExpiryUtc.ToUniversalTime())
                throw new InvalidDataException("Manifest identity mismatch.");

            var filesElement = root.GetProperty("files");
            if (filesElement.ValueKind != JsonValueKind.Array
                || filesElement.GetArrayLength() is <= 0 or > MaximumFiles)
                throw new InvalidDataException("Manifest file list is invalid.");

            var declaredFiles = new Dictionary<string, ManifestFile>(
                StringComparer.OrdinalIgnoreCase);
            long totalBytes = 0;
            foreach (var fileElement in filesElement.EnumerateArray())
            {
                StrictJson.RequireExactMembers(
                    fileElement,
                    "path",
                    "bytes",
                    "sha256");
                var relativePath = fileElement.GetProperty("path").GetString()
                                   ?? throw new InvalidDataException("Missing file path.");
                if (relativePath.Equals(ManifestFileName, StringComparison.OrdinalIgnoreCase)
                    || relativePath.Equals(
                        PreviewCredentialVerifier.CredentialFileName,
                        StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Manifest contains a reserved file.");

                var length = fileElement.GetProperty("bytes").GetInt64();
                if (length is <= 0 or > MaximumFileBytes)
                    throw new InvalidDataException("Manifest file length is invalid.");
                totalBytes = checked(totalBytes + length);
                if (totalBytes > MaximumPackageBytes)
                    throw new InvalidDataException("Package is too large.");

                var hash = fileElement.GetProperty("sha256").GetString()
                           ?? throw new InvalidDataException("Missing file hash.");
                var parsedHash = ParseSha256(hash);
                CryptographicOperations.ZeroMemory(parsedHash);
                if (!declaredFiles.TryAdd(
                        relativePath,
                        new ManifestFile(length, hash)))
                    throw new InvalidDataException("Duplicate manifest path.");
            }

            var actualFiles = LocalAssetPolicy.EnumeratePackageFiles(
                    normalizedBase,
                    MaximumFiles + 2)
                .Select(path => new
                {
                    FullPath = path,
                    RelativePath = Path.GetRelativePath(normalizedBase, path)
                        .Replace(Path.DirectorySeparatorChar, '/')
                })
                .Where(file =>
                    !file.RelativePath.Equals(
                        ManifestFileName,
                        StringComparison.OrdinalIgnoreCase)
                    && !file.RelativePath.Equals(
                        PreviewCredentialVerifier.CredentialFileName,
                        StringComparison.OrdinalIgnoreCase))
                .ToArray();
            if (actualFiles.Length != declaredFiles.Count)
                throw new InvalidDataException("Package file set mismatch.");

            foreach (var actualFile in actualFiles)
            {
                if (!declaredFiles.Remove(actualFile.RelativePath, out var declared))
                    throw new InvalidDataException("Undeclared package file.");

                var info = new FileInfo(actualFile.FullPath);
                if (info.Length != declared.Bytes)
                    throw new InvalidDataException("Package file length mismatch.");

                using var stream = new FileStream(
                    actualFile.FullPath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read,
                    bufferSize: 128 * 1024,
                    FileOptions.SequentialScan);
                var actualHash = SHA256.HashData(stream);
                var declaredHash = ParseSha256(declared.Sha256);
                try
                {
                    if (!CryptographicOperations.FixedTimeEquals(
                            actualHash,
                            declaredHash))
                        throw new InvalidDataException("Package file hash mismatch.");
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(actualHash);
                    CryptographicOperations.ZeroMemory(declaredHash);
                }
            }

            if (declaredFiles.Count != 0)
                throw new InvalidDataException("Manifest references missing files.");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(manifestBytes);
            if (expectedManifestHash.Length != 0)
                CryptographicOperations.ZeroMemory(expectedManifestHash);
            if (actualManifestHash.Length != 0)
                CryptographicOperations.ZeroMemory(actualManifestHash);
        }
    }

    private static byte[] ParseSha256(string? value)
    {
        if (value is not { Length: 64 }
            || value.Any(character => character is not (>= '0' and <= '9'
                or >= 'a' and <= 'f')))
            throw new InvalidDataException("SHA-256 is not canonical.");
        return Convert.FromHexString(value);
    }

    private static DateTimeOffset ParseCanonicalUtc(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)
            || !DateTimeOffset.TryParseExact(
                value,
                "O",
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var parsed)
            || !parsed.ToString("O", CultureInfo.InvariantCulture)
                .Equals(value, StringComparison.Ordinal))
            throw new InvalidDataException("Timestamp is not canonical.");
        return parsed;
    }

    private sealed record ManifestFile(long Bytes, string Sha256);
}
