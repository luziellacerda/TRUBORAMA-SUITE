using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace TurboBoxManager.Catalog;

internal sealed record CatalogGamePackageOrganizationResult(
    string InstalledPath,
    string MarkerDirectory);

internal static class CatalogGamePackageOrganizer
{
    internal const string ReceiptFolderName = ".turborama-installed";
    internal const string RecoveryFolderName = ".turborama-recovery";
    private const int CompletionMarkerSchemaVersion = 2;
    internal static readonly object LibraryMutationGate = new();
    private static readonly TimeSpan LibraryMutationTimeout = TimeSpan.FromMinutes(5);
    private static readonly StringComparison PathComparison = OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase
        : StringComparison.Ordinal;
    private static readonly StringComparer PathComparer = OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;
    private static readonly JsonSerializerOptions MarkerJsonOptions = new()
    {
        WriteIndented = true
    };

    internal static CatalogGamePackageOrganizationResult Organize(
        string extractedPath,
        string gameLibraryRoot,
        CatalogArtifactDescriptor expectedArtifact,
        string expectedCategory,
        string expectedItemId)
    {
        ArgumentNullException.ThrowIfNull(expectedArtifact);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedCategory);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedItemId);
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(gameLibraryRoot));
        var extracted = Path.TrimEndingDirectorySeparator(Path.GetFullPath(extractedPath));
        var expectedExtracted = Path.TrimEndingDirectorySeparator(
            CatalogArchiveExtractor.BuildGameDestinationPath(
                root,
                expectedCategory,
                expectedItemId));
        if (!extracted.Equals(expectedExtracted, PathComparison))
            throw new InvalidDataException(
                "A pasta extraída não corresponde ao destino determinístico do item autorizado.");
        return WithLibraryMutationLock(
            root,
            () => OrganizeLocked(
                extracted,
                root,
                expectedArtifact,
                expectedCategory,
                expectedItemId));
    }

    internal static T WithLibraryMutationLock<T>(
        string gameLibraryRoot,
        Func<T> action)
    {
        ArgumentNullException.ThrowIfNull(action);
        var canonicalRoot = PathIdentity.Canonicalize(gameLibraryRoot);
        var mutexName = @"Local\Turborama-Roms-" + Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(canonicalRoot.ToUpperInvariant())));

        lock (LibraryMutationGate)
        {
            using var mutex = new Mutex(initiallyOwned: false, mutexName);
            var mutexAcquired = false;
            try
            {
                try
                {
                    mutexAcquired = mutex.WaitOne(LibraryMutationTimeout);
                }
                catch (AbandonedMutexException)
                {
                    mutexAcquired = true;
                }

                if (!mutexAcquired)
                    throw new TimeoutException(
                        "A pasta mestre de ROMs permaneceu ocupada por mais de cinco minutos.");
                return action();
            }
            finally
            {
                if (mutexAcquired) mutex.ReleaseMutex();
            }
        }
    }

    internal static string MovePartialExtractionToRecovery(
        string extractedPath,
        string gameLibraryRoot,
        string expectedCategory,
        string expectedItemId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedCategory);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedItemId);
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(gameLibraryRoot));
        var extracted = Path.TrimEndingDirectorySeparator(Path.GetFullPath(extractedPath));
        var expectedExtracted = Path.TrimEndingDirectorySeparator(
            CatalogArchiveExtractor.BuildGameDestinationPath(
                root,
                expectedCategory,
                expectedItemId));
        if (!extracted.Equals(expectedExtracted, PathComparison))
            throw new InvalidDataException(
                "A pasta parcial não corresponde ao destino determinístico do item autorizado.");

        return WithLibraryMutationLock(root, () =>
        {
            if (!Directory.Exists(extracted))
                throw new DirectoryNotFoundException(
                    "A pasta parcial desapareceu antes de ser preservada.");
            EnsureWithin(extracted, root);
            RejectReparsePoint(root, "A pasta mestre de ROMs");
            RejectReparsePoint(extracted, "A pasta parcial");
            EnsureNoReparseTree(extracted);

            var recoveryRoot = Path.GetFullPath(Path.Combine(root, RecoveryFolderName));
            EnsureWithin(recoveryRoot, root);
            var recoveryPath = Path.GetFullPath(Path.Combine(
                recoveryRoot,
                $"{Path.GetFileName(extracted)}-{Guid.NewGuid():N}"));
            EnsureWithin(recoveryPath, recoveryRoot);

            using var rootTree = PathIdentity.OpenDirectoryTree(root);
            using var recoveryTree = PathIdentity.OpenDirectoryTree(
                recoveryRoot,
                createIfMissing: true,
                privateLeaf: true);
            using var extractedTree = PathIdentity.OpenDirectoryTree(
                extracted,
                leafDeleteAccess: true);
            rootTree.Revalidate();
            recoveryTree.Revalidate();
            extractedTree.Revalidate();
            if (Directory.Exists(recoveryPath) || File.Exists(recoveryPath))
                throw new IOException("O destino exclusivo do backup de recuperação já existe.");

            var extractedHandle = extractedTree.AnchorHandle;
            var extractedIdentity = PathIdentity.CaptureDirectoryIdentity(
                extractedHandle,
                extracted);
            var recoveryIdentity = PathIdentity.RenameByHandle(
                extractedHandle,
                extractedIdentity,
                recoveryTree.AnchorHandle,
                recoveryRoot,
                Path.GetFileName(recoveryPath),
                replaceIfExists: false);
            extractedTree.ReleaseDirectoryAfterRename(extracted);
            using var preservedTree = PathIdentity.OpenDirectoryTree(recoveryPath);
            var preservedIdentity = PathIdentity.CaptureDirectoryIdentity(
                preservedTree.AnchorHandle,
                recoveryPath);
            if (!preservedIdentity.SameObject(recoveryIdentity))
                throw new IOException("O backup de recuperação perdeu sua identidade física.");
            preservedTree.Revalidate();
            recoveryTree.Revalidate();
            rootTree.Revalidate();
            return recoveryPath;
        });
    }

    internal static bool DeleteRecoveryBackup(
        string recoveryPath,
        string gameLibraryRoot)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(gameLibraryRoot));
        var recoveryRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.Combine(
            root,
            RecoveryFolderName)));
        var canonicalRecovery = Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(recoveryPath));
        var recoveryParent = Path.GetDirectoryName(canonicalRecovery)
                             ?? throw new InvalidDataException(
                                 "O backup de recuperação não possui diretório-pai.");
        if (!recoveryParent.Equals(recoveryRoot, PathComparison))
            throw new InvalidDataException(
                "O backup solicitado não é filho direto da área de recuperação.");

        return WithLibraryMutationLock(root, () =>
        {
            if (File.Exists(canonicalRecovery))
                throw new InvalidDataException(
                    "Há um arquivo onde deveria existir o backup de recuperação.");
            return PathIdentity.DeleteDirectoryTreeExact(
                canonicalRecovery,
                recoveryRoot);
        });
    }

    private static CatalogGamePackageOrganizationResult OrganizeLocked(
        string extractedPath,
        string gameLibraryRoot,
        CatalogArtifactDescriptor expectedArtifact,
        string expectedCategory,
        string expectedItemId)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(gameLibraryRoot));
        var extracted = Path.TrimEndingDirectorySeparator(Path.GetFullPath(extractedPath));
        EnsureWithin(extracted, root);
        var packagedRoms = Path.GetFullPath(Path.Combine(extracted, "sistema", "roms"));
        if (!Directory.Exists(packagedRoms))
            return new(extracted, extracted);
        EnsureWithin(packagedRoms, extracted);
        RejectReparsePoint(root, "A pasta mestre de ROMs");
        RejectReparsePoint(extracted, "A pasta extraída");
        RejectReparsePoint(packagedRoms, "A pasta sistema\\roms");
        EnsureNoReparseTree(extracted);

        using var rootTree = PathIdentity.OpenDirectoryTree(root);
        using var extractedTree = PathIdentity.OpenDirectoryTree(extracted);
        var markerSource = Path.Combine(
            extracted,
            CatalogArchiveExtractor.CompletionMarkerFileName);
        var plan = BuildPublicationPlan(
            markerSource,
            extracted,
            root,
            expectedArtifact,
            expectedCategory,
            expectedItemId);
        PreflightPublication(plan, root);

        // Vídeos não integram a instalação final. O arquivo compactado continua
        // preservado até todo o fluxo confirmar a conclusão.
        foreach (var videos in Directory.EnumerateDirectories(
                     packagedRoms,
                     "videos",
                     SearchOption.AllDirectories).OrderByDescending(path => path.Length))
        {
            EnsureWithin(videos, packagedRoms);
            _ = PathIdentity.DeleteDirectoryTreeExact(videos, packagedRoms);
        }

        foreach (var entry in plan.Entries)
        {
            rootTree.Revalidate();
            extractedTree.Revalidate();
            if (File.Exists(entry.DestinationPath))
            {
                ValidateExistingFile(entry.DestinationPath, entry.Length, entry.Sha256);
                continue;
            }
            if (!File.Exists(entry.SourcePath))
                throw new InvalidDataException(
                    $"O pacote perdeu o arquivo '{entry.OriginalRelativePath}' antes da organização.");

            var sourceParent = Path.GetDirectoryName(entry.SourcePath)
                               ?? throw new InvalidDataException(
                                   "Um arquivo do pacote não possui diretório-pai.");
            var destinationParent = Path.GetDirectoryName(entry.DestinationPath)
                                    ?? throw new InvalidDataException(
                                        "Um arquivo de destino não possui diretório-pai.");
            using var sourceParentTree = PathIdentity.OpenDirectoryTree(sourceParent);
            using var destinationParentTree = PathIdentity.OpenDirectoryTree(
                destinationParent,
                createIfMissing: true);
            rootTree.Revalidate();
            extractedTree.Revalidate();
            sourceParentTree.Revalidate();
            destinationParentTree.Revalidate();
            if (Directory.Exists(entry.DestinationPath))
                throw new IOException(
                    $"Há uma pasta onde deveria ser publicado o arquivo '{entry.DestinationPath}'.");
            if (File.Exists(entry.DestinationPath))
            {
                ValidateExistingFile(entry.DestinationPath, entry.Length, entry.Sha256);
                continue;
            }
            if (!File.Exists(entry.SourcePath))
                throw new InvalidDataException(
                    $"O pacote perdeu o arquivo '{entry.OriginalRelativePath}' antes da organização.");
            File.Move(entry.SourcePath, entry.DestinationPath, overwrite: false);
            sourceParentTree.Revalidate();
            destinationParentTree.Revalidate();
            rootTree.Revalidate();
            extractedTree.Revalidate();
        }

        foreach (var entry in plan.Entries)
            ValidateExistingFile(entry.DestinationPath, entry.Length, entry.Sha256);

        var packageReadme = Path.Combine(extracted, "leia-me.txt");
        if (File.Exists(packageReadme))
            File.Copy(
                packageReadme,
                Path.Combine(root, "LEIA-ME-TURBORAMA.txt"),
                overwrite: true);

        var receiptRoot = Path.Combine(root, ReceiptFolderName);
        if (!Directory.Exists(receiptRoot)) Directory.CreateDirectory(receiptRoot);
        RejectReparsePoint(receiptRoot, "A pasta de comprovantes");
        var markerDirectory = BuildMarkerDirectory(root, Path.GetFileName(extracted));
        var pendingMarkerDirectory = Path.Combine(
            receiptRoot,
            $".pending-{Path.GetFileName(extracted)}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(pendingMarkerDirectory);
        try
        {
            File.WriteAllBytes(
                Path.Combine(
                    pendingMarkerDirectory,
                    CatalogArchiveExtractor.CompletionMarkerFileName),
                plan.RewrittenMarker);
            if (Directory.Exists(markerDirectory))
                _ = PathIdentity.DeleteDirectoryTreeExact(markerDirectory, receiptRoot);
            Directory.Move(pendingMarkerDirectory, markerDirectory);
        }
        finally
        {
            if (Directory.Exists(pendingMarkerDirectory))
                _ = PathIdentity.DeleteDirectoryTreeExact(pendingMarkerDirectory, receiptRoot);
        }

        rootTree.Revalidate();
        extractedTree.Revalidate();
        extractedTree.Dispose();
        _ = PathIdentity.DeleteDirectoryTreeExact(extracted, root);
        return new(root, markerDirectory);
    }

    internal static string BuildMarkerDirectory(string gameLibraryRoot, string stableDirectoryName) =>
        Path.GetFullPath(Path.Combine(
            gameLibraryRoot,
            ReceiptFolderName,
            Path.GetFileName(stableDirectoryName)));

    private static PublicationPlan BuildPublicationPlan(
        string markerPath,
        string extracted,
        string root,
        CatalogArtifactDescriptor expectedArtifact,
        string expectedCategory,
        string expectedItemId)
    {
        var expectedMarkerCategory = Path.GetFileName(
            CatalogArchiveExtractor.BuildCategoryDestinationPath(root, expectedCategory));
        _ = CatalogArchiveExtractor.BuildGameDestinationPath(
            root,
            expectedCategory,
            expectedItemId);
        var expectedStableItemId = expectedItemId.Trim().ToLowerInvariant();
        try
        {
            var marker = JsonNode.Parse(File.ReadAllText(markerPath))?.AsObject()
                         ?? throw new InvalidDataException("O comprovante da extração é inválido.");
            if (marker["SchemaVersion"]?.GetValue<int>() != CompletionMarkerSchemaVersion
                || marker["ArchiveLength"]?.GetValue<long>() != expectedArtifact.ContentLength
                || !string.Equals(
                    marker["ArchiveSha256"]?.GetValue<string>(),
                    expectedArtifact.Sha256,
                    StringComparison.Ordinal)
                || !string.Equals(
                    marker["ManifestIdentity"]?.GetValue<string>(),
                    expectedArtifact.ManifestIdentity,
                    StringComparison.Ordinal)
                || !string.Equals(
                    marker["ArtifactId"]?.GetValue<string>(),
                    expectedArtifact.ArtifactId,
                    StringComparison.Ordinal)
                || marker["ArtifactVersion"]?.GetValue<int>() != expectedArtifact.ArtifactVersion
                || !string.Equals(
                    marker["Category"]?.GetValue<string>(),
                    expectedMarkerCategory,
                    StringComparison.Ordinal)
                || !string.Equals(
                    marker["StableItemId"]?.GetValue<string>(),
                    expectedStableItemId,
                    StringComparison.Ordinal))
                throw new InvalidDataException(
                    "O comprovante da extração não corresponde ao artefato, categoria ou item autorizados.");
            var inventory = marker["Inventory"]?.AsArray()
                            ?? throw new InvalidDataException("O inventário da extração está ausente.");
            var rewrittenItems = new List<JsonObject>();
            var entries = new List<PublicationEntry>();
            var destinations = new HashSet<string>(PathComparer);
            foreach (var node in inventory)
            {
                var item = node?.DeepClone().AsObject()
                           ?? throw new InvalidDataException("O inventário contém uma entrada inválida.");
                var original = NormalizeInventoryPath(
                    item["RelativePath"]?.GetValue<string>());
                var mapped = MapPackageRomPath(original);
                if (mapped is null) continue;
                var length = item["Length"]?.GetValue<long>() ?? -1;
                var sha256 = item["Sha256"]?.GetValue<string>() ?? string.Empty;
                if (length < 0 || !IsCanonicalSha256(sha256))
                    throw new InvalidDataException("O inventário contém tamanho ou SHA-256 inválido.");

                var source = Path.GetFullPath(Path.Combine(
                    extracted,
                    original.Replace('/', Path.DirectorySeparatorChar)));
                var destination = Path.GetFullPath(Path.Combine(
                    root,
                    mapped.Replace('/', Path.DirectorySeparatorChar)));
                EnsureWithin(source, extracted);
                EnsureWithin(destination, root);
                if (destination.Equals(extracted, PathComparison)
                    || destination.StartsWith(
                        extracted + Path.DirectorySeparatorChar,
                        PathComparison))
                    throw new InvalidDataException(
                        "Um destino de publicação ficou dentro do invólucro temporário do pacote.");
                if (!destinations.Add(destination))
                    throw new InvalidDataException("O pacote contém dois arquivos para o mesmo destino.");
                item["RelativePath"] = mapped;
                rewrittenItems.Add(item);
                entries.Add(new(original, source, destination, length, sha256));
            }
            if (entries.Count == 0)
                throw new InvalidDataException("A pasta sistema\\roms não contém jogos publicáveis.");

            rewrittenItems.Sort((left, right) => string.CompareOrdinal(
                left["RelativePath"]?.GetValue<string>(),
                right["RelativePath"]?.GetValue<string>()));
            marker["Inventory"] = new JsonArray(
                rewrittenItems.Select(item => (JsonNode)item).ToArray());
            return new(entries, JsonSerializer.SerializeToUtf8Bytes(marker, MarkerJsonOptions));
        }
        catch (Exception exception) when (exception is JsonException
                                           or InvalidOperationException
                                           or FormatException
                                           or OverflowException)
        {
            throw new InvalidDataException(
                "O comprovante da extração contém JSON inválido.",
                exception);
        }
    }

    private static void PreflightPublication(PublicationPlan plan, string root)
    {
        foreach (var entry in plan.Entries)
        {
            if (Directory.Exists(entry.DestinationPath))
                throw new IOException(
                    $"Há uma pasta onde deveria ser publicado o arquivo '{entry.DestinationPath}'.");
            if (!File.Exists(entry.SourcePath) && !File.Exists(entry.DestinationPath))
                throw new InvalidDataException(
                    $"O pacote perdeu o arquivo '{entry.OriginalRelativePath}'.");
            if (File.Exists(entry.SourcePath))
                RejectReparsePoint(entry.SourcePath, "Um arquivo do pacote");
            ValidateDestinationParent(root, Path.GetDirectoryName(entry.DestinationPath)!);
            if (!File.Exists(entry.DestinationPath)) continue;
            RejectReparsePoint(entry.DestinationPath, "Um arquivo já instalado");
            ValidateExistingFile(entry.DestinationPath, entry.Length, entry.Sha256);
        }
    }

    private static void ValidateExistingFile(string path, long length, string expectedSha256)
    {
        using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length != length
            || !Convert.ToHexString(SHA256.HashData(stream))
                .Equals(expectedSha256, StringComparison.OrdinalIgnoreCase))
            throw new IOException(
                $"Já existe outro arquivo em '{path}'. Ele foi preservado e não será substituído.");
    }

    private static void ValidateDestinationParent(string root, string parent)
    {
        EnsureWithin(parent, root);
        var relative = Path.GetRelativePath(root, parent);
        if (relative.Equals(".", StringComparison.Ordinal)) return;
        var current = root;
        foreach (var segment in relative.Split(
                     Path.DirectorySeparatorChar,
                     StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            if (File.Exists(current))
                throw new IOException($"Há um arquivo onde deveria existir a pasta '{current}'.");
            if (!Directory.Exists(current)) return;
            RejectReparsePoint(current, "Uma pasta de destino");
        }
    }

    private static string? MapPackageRomPath(string normalized)
    {
        const string prefix = "sistema/roms/";
        if (!normalized.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return null;
        var mapped = normalized[prefix.Length..];
        if (mapped.Length == 0
            || mapped.Split('/', StringSplitOptions.RemoveEmptyEntries)
                .Any(segment => segment.Equals("videos", StringComparison.OrdinalIgnoreCase)))
            return null;
        return mapped;
    }

    private static string NormalizeInventoryPath(string? relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
            throw new InvalidDataException("O inventário contém um caminho vazio.");
        var normalized = relativePath.Replace('\\', '/');
        if (Path.IsPathRooted(normalized)
            || normalized.Split('/').Any(segment => segment is "" or "." or ".."))
            throw new InvalidDataException("O inventário contém um caminho inseguro.");
        return normalized;
    }

    private static bool IsCanonicalSha256(string value) =>
        value.Length == 64
        && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static void EnsureNoReparseTree(string root)
    {
        foreach (var entry in Directory.EnumerateFileSystemEntries(root, "*", SearchOption.AllDirectories))
            RejectReparsePoint(entry, "O pacote");
    }

    private static void RejectReparsePoint(string path, string subject)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException($"{subject} contém um redirecionamento inseguro.");
    }

    private static void EnsureWithin(string path, string root)
    {
        var canonicalPath = Path.GetFullPath(path);
        var canonicalRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        if (!canonicalPath.StartsWith(canonicalRoot + Path.DirectorySeparatorChar, PathComparison))
            throw new InvalidDataException("A organização do pacote saiu da pasta TruboRoms\\roms.");
    }

    private sealed record PublicationPlan(
        IReadOnlyList<PublicationEntry> Entries,
        byte[] RewrittenMarker);

    private sealed record PublicationEntry(
        string OriginalRelativePath,
        string SourcePath,
        string DestinationPath,
        long Length,
        string Sha256);
}
