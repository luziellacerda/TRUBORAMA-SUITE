using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SharpCompress.Archives;
using SharpCompress.Common;
using SharpCompress.Readers;

namespace TurboBoxManager.Catalog;

public enum CatalogArchiveExtractionStatus
{
    Succeeded,
    InsufficientSpace,
    Failed,
    Canceled
}

public sealed record CatalogArchiveExtractionProgress(
    string CurrentEntry,
    int ExtractedFileCount,
    int TotalFileCount,
    long ExtractedBytes,
    long TotalBytes);

public sealed record CatalogArchiveExtractionResult(
    CatalogArchiveExtractionStatus Status,
    string Message,
    string ArchivePath,
    string DestinationPath,
    long RequiredBytes = 0,
    long AvailableBytes = 0,
    int ExtractedFileCount = 0,
    long ExtractedBytes = 0)
{
    public bool Succeeded => Status == CatalogArchiveExtractionStatus.Succeeded;
    public bool NeedsAnotherDrive => Status == CatalogArchiveExtractionStatus.InsufficientSpace;
    public bool CanRetry => Status != CatalogArchiveExtractionStatus.Succeeded;

    // The extractor never deletes, moves or renames the downloaded package.
    public bool ArchivePreserved => File.Exists(ArchivePath);
}

public sealed class CatalogArchiveExtractionOptions
{
    /// <summary>
    /// Free space left untouched after the estimated uncompressed payload.
    /// This prevents an extraction from completely filling a drive.
    /// </summary>
    public long MinimumFreeSpaceReserveBytes { get; init; } = 256L * 1024L * 1024L;

    /// <summary>
    /// Guards the preflight metadata scan against archives with an unreasonable
    /// number of entries.
    /// </summary>
    public int MaximumEntryCount { get; init; } = 100_000;

    /// <summary>
    /// Maximum sum of all declared uncompressed file sizes. Disk free space is
    /// checked separately and must never be the only archive-bomb control.
    /// </summary>
    public long MaximumTotalUncompressedBytes { get; init; } = 256L * 1024L * 1024L * 1024L;

    /// <summary>
    /// Maximum declared and observed size for a single extracted file.
    /// </summary>
    public long MaximumEntryUncompressedBytes { get; init; } = 128L * 1024L * 1024L * 1024L;

    /// <summary>
    /// Maximum uncompressed/compressed ratio. ZIP and RAR entries are checked
    /// individually; every supported format, including solid 7z/RAR packages,
    /// must also provide a verifiable aggregate compressed size.
    /// </summary>
    public double MaximumCompressionRatio { get; init; } = 250d;

    /// <summary>
    /// Maximum number of path segments in an archive entry.
    /// </summary>
    public int MaximumPathDepth { get; init; } = 24;

    /// <summary>Maximum UTF-16 length of one normalized path segment.</summary>
    public int MaximumPathSegmentLength { get; init; } = 180;

    /// <summary>Maximum UTF-16 length of a normalized relative archive path.</summary>
    public int MaximumRelativePathLength { get; init; } = 1_024;

    /// <summary>Maximum UTF-16 length of a fully resolved destination path.</summary>
    public int MaximumDestinationPathLength { get; init; } = 2_048;

    /// <summary>
    /// Monotonic wall-clock budget for planning, extraction and publication.
    /// </summary>
    public TimeSpan MaximumExtractionDuration { get; init; } = TimeSpan.FromHours(6);

    public int CopyBufferSize { get; init; } = 128 * 1024;
}

/// <summary>
/// Extracts ZIP, RAR and 7z packages into a validated library root while
/// preserving the &lt;category&gt;\&lt;item&gt; subpath.
///
/// Every entry is validated before any payload is written. Extraction happens
/// inside a unique staging directory on the destination drive; the completed
/// directory is moved into place only after every entry succeeds. The source
/// archive is opened read-only and is never deleted, moved or renamed.
/// </summary>
public sealed class CatalogArchiveExtractor
{
    public const string LibraryFolderName = "TruboRoms";
    public const string GameLibraryLeafFolderName = "roms";
    public const string GameLibraryFolderName = "TruboRoms\\roms";
    public const string CompletionMarkerFileName = ".turborama-extraction-complete";
    private const int CompletionMarkerSchemaVersion = 2;
    private const int MaximumCompletionMarkerBytes = 64 * 1024 * 1024;

    private static readonly StringComparer FileSystemPathComparer =
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private static readonly HashSet<string> WindowsDeviceNames = new(
        [
            "CON", "PRN", "AUX", "NUL",
            "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
            "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
        ],
        StringComparer.OrdinalIgnoreCase);

    private readonly CatalogArchiveExtractionOptions _options;

    private int MaximumInventoryTreeEntries => (int)Math.Min(
        int.MaxValue,
        (long)_options.MaximumEntryCount * (_options.MaximumPathDepth + 1L));

    public CatalogArchiveExtractor(CatalogArchiveExtractionOptions? options = null)
    {
        _options = options ?? new CatalogArchiveExtractionOptions();
        if (_options.MinimumFreeSpaceReserveBytes < 0)
            throw new ArgumentOutOfRangeException(nameof(options), "A reserva de espaço não pode ser negativa.");
        if (_options.MaximumEntryCount <= 0)
            throw new ArgumentOutOfRangeException(nameof(options), "O limite de entradas deve ser positivo.");
        if (_options.MaximumTotalUncompressedBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(options), "O limite total descompactado deve ser positivo.");
        if (_options.MaximumEntryUncompressedBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(options), "O limite por entrada deve ser positivo.");
        if (!double.IsFinite(_options.MaximumCompressionRatio)
            || _options.MaximumCompressionRatio < 1d)
            throw new ArgumentOutOfRangeException(nameof(options), "A razão máxima de compressão deve ser finita e maior ou igual a 1.");
        if (_options.MaximumPathDepth <= 0)
            throw new ArgumentOutOfRangeException(nameof(options), "A profundidade máxima deve ser positiva.");
        if (_options.MaximumPathSegmentLength is < 1 or > 255)
            throw new ArgumentOutOfRangeException(nameof(options), "O limite de segmento deve ficar entre 1 e 255 caracteres.");
        if (_options.MaximumRelativePathLength < _options.MaximumPathSegmentLength)
            throw new ArgumentOutOfRangeException(nameof(options), "O limite do caminho relativo não pode ser menor que o limite de segmento.");
        if (_options.MaximumDestinationPathLength < _options.MaximumRelativePathLength)
            throw new ArgumentOutOfRangeException(nameof(options), "O limite do caminho final não pode ser menor que o limite relativo.");
        if (_options.MaximumExtractionDuration <= TimeSpan.Zero
            || _options.MaximumExtractionDuration > TimeSpan.FromDays(1))
            throw new ArgumentOutOfRangeException(nameof(options), "O tempo máximo deve ficar entre zero e 24 horas.");
        if (_options.CopyBufferSize < 4 * 1024)
            throw new ArgumentOutOfRangeException(nameof(options), "O buffer deve ter pelo menos 4 KB.");
    }

    public async Task<CatalogArchiveExtractionResult> ExtractAsync(
        string archivePath,
        string baseDirectory,
        string category,
        string item,
        CatalogArtifactDescriptor authorizedArtifact,
        IProgress<CatalogArchiveExtractionProgress>? progress = null,
        bool baseDirectoryIsGameLibrary = false,
        string? itemId = null,
        CancellationToken cancellationToken = default)
    {
        string canonicalArchivePath = string.Empty;
        string destinationPath = string.Empty;
        string? stagingPath = null;
        long requiredBytes = 0;
        long availableBytes = 0;
        long extractedBytes = 0;
        var extractedFileCount = 0;
        var retainedExtractionInventory = new List<ExtractionInventoryEntry>();
        var libraryFolderName = baseDirectoryIsGameLibrary
            ? GameLibraryFolderName
            : LibraryFolderName;
        using var timeoutCancellation = new CancellationTokenSource();
        timeoutCancellation.CancelAfter(_options.MaximumExtractionDuration);
        using var operationCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            timeoutCancellation.Token);
        var operationToken = operationCancellation.Token;
        var deadline = new ExtractionDeadline(_options.MaximumExtractionDuration);

        try
        {
            deadline.ThrowIfExpired(operationToken);
            canonicalArchivePath = RequireExistingArchive(archivePath);
            using var archivePathLease = PathIdentity.OpenDirectoryTree(
                Path.GetDirectoryName(canonicalArchivePath)!);
            await using var archiveStream = OpenArchiveStream(
                canonicalArchivePath,
                archivePathLease);
            var archiveHandleIdentity = PathIdentity.CaptureFileIdentity(
                archiveStream.SafeFileHandle,
                canonicalArchivePath);
            var sourceIdentity = await CaptureAndValidateAuthorizedIdentityAsync(
                canonicalArchivePath,
                archiveStream,
                authorizedArtifact,
                archiveHandleIdentity,
                deadline,
                operationToken);
            deadline.ThrowIfExpired(operationToken);
            var canonicalBase = RequireBaseDirectory(baseDirectory);
            using var destinationTree = PathIdentity.OpenDirectoryTree(canonicalBase);
            var safeCategory = SanitizeDestinationSegment(category, "categoria");
            var stableItemId = ResolveStableItemId(itemId, canonicalArchivePath);
            var safeItem = BuildStableItemDirectoryName(stableItemId);
            var libraryRoot = baseDirectoryIsGameLibrary
                ? RequireGameLibraryRoot(canonicalBase)
                : Path.GetFullPath(Path.Combine(canonicalBase, LibraryFolderName));
            _ = destinationTree.EnsureDirectory(libraryRoot);
            destinationPath = Path.GetFullPath(Path.Combine(libraryRoot, safeCategory, safeItem));
            EnsurePathLength(destinationPath, _options.MaximumDestinationPathLength, "destino final");

            EnsureWithinRoot(
                destinationPath,
                libraryRoot,
                $"O destino calculado saiu da biblioteca {libraryFolderName}.");
            var destinationParent = Path.GetDirectoryName(destinationPath)!;
            _ = destinationTree.EnsureDirectory(destinationParent);
            destinationTree.Revalidate();
            if (IsWithinRoot(canonicalArchivePath, destinationPath))
                throw new InvalidDataException(
                    "O pacote compactado está dentro da pasta que seria substituída. " +
                    "Mova o pacote para Downloads antes de extrair.");

            using var archive = ArchiveFactory.OpenArchive(
                archiveStream,
                new ReaderOptions { LeaveStreamOpen = true });

            EnsureSupportedArchive(archive);
            deadline.ThrowIfExpired(operationToken);
            var plans = BuildAndValidatePlans(archive, deadline, operationToken);
            var totalUncompressedBytes = plans.TotalUncompressedBytes;
            ValidateArchiveSourceIdentity(canonicalArchivePath, archiveStream, sourceIdentity);

            if (Directory.Exists(destinationPath)
                && destinationTree.EnsureDirectory(destinationPath) is not null
                && await IsCompletedExtractionAsync(
                    destinationPath,
                    sourceIdentity,
                    safeCategory,
                    stableItemId,
                    plans,
                    deadline,
                    destinationTree,
                    operationToken))
            {
                return new CatalogArchiveExtractionResult(
                    CatalogArchiveExtractionStatus.Succeeded,
                    "A extração anterior já havia sido concluída e foi recuperada com segurança.",
                    canonicalArchivePath,
                    destinationPath);
            }
            if (Directory.Exists(destinationPath) || File.Exists(destinationPath))
                throw new IOException(
                    "A pasta final deste item já existe. Ela não foi alterada; escolha outro destino ou remova-a manualmente.");
            requiredBytes = checked(totalUncompressedBytes + _options.MinimumFreeSpaceReserveBytes);
            availableBytes = GetAvailableFreeSpace(canonicalBase);

            if (availableBytes < requiredBytes)
            {
                return new CatalogArchiveExtractionResult(
                    CatalogArchiveExtractionStatus.InsufficientSpace,
                    $"Não há espaço suficiente neste disco para a pasta {libraryFolderName}.",
                    canonicalArchivePath,
                    destinationPath,
                    requiredBytes,
                    availableBytes);
            }

            var stagingContainer = Path.GetFullPath(Path.Combine(libraryRoot, ".staging"));
            EnsureWithinRoot(
                stagingContainer,
                libraryRoot,
                $"A área temporária saiu da biblioteca {libraryFolderName}.");
            _ = destinationTree.EnsureDirectory(stagingContainer);
            stagingPath = Path.GetFullPath(Path.Combine(stagingContainer, Guid.NewGuid().ToString("N")));
            EnsureWithinRoot(stagingPath, stagingContainer, "A área temporária calculada é inválida.");
            _ = destinationTree.EnsureDirectory(
                stagingPath,
                privateLeaf: true,
                requireNewLeaf: true,
                leafDeleteAccess: true);

            foreach (var plan in plans)
            {
                deadline.ThrowIfExpired(operationToken);
                var outputPath = Path.GetFullPath(Path.Combine(stagingPath, plan.RelativePath));
                EnsurePathLength(outputPath, _options.MaximumDestinationPathLength, "destino de uma entrada");
                EnsureWithinRoot(outputPath, stagingPath, "Uma entrada do pacote tentou sair da área segura.");

                if (plan.Entry.IsDirectory)
                {
                    _ = destinationTree.EnsureDirectory(outputPath);
                    continue;
                }

                if (plan.Entry.Size != plan.DeclaredSize
                    || plan.Entry.CompressedSize != plan.DeclaredCompressedSize)
                    throw new InvalidDataException(
                        $"Os metadados da entrada '{plan.Entry.Key}' mudaram após o planejamento.");

                var outputDirectory = Path.GetDirectoryName(outputPath)!;
                _ = destinationTree.EnsureDirectory(outputDirectory);
                await using var input = await plan.Entry.OpenEntryStreamAsync(operationToken);
                await using var output = destinationTree.OpenFile(
                    outputPath,
                    FileMode.CreateNew,
                    FileAccess.ReadWrite,
                    FileShare.Read,
                    _options.CopyBufferSize,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);

                const long progressIntervalBytes = 1024L * 1024L;
                long lastReportedEntryBytes = -progressIntervalBytes;
                var copied = await CopyEntryAsync(
                    input,
                    output,
                    plan.DeclaredSize,
                    _options.MaximumEntryUncompressedBytes,
                    _options.CopyBufferSize,
                    deadline,
                    entryBytes =>
                    {
                        var overallBytes = checked(extractedBytes + entryBytes);
                        if (overallBytes > _options.MaximumTotalUncompressedBytes)
                            throw new InvalidDataException(
                                "A extração excedeu o limite total descompactado configurado.");
                        if (progress is null
                            || (entryBytes != plan.DeclaredSize
                                && entryBytes - lastReportedEntryBytes < progressIntervalBytes))
                            return;
                        lastReportedEntryBytes = entryBytes;
                        progress.Report(new CatalogArchiveExtractionProgress(
                            plan.Entry.Key ?? plan.RelativePath,
                            extractedFileCount,
                            plans.FileCount,
                            overallBytes,
                            totalUncompressedBytes));
                    },
                    operationToken);
                extractedBytes = checked(extractedBytes + copied);
                extractedFileCount++;
                output.Flush(flushToDisk: true);
                output.Position = 0;
                var outputHash = Convert.ToHexString(
                        await SHA256.HashDataAsync(output, operationToken))
                    .ToLowerInvariant();
                output.Position = 0;
                var outputIdentity = PathIdentity.CaptureFileIdentity(
                    output.SafeFileHandle,
                    outputPath);
                destinationTree.RetainFile(
                    output.SafeFileHandle,
                    outputPath,
                    outputIdentity);
                retainedExtractionInventory.Add(new ExtractionInventoryEntry(
                    plan.RelativePath.Replace(Path.DirectorySeparatorChar, '/'),
                    copied,
                    outputHash));

                progress?.Report(new CatalogArchiveExtractionProgress(
                    plan.Entry.Key ?? plan.RelativePath,
                    extractedFileCount,
                    plans.FileCount,
                    extractedBytes,
                    totalUncompressedBytes));
            }

            if (extractedBytes != totalUncompressedBytes)
                throw new InvalidDataException(
                    "O tamanho extraído não corresponde ao tamanho declarado pelo pacote.");

            deadline.ThrowIfExpired(operationToken);
            await ValidateAuthorizedArchiveIdentityAsync(
                canonicalArchivePath,
                archiveStream,
                sourceIdentity,
                deadline,
                operationToken);
            EnsureTreeContainsNoReparsePoints(
                stagingPath,
                deadline,
                MaximumInventoryTreeEntries,
                destinationTree,
                operationToken);
            retainedExtractionInventory.Sort((left, right) =>
                string.CompareOrdinal(left.RelativePath, right.RelativePath));
            var extractionInventory = retainedExtractionInventory.ToArray();
            if (extractionInventory.Length != plans.FileCount
                || extractionInventory.Sum(entry => entry.Length) != extractedBytes)
                throw new InvalidDataException(
                    "O inventário final não corresponde ao conteúdo extraído.");
            WriteCompletionMarker(
                stagingPath,
                sourceIdentity,
                safeCategory,
                item,
                stableItemId,
                extractionInventory,
                destinationTree);
            EnsureTreeContainsNoReparsePoints(
                stagingPath,
                deadline,
                MaximumInventoryTreeEntries,
                destinationTree,
                operationToken);

            deadline.ThrowIfExpired(operationToken);
            destinationTree.Revalidate();
            deadline.ThrowIfExpired(operationToken);
            // Windows refuses a directory rename while any descendant handle is
            // open, even when those transition handles grant FILE_SHARE_DELETE
            // on some filesystems. PrepareSubtreeForRename first overlaps every
            // identity with a share-delete handle, releases the original locks,
            // and this narrow transition is then closed immediately before the
            // root-handle rename. Volume/base/library/staging/destination-parent
            // remain pinned throughout, and the complete tree is reopened and
            // re-authenticated before success is observable.
            using (var renameTransition = destinationTree.PrepareSubtreeForRename(stagingPath))
            {
            }
            var stagingHandle = destinationTree.GetDirectoryHandle(stagingPath);
            var stagingIdentity = PathIdentity.CaptureDirectoryIdentity(
                stagingHandle,
                stagingPath);
            var publishedIdentity = PathIdentity.RenameByHandle(
                stagingHandle,
                stagingIdentity,
                destinationTree.GetDirectoryHandle(destinationParent),
                destinationParent,
                Path.GetFileName(destinationPath),
                replaceIfExists: false);
            var releasedRenamedRoot = false;
            try
            {
                destinationTree.ReleaseDirectoryAfterRename(stagingPath);
                releasedRenamedRoot = true;
                using var publishedTree = PathIdentity.OpenDirectoryTree(destinationPath);
                if (!await IsCompletedExtractionAsync(
                        destinationPath,
                        sourceIdentity,
                        safeCategory,
                        stableItemId,
                        plans,
                        deadline,
                        publishedTree,
                        operationToken))
                    throw new InvalidDataException(
                        "A árvore publicada divergiu do inventário autenticado.");
                stagingPath = null;
            }
            catch
            {
                var quarantinePath = Path.Combine(
                    stagingContainer,
                    Guid.NewGuid().ToString("N") + ".failed");
                if (!releasedRenamedRoot)
                {
                    _ = PathIdentity.RenameByHandle(
                        stagingHandle,
                        publishedIdentity,
                        destinationTree.GetDirectoryHandle(stagingContainer),
                        stagingContainer,
                        Path.GetFileName(quarantinePath),
                        replaceIfExists: false);
                }
                else
                {
                    using var quarantineSource = PathIdentity.OpenDirectoryTree(
                        destinationPath,
                        leafDeleteAccess: true);
                    var quarantineHandle = quarantineSource.AnchorHandle;
                    var quarantineIdentity = PathIdentity.CaptureDirectoryIdentity(
                        quarantineHandle,
                        destinationPath);
                    _ = PathIdentity.RenameByHandle(
                        quarantineHandle,
                        quarantineIdentity,
                        destinationTree.GetDirectoryHandle(stagingContainer),
                        stagingContainer,
                        Path.GetFileName(quarantinePath),
                        replaceIfExists: false);
                }
                stagingPath = quarantinePath;
                throw;
            }

            return new CatalogArchiveExtractionResult(
                CatalogArchiveExtractionStatus.Succeeded,
                "Pacote extraído com segurança. O arquivo compactado foi preservado.",
                canonicalArchivePath,
                destinationPath,
                requiredBytes,
                availableBytes,
                extractedFileCount,
                extractedBytes);
        }
        catch (OperationCanceledException)
        {
            var timedOut = !cancellationToken.IsCancellationRequested
                           && (timeoutCancellation.IsCancellationRequested || deadline.IsExpired);
            return new CatalogArchiveExtractionResult(
                CatalogArchiveExtractionStatus.Canceled,
                timedOut
                    ? "A extração excedeu o tempo máximo configurado. O pacote compactado foi preservado."
                    : "Extração cancelada. O pacote compactado foi preservado e pode ser usado novamente.",
                canonicalArchivePath,
                destinationPath,
                requiredBytes,
                availableBytes,
                extractedFileCount,
                extractedBytes);
        }
        catch (IOException exception) when (IsDiskFull(exception))
        {
            availableBytes = TryGetAvailableFreeSpace(destinationPath, baseDirectory);
            return new CatalogArchiveExtractionResult(
                CatalogArchiveExtractionStatus.InsufficientSpace,
                $"O disco ficou sem espaço durante a extração em {libraryFolderName}.",
                canonicalArchivePath,
                destinationPath,
                requiredBytes,
                availableBytes,
                extractedFileCount,
                extractedBytes);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return new CatalogArchiveExtractionResult(
                CatalogArchiveExtractionStatus.Failed,
                $"Falha na extração: {exception.Message} O pacote compactado foi preservado.",
                canonicalArchivePath,
                destinationPath,
                requiredBytes,
                availableBytes,
                extractedFileCount,
                extractedBytes);
        }
        finally
        {
            DeleteStagingDirectory(stagingPath);
        }
    }

    private ExtractionPlan BuildAndValidatePlans(
        IArchive archive,
        ExtractionDeadline deadline,
        CancellationToken cancellationToken)
    {
        var plannedEntries = new List<PlannedEntry>();
        var exactTargets = new HashSet<string>(StringComparer.Ordinal);
        var caseInsensitiveTargets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long totalUncompressedBytes = 0;
        long totalCompressedBytes = 0;
        var hasPerEntryCompressedSizes = archive.Type is ArchiveType.Zip or ArchiveType.Rar;

        foreach (var entry in archive.Entries)
        {
            deadline.ThrowIfExpired(cancellationToken);
            if (plannedEntries.Count >= _options.MaximumEntryCount)
                throw new InvalidDataException(
                    $"O pacote excede o limite de {_options.MaximumEntryCount:N0} entradas.");
            if (!entry.IsComplete)
                throw new InvalidDataException($"A entrada '{entry.Key}' está incompleta.");
            if (entry.IsEncrypted)
                throw new InvalidDataException("Pacotes protegidos por senha não são aceitos.");
            EnsureEntryIsNotLinkOrReparsePoint(entry);
            if (entry.Size < 0)
                throw new InvalidDataException($"A entrada '{entry.Key}' possui tamanho inválido.");
            if (entry.CompressedSize < 0)
                throw new InvalidDataException($"A entrada '{entry.Key}' possui tamanho compactado inválido.");

            var relativePath = NormalizeArchiveEntryPath(
                entry.Key ?? string.Empty,
                _options.MaximumPathDepth,
                _options.MaximumPathSegmentLength,
                _options.MaximumRelativePathLength);
            if (relativePath.Equals(CompletionMarkerFileName, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException(
                    $"A entrada reservada '{entry.Key}' foi bloqueada.");
            if (!exactTargets.Add(relativePath))
                throw new InvalidDataException(
                    $"O pacote contém caminhos duplicados: '{entry.Key}'.");
            if (!caseInsensitiveTargets.Add(relativePath))
                throw new InvalidDataException(
                    $"O pacote contém caminhos que colidem por maiúsculas/minúsculas: '{entry.Key}'.");

            if (entry.IsDirectory)
            {
                if (entry.Size != 0)
                    throw new InvalidDataException(
                        $"O diretório '{entry.Key}' declara conteúdo e foi bloqueado.");
            }
            else
            {
                if (entry.Size > _options.MaximumEntryUncompressedBytes)
                    throw new InvalidDataException(
                        $"A entrada '{entry.Key}' excede o limite descompactado por arquivo.");
                if (hasPerEntryCompressedSizes)
                    ValidateCompressionRatio(
                        entry.Size,
                        entry.CompressedSize,
                        $"a entrada '{entry.Key}'");
                totalUncompressedBytes = checked(totalUncompressedBytes + entry.Size);
                totalCompressedBytes = checked(totalCompressedBytes + entry.CompressedSize);
                if (totalUncompressedBytes > _options.MaximumTotalUncompressedBytes)
                    throw new InvalidDataException(
                        "O pacote excede o limite total descompactado configurado.");
            }

            plannedEntries.Add(new PlannedEntry(
                entry,
                relativePath,
                entry.Size,
                entry.CompressedSize));
        }

        if (plannedEntries.Count == 0)
            throw new InvalidDataException("O pacote não contém arquivos para extrair.");

        ValidatePathHierarchy(plannedEntries, deadline, cancellationToken);
        if (totalUncompressedBytes > 0)
        {
            if (archive.TotalSize <= 0)
                throw new InvalidDataException(
                    "O pacote não informou tamanho compactado total verificável.");
            ValidateCompressionRatio(totalUncompressedBytes, archive.TotalSize, "o pacote");
        }
        return new ExtractionPlan(
            plannedEntries,
            plannedEntries.Count(entry => !entry.Entry.IsDirectory),
            totalUncompressedBytes,
            totalCompressedBytes);
    }

    private void ValidateCompressionRatio(long uncompressedBytes, long compressedBytes, string subject)
    {
        if (uncompressedBytes == 0) return;
        if (compressedBytes <= 0
            || uncompressedBytes / (double)compressedBytes > _options.MaximumCompressionRatio)
            throw new InvalidDataException(
                $"A razão de compressão de {subject} excede o limite configurado.");
    }

    private static void ValidatePathHierarchy(
        IEnumerable<PlannedEntry> entries,
        ExtractionDeadline deadline,
        CancellationToken cancellationToken)
    {
        deadline.ThrowIfExpired(cancellationToken);
        var filePaths = entries
            .Where(entry => !entry.Entry.IsDirectory)
            .Select(entry => entry.RelativePath)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var entry in entries)
        {
            deadline.ThrowIfExpired(cancellationToken);
            var parent = Path.GetDirectoryName(entry.RelativePath);
            while (!string.IsNullOrEmpty(parent))
            {
                deadline.ThrowIfExpired(cancellationToken);
                if (filePaths.Contains(parent))
                    throw new InvalidDataException(
                        $"O caminho '{entry.Entry.Key}' tenta criar conteúdo dentro de um arquivo.");
                parent = Path.GetDirectoryName(parent);
            }
        }
    }

    private static async Task<long> CopyEntryAsync(
        Stream input,
        Stream output,
        long declaredSize,
        long maximumEntrySize,
        int bufferSize,
        ExtractionDeadline deadline,
        Action<long> reportCopied,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[bufferSize];
        long copied = 0;
        while (true)
        {
            deadline.ThrowIfExpired(cancellationToken);
            var read = await input.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken);
            deadline.ThrowIfExpired(cancellationToken);
            if (read == 0) break;
            copied = checked(copied + read);
            if (copied > declaredSize || copied > maximumEntrySize)
                throw new InvalidDataException("Uma entrada excedeu o tamanho declarado no pacote.");
            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            reportCopied(copied);
            deadline.ThrowIfExpired(cancellationToken);
        }

        await output.FlushAsync(cancellationToken);
        deadline.ThrowIfExpired(cancellationToken);
        if (copied != declaredSize)
            throw new InvalidDataException("Uma entrada não corresponde ao tamanho declarado no pacote.");
        return copied;
    }

    private static string RequireExistingArchive(string archivePath)
    {
        if (string.IsNullOrWhiteSpace(archivePath))
            throw new ArgumentException("Informe o arquivo compactado.", nameof(archivePath));
        var canonicalPath = Path.GetFullPath(archivePath);
        if (!File.Exists(canonicalPath))
            throw new FileNotFoundException("O arquivo compactado não foi encontrado.", canonicalPath);
        var attributes = File.GetAttributes(canonicalPath);
        if ((attributes & FileAttributes.Directory) != 0)
            throw new InvalidDataException("O caminho do pacote aponta para uma pasta.");
        if ((attributes & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("Links e reparse points não são aceitos como pacote de origem.");
        return canonicalPath;
    }

    private static FileStream OpenArchiveStream(
        string canonicalArchivePath,
        PathIdentity.DirectoryTreeLease archivePathLease)
    {
        // FileShare.Read deliberately denies new writers, renames and deletes on
        // Windows. More importantly, SharpCompress plans and reads through this
        // same handle, so replacing the path cannot redirect a later entry read.
        archivePathLease.Revalidate();
        return archivePathLease.OpenFile(
            canonicalArchivePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            128 * 1024,
            FileOptions.Asynchronous | FileOptions.RandomAccess);
    }

    private static async Task<ArchiveSourceIdentity> CaptureAndValidateAuthorizedIdentityAsync(
        string canonicalArchivePath,
        FileStream archiveStream,
        CatalogArtifactDescriptor authorizedArtifact,
        PathIdentity.HandleIdentity archiveHandleIdentity,
        ExtractionDeadline deadline,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(authorizedArtifact);
        ValidateAuthorizedArtifactDescriptor(authorizedArtifact);
        var info = new FileInfo(canonicalArchivePath);
        info.Refresh();
        if (!info.Exists || archiveStream.Length != info.Length)
            throw new InvalidDataException("O pacote mudou durante a abertura segura.");
        if (archiveStream.Length != authorizedArtifact.ContentLength)
            throw new InvalidDataException(
                "O tamanho do pacote não corresponde ao manifesto autorizado.");

        archiveStream.Position = 0;
        var actualSha256 = Convert.ToHexString(
                await SHA256.HashDataAsync(archiveStream, cancellationToken))
            .ToLowerInvariant();
        deadline.ThrowIfExpired(cancellationToken);
        archiveStream.Position = 0;
        if (!actualSha256.Equals(authorizedArtifact.Sha256, StringComparison.Ordinal))
            throw new InvalidDataException(
                "O SHA-256 do pacote não corresponde ao manifesto autorizado.");

        return new ArchiveSourceIdentity(
            info.Length,
            info.LastWriteTimeUtc.Ticks,
            actualSha256,
            authorizedArtifact.ManifestIdentity,
            authorizedArtifact.ArtifactId,
            authorizedArtifact.ArtifactVersion,
            archiveHandleIdentity);
    }

    private static void ValidateAuthorizedArtifactDescriptor(
        CatalogArtifactDescriptor authorizedArtifact)
    {
        if (!authorizedArtifact.ProductId.Equals(
                CatalogArtifactDescriptor.TurboramaSuiteProductId,
                StringComparison.Ordinal)
            || authorizedArtifact.ArtifactId.Length != 32
            || authorizedArtifact.ArtifactId.Any(character =>
                !(character is >= '0' and <= '9' or >= 'a' and <= 'f'))
            || authorizedArtifact.ArtifactVersion <= 0
            || authorizedArtifact.ContentLength <= 0
            || !IsCanonicalSha256(authorizedArtifact.Sha256)
            || !IsCanonicalSha256(authorizedArtifact.ManifestIdentity)
            || authorizedArtifact.ExtractPolicy != CatalogExtractPolicy.ExtractArchive)
            throw new InvalidDataException(
                "A identidade ou a política do artefato autorizado é inválida para extração.");
    }

    private static bool IsCanonicalSha256(string value) => value.Length == 64
        && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static async Task ValidateAuthorizedArchiveIdentityAsync(
        string canonicalArchivePath,
        FileStream archiveStream,
        ArchiveSourceIdentity expected,
        ExtractionDeadline deadline,
        CancellationToken cancellationToken)
    {
        ValidateArchiveSourceIdentity(canonicalArchivePath, archiveStream, expected);
        archiveStream.Position = 0;
        var actualSha256 = Convert.ToHexString(
                await SHA256.HashDataAsync(archiveStream, cancellationToken))
            .ToLowerInvariant();
        deadline.ThrowIfExpired(cancellationToken);
        archiveStream.Position = 0;
        if (!actualSha256.Equals(expected.Sha256, StringComparison.Ordinal))
            throw new InvalidDataException(
                "O pacote mudou depois da validação autorizada e não será publicado.");
    }

    private static void ValidateArchiveSourceIdentity(
        string canonicalArchivePath,
        FileStream archiveStream,
        ArchiveSourceIdentity expected)
    {
        if (archiveStream.Length != expected.Length)
            throw new InvalidDataException("O pacote mudou durante a extração.");

        _ = PathIdentity.RevalidateFile(
            archiveStream.SafeFileHandle,
            canonicalArchivePath,
            expected.PathIdentity);

        var info = new FileInfo(canonicalArchivePath);
        info.Refresh();
        if (!info.Exists
            || info.Length != expected.Length
            || info.LastWriteTimeUtc.Ticks != expected.LastWriteUtcTicks
            || (info.Attributes & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException(
                "A identidade do pacote mudou entre o planejamento e a leitura.");
    }

    private static string RequireBaseDirectory(string baseDirectory)
    {
        if (string.IsNullOrWhiteSpace(baseDirectory))
            throw new ArgumentException("Informe a pasta-base da biblioteca.", nameof(baseDirectory));
        var canonicalPath = Path.GetFullPath(baseDirectory);
        var root = Path.GetPathRoot(canonicalPath);
        if (string.IsNullOrEmpty(root) || !Directory.Exists(root))
            throw new DirectoryNotFoundException("A unidade escolhida não está disponível.");
        if (!Directory.Exists(canonicalPath))
            throw new DirectoryNotFoundException("A pasta-base da biblioteca não foi encontrada.");
        if ((File.GetAttributes(canonicalPath) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("A pasta-base não pode ser um link ou reparse point.");
        return canonicalPath;
    }

    private static string RequireGameLibraryRoot(string canonicalPath)
    {
        if (!Directory.Exists(canonicalPath))
            throw new DirectoryNotFoundException(
                $"A pasta mestre {GameLibraryFolderName} não foi encontrada.");
        if (!IsGameLibraryRoot(canonicalPath))
            throw new InvalidDataException(
                $"A pasta mestre precisa se chamar exatamente '{GameLibraryFolderName}'.");
        return canonicalPath;
    }

    public static bool IsGameLibraryRoot(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        try
        {
            var canonical = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
            var parent = Directory.GetParent(canonical);
            return Directory.Exists(canonical)
                   && Path.GetFileName(canonical).Equals(
                       GameLibraryLeafFolderName,
                       StringComparison.OrdinalIgnoreCase)
                   && parent is not null
                   && Path.GetFileName(Path.TrimEndingDirectorySeparator(parent.FullName)).Equals(
                       LibraryFolderName,
                       StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (exception is IOException
                                           or UnauthorizedAccessException
                                           or ArgumentException
                                           or NotSupportedException)
        {
            return false;
        }
    }

    private static string SanitizeDestinationSegment(string value, string fieldName)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException($"Informe o nome de {fieldName}.", fieldName);

        var invalid = Path.GetInvalidFileNameChars().ToHashSet();
        var characters = value.Trim()
            .Select(character => invalid.Contains(character) || char.IsControl(character) ? '-' : character)
            .ToArray();
        var result = new string(characters).Trim(' ', '.', '-');
        if (result.Length == 0)
            throw new InvalidDataException($"O nome de {fieldName} não forma uma pasta válida.");
        if (IsWindowsDeviceName(result)) result += "-item";
        return result.Length <= 120 ? result : result[..120].TrimEnd(' ', '.', '-');
    }

    private static string ResolveStableItemId(string? itemId, string canonicalArchivePath)
    {
        if (!string.IsNullOrWhiteSpace(itemId))
        {
            var normalized = itemId.Trim().ToLowerInvariant();
            if (normalized.Length is < 1 or > 160
                || normalized.Any(character => !(char.IsAsciiLetterOrDigit(character)
                                                   || character is '-' or '_' or '.')))
                throw new InvalidDataException(
                    "O identificador estável do item possui formato inválido.");
            return normalized;
        }

        // Compatibility path for existing callers. Current downloaded package
        // names already contain the catalog item identifier and its hash, so the
        // fallback remains independent from the mutable display title.
        var archiveName = Path.GetFileNameWithoutExtension(canonicalArchivePath);
        return SanitizeDestinationSegment(archiveName, "identificador do pacote")
            .ToLowerInvariant();
    }

    private static string BuildStableItemDirectoryName(string stableItemId)
    {
        var safePrefix = SanitizeDestinationSegment(stableItemId, "identificador do item")
            .ToLowerInvariant();
        if (safePrefix.Length > 96) safePrefix = safePrefix[..96].TrimEnd(' ', '.', '-');
        var idBytes = Encoding.UTF8.GetBytes(stableItemId);
        try
        {
            var hash = Convert.ToHexString(SHA256.HashData(idBytes))[..16].ToLowerInvariant();
            return $"{safePrefix}-{hash}";
        }
        finally
        {
            CryptographicOperations.ZeroMemory(idBytes);
        }
    }

    private static string NormalizeArchiveEntryPath(
        string key,
        int maximumPathDepth,
        int maximumPathSegmentLength,
        int maximumRelativePathLength)
    {
        if (string.IsNullOrWhiteSpace(key))
            throw new InvalidDataException("O pacote contém uma entrada sem nome.");
        if (key.Contains('\0'))
            throw new InvalidDataException("O pacote contém um caminho inválido.");

        var normalized = key.Normalize(NormalizationForm.FormC).Replace('\\', '/');
        if (normalized.Length > maximumRelativePathLength)
            throw new InvalidDataException(
                $"O caminho '{key}' excede o limite de {maximumRelativePathLength} caracteres.");
        if (normalized[0] == '/')
            throw new InvalidDataException($"O caminho absoluto '{key}' foi bloqueado.");

        var segments = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0)
            throw new InvalidDataException($"O caminho '{key}' é inválido.");
        if (segments.Length > maximumPathDepth)
            throw new InvalidDataException(
                $"O caminho '{key}' excede a profundidade máxima de {maximumPathDepth} níveis.");

        foreach (var segment in segments)
        {
            if (segment is "." or "..")
                throw new InvalidDataException($"O caminho relativo '{key}' foi bloqueado.");
            if (segment.Length > maximumPathSegmentLength)
                throw new InvalidDataException(
                    $"Um segmento de '{key}' excede o limite de {maximumPathSegmentLength} caracteres.");
            ValidateArchivePathSegment(segment, key);
        }

        var relativePath = Path.Combine(segments);
        if (relativePath.Length > maximumRelativePathLength)
            throw new InvalidDataException(
                $"O caminho normalizado '{key}' excede o limite configurado.");
        return relativePath;
    }

    private static void EnsureEntryIsNotLinkOrReparsePoint(IArchiveEntry entry)
    {
        if (!string.IsNullOrWhiteSpace(entry.LinkTarget))
            throw new InvalidDataException(
                $"A entrada '{entry.Key}' é um link e foi bloqueada por segurança.");
        if (entry.Attrib is not int attributes) return;

        var rawAttributes = unchecked((uint)attributes);
        if ((rawAttributes & (uint)FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException(
                $"A entrada '{entry.Key}' declara um reparse point e foi bloqueada.");

        // ZIP stores Unix mode/type in the high 16 bits. Accept only regular
        // files and directories; symbolic links, devices, sockets and FIFOs fail.
        var unixType = (rawAttributes >> 16) & 0xF000u;
        if (unixType != 0
            && unixType != 0x8000u
            && unixType != 0x4000u)
            throw new InvalidDataException(
                $"A entrada especial '{entry.Key}' foi bloqueada por segurança.");
    }

    private static void ValidateArchivePathSegment(string segment, string originalKey)
    {
        if (segment.EndsWith(' ') || segment.EndsWith('.'))
            throw new InvalidDataException(
                $"O caminho '{originalKey}' termina com ponto ou espaço e foi bloqueado.");
        if (segment.Any(character => char.IsControl(character)
                                     || Path.GetInvalidFileNameChars().Contains(character)))
            throw new InvalidDataException($"O caminho '{originalKey}' contém caracteres inválidos.");
        if (IsWindowsDeviceName(segment))
            throw new InvalidDataException($"O caminho reservado '{originalKey}' foi bloqueado.");
    }

    private static bool IsWindowsDeviceName(string segment)
    {
        var nameWithoutExtension = segment.Split('.', 2)[0];
        return WindowsDeviceNames.Contains(nameWithoutExtension);
    }

    private static void EnsureSupportedArchive(IArchive archive)
    {
        if (archive.Type is not (ArchiveType.Zip or ArchiveType.Rar or ArchiveType.SevenZip))
            throw new InvalidDataException(
                $"Formato não suportado: {archive.Type}. Use um pacote ZIP, RAR ou 7z.");
        if (!archive.IsComplete)
            throw new InvalidDataException("O pacote está incompleto ou faltam partes do arquivo.");
        if (archive.IsEncrypted)
            throw new InvalidDataException("Pacotes protegidos por senha não são aceitos.");
    }

    private static long GetAvailableFreeSpace(string destinationBase)
    {
        var root = Path.GetPathRoot(Path.GetFullPath(destinationBase));
        if (string.IsNullOrEmpty(root))
            throw new IOException("Não foi possível identificar a unidade de destino.");
        return new DriveInfo(root).AvailableFreeSpace;
    }

    private static void WriteCompletionMarker(
        string stagingPath,
        ArchiveSourceIdentity sourceIdentity,
        string category,
        string itemTitle,
        string stableItemId,
        ExtractionInventoryEntry[] inventory,
        PathIdentity.DirectoryTreeLease destinationTree)
    {
        var marker = new ExtractionCompletionMarker(
            CompletionMarkerSchemaVersion,
            sourceIdentity.Length,
            sourceIdentity.Sha256,
            sourceIdentity.ManifestIdentity,
            sourceIdentity.ArtifactId,
            sourceIdentity.ArtifactVersion,
            category,
            itemTitle,
            stableItemId,
            inventory);
        var markerPath = Path.Combine(stagingPath, CompletionMarkerFileName);
        var markerBytes = JsonSerializer.SerializeToUtf8Bytes(marker);
        if (markerBytes.Length > MaximumCompletionMarkerBytes)
            throw new InvalidDataException("O inventário da extração excedeu o limite seguro.");
        using var markerStream = destinationTree.OpenFile(
            markerPath,
            FileMode.CreateNew,
            FileAccess.ReadWrite,
            FileShare.Read,
            64 * 1024,
            FileOptions.WriteThrough);
        markerStream.Write(markerBytes);
        markerStream.Flush(flushToDisk: true);
        var markerIdentity = PathIdentity.CaptureFileIdentity(
            markerStream.SafeFileHandle,
            markerPath);
        destinationTree.RetainFile(
            markerStream.SafeFileHandle,
            markerPath,
            markerIdentity);
    }

    private async Task<bool> IsCompletedExtractionAsync(
        string destinationPath,
        ArchiveSourceIdentity sourceIdentity,
        string category,
        string stableItemId,
        ExtractionPlan authenticatedArchivePlans,
        ExtractionDeadline deadline,
        PathIdentity.DirectoryTreeLease destinationTree,
        CancellationToken cancellationToken)
    {
        try
        {
            deadline.ThrowIfExpired(cancellationToken);
            var markerPath = Path.Combine(destinationPath, CompletionMarkerFileName);
            if (!File.Exists(markerPath)) return false;
            byte[] markerBytes;
            await using (var markerStream = destinationTree.OpenFile(
                             markerPath,
                             FileMode.Open,
                             FileAccess.Read,
                             FileShare.Read,
                             64 * 1024,
                             FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                if (markerStream.Length is <= 0 or > MaximumCompletionMarkerBytes)
                    return false;
                var markerIdentity = PathIdentity.CaptureFileIdentity(
                    markerStream.SafeFileHandle,
                    markerPath);
                destinationTree.RetainFile(
                    markerStream.SafeFileHandle,
                    markerPath,
                    markerIdentity);
                markerBytes = new byte[checked((int)markerStream.Length)];
                await markerStream.ReadExactlyAsync(markerBytes, cancellationToken);
            }

            ExtractionCompletionMarker? marker;
            try
            {
                marker = JsonSerializer.Deserialize<ExtractionCompletionMarker>(markerBytes);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(markerBytes);
            }

            if (marker is null
                || marker.SchemaVersion != CompletionMarkerSchemaVersion
                || marker.Inventory is null
                || !IsValidMarkerInventory(marker.Inventory)
                || marker.ArchiveLength != sourceIdentity.Length
                || !string.Equals(marker.ArchiveSha256, sourceIdentity.Sha256, StringComparison.Ordinal)
                || !string.Equals(marker.ManifestIdentity, sourceIdentity.ManifestIdentity, StringComparison.Ordinal)
                || !string.Equals(marker.ArtifactId, sourceIdentity.ArtifactId, StringComparison.Ordinal)
                || marker.ArtifactVersion != sourceIdentity.ArtifactVersion
                || !string.Equals(marker.Category, category, StringComparison.Ordinal)
                || !string.Equals(marker.StableItemId, stableItemId, StringComparison.Ordinal))
                return false;

            // The marker is only a recovery hint. Derive the authoritative inventory
            // from the entries read through the same archive handle whose complete
            // SHA-256 was authenticated before parsing. An attacker who can rewrite
            // both the destination and marker still cannot forge this comparison.
            var authenticatedArchiveInventory = await BuildArchiveInventoryAsync(
                authenticatedArchivePlans,
                deadline,
                cancellationToken);
            if (!marker.Inventory.SequenceEqual(authenticatedArchiveInventory))
                return false;

            EnsureTreeContainsNoReparsePoints(
                destinationPath,
                deadline,
                MaximumInventoryTreeEntries,
                destinationTree,
                cancellationToken);
            var actualInventory = await BuildExtractionInventoryAsync(
                destinationPath,
                deadline,
                destinationTree,
                cancellationToken);
            return authenticatedArchiveInventory.SequenceEqual(actualInventory);
        }
        catch (Exception exception) when (exception is IOException
                                           or UnauthorizedAccessException
                                           or JsonException
                                           or InvalidDataException
                                           or System.Security.Cryptography.CryptographicException)
        {
            return false;
        }
    }

    private async Task<ExtractionInventoryEntry[]> BuildArchiveInventoryAsync(
        ExtractionPlan plans,
        ExtractionDeadline deadline,
        CancellationToken cancellationToken)
    {
        var inventory = new List<ExtractionInventoryEntry>(plans.FileCount);
        var buffer = new byte[_options.CopyBufferSize];
        long totalBytes = 0;
        try
        {
            foreach (var plan in plans)
            {
                deadline.ThrowIfExpired(cancellationToken);
                if (plan.Entry.IsDirectory) continue;
                if (plan.Entry.Size != plan.DeclaredSize
                    || plan.Entry.CompressedSize != plan.DeclaredCompressedSize)
                    throw new InvalidDataException(
                        $"Os metadados da entrada '{plan.Entry.Key}' mudaram durante a recuperação.");

                await using var input = await plan.Entry.OpenEntryStreamAsync(cancellationToken);
                using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                long entryBytes = 0;
                while (true)
                {
                    deadline.ThrowIfExpired(cancellationToken);
                    var read = await input.ReadAsync(
                        buffer.AsMemory(0, buffer.Length),
                        cancellationToken);
                    deadline.ThrowIfExpired(cancellationToken);
                    if (read == 0) break;
                    entryBytes = checked(entryBytes + read);
                    totalBytes = checked(totalBytes + read);
                    if (entryBytes > plan.DeclaredSize
                        || entryBytes > _options.MaximumEntryUncompressedBytes
                        || totalBytes > _options.MaximumTotalUncompressedBytes)
                        throw new InvalidDataException(
                            "O conteúdo do pacote excedeu os limites autorizados durante a recuperação.");
                    hasher.AppendData(buffer, 0, read);
                }

                if (entryBytes != plan.DeclaredSize)
                    throw new InvalidDataException(
                        $"A entrada '{plan.Entry.Key}' não corresponde ao tamanho declarado durante a recuperação.");
                var hashBytes = hasher.GetHashAndReset();
                try
                {
                    inventory.Add(new ExtractionInventoryEntry(
                        plan.RelativePath.Replace(Path.DirectorySeparatorChar, '/'),
                        entryBytes,
                        Convert.ToHexString(hashBytes).ToLowerInvariant()));
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(hashBytes);
                }
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(buffer);
        }

        if (inventory.Count != plans.FileCount
            || totalBytes != plans.TotalUncompressedBytes)
            throw new InvalidDataException(
                "O inventário autenticado não corresponde aos metadados do pacote.");
        inventory.Sort((left, right) => string.CompareOrdinal(left.RelativePath, right.RelativePath));
        return inventory.ToArray();
    }

    private bool IsValidMarkerInventory(ExtractionInventoryEntry[] inventory)
    {
        if (inventory.Length > _options.MaximumEntryCount) return false;
        string? previousPath = null;
        long totalBytes = 0;
        foreach (var entry in inventory)
        {
            if (entry is null
                || entry.Length < 0
                || entry.Length > _options.MaximumEntryUncompressedBytes
                || !IsCanonicalSha256(entry.Sha256))
                return false;
            var normalized = NormalizeArchiveEntryPath(
                    entry.RelativePath,
                    _options.MaximumPathDepth,
                    _options.MaximumPathSegmentLength,
                    _options.MaximumRelativePathLength)
                .Replace(Path.DirectorySeparatorChar, '/');
            if (!normalized.Equals(entry.RelativePath, StringComparison.Ordinal)
                || previousPath is not null
                && string.CompareOrdinal(previousPath, entry.RelativePath) >= 0)
                return false;
            totalBytes = checked(totalBytes + entry.Length);
            if (totalBytes > _options.MaximumTotalUncompressedBytes) return false;
            previousPath = entry.RelativePath;
        }
        return true;
    }

    private async Task<ExtractionInventoryEntry[]> BuildExtractionInventoryAsync(
        string rootPath,
        ExtractionDeadline deadline,
        PathIdentity.DirectoryTreeLease destinationTree,
        CancellationToken cancellationToken)
    {
        var canonicalRoot = Path.GetFullPath(rootPath);
        var pending = new Stack<string>();
        var inventory = new List<ExtractionInventoryEntry>();
        long totalBytes = 0;
        var visitedEntries = 0;
        pending.Push(canonicalRoot);

        while (pending.Count > 0)
        {
            deadline.ThrowIfExpired(cancellationToken);
            var current = pending.Pop();
            foreach (var child in Directory.EnumerateFileSystemEntries(current))
            {
                deadline.ThrowIfExpired(cancellationToken);
                visitedEntries++;
                if (visitedEntries > MaximumInventoryTreeEntries)
                    throw new InvalidDataException(
                        "A árvore extraída excedeu o limite estrutural do inventário.");
                var attributes = File.GetAttributes(child);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException(
                        $"A árvore extraída contém um link ou reparse point não autorizado: {child}");
                if ((attributes & FileAttributes.Directory) != 0)
                {
                    _ = destinationTree.EnsureDirectory(child);
                    pending.Push(child);
                    continue;
                }

                var canonicalChild = Path.GetFullPath(child);
                var markerPath = Path.GetFullPath(Path.Combine(
                    canonicalRoot,
                    CompletionMarkerFileName));
                if (canonicalChild.Equals(
                        markerPath,
                        OperatingSystem.IsWindows()
                            ? StringComparison.OrdinalIgnoreCase
                            : StringComparison.Ordinal))
                    continue;
                if (inventory.Count >= _options.MaximumEntryCount)
                    throw new InvalidDataException(
                        "A árvore extraída excedeu o limite de arquivos do inventário.");

                var relativePath = Path.GetRelativePath(canonicalRoot, canonicalChild)
                    .Replace(Path.DirectorySeparatorChar, '/');
                if (relativePath.Length == 0
                    || relativePath.Length > _options.MaximumRelativePathLength
                    || relativePath.StartsWith("../", StringComparison.Ordinal)
                    || Path.IsPathFullyQualified(relativePath))
                    throw new InvalidDataException(
                        "A árvore extraída contém um caminho fora do inventário seguro.");

                await using var stream = destinationTree.OpenFile(
                    child,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read,
                    _options.CopyBufferSize,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);
                var identity = PathIdentity.CaptureFileIdentity(
                    stream.SafeFileHandle,
                    child);
                destinationTree.RetainFile(stream.SafeFileHandle, child, identity);
                if (stream.Length > _options.MaximumEntryUncompressedBytes)
                    throw new InvalidDataException(
                        "Um arquivo extraído excedeu o limite individual do inventário.");
                totalBytes = checked(totalBytes + stream.Length);
                if (totalBytes > _options.MaximumTotalUncompressedBytes)
                    throw new InvalidDataException(
                        "A árvore extraída excedeu o limite total do inventário.");

                var hash = Convert.ToHexString(
                        await SHA256.HashDataAsync(stream, cancellationToken))
                    .ToLowerInvariant();
                deadline.ThrowIfExpired(cancellationToken);
                inventory.Add(new ExtractionInventoryEntry(relativePath, stream.Length, hash));
            }
        }

        inventory.Sort((left, right) => string.CompareOrdinal(left.RelativePath, right.RelativePath));
        return inventory.ToArray();
    }

    private static long TryGetAvailableFreeSpace(string destinationPath, string fallbackBase)
    {
        try
        {
            return GetAvailableFreeSpace(
                string.IsNullOrWhiteSpace(destinationPath) ? fallbackBase : destinationPath);
        }
        catch (Exception exception) when (exception is IOException
                                           or UnauthorizedAccessException
                                           or ArgumentException)
        {
            return 0;
        }
    }

    private static bool IsDiskFull(IOException exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current is not IOException ioException) continue;
            var windowsError = ioException.HResult & 0xFFFF;
            if (windowsError is 0x27 or 0x70) return true; // HANDLE_DISK_FULL / DISK_FULL
        }
        return false;
    }

    private static void EnsureWithinRoot(string candidatePath, string rootPath, string message)
    {
        if (!IsWithinRoot(candidatePath, rootPath)) throw new InvalidDataException(message);
    }

    private static void EnsurePathLength(string path, int maximumLength, string subject)
    {
        if (path.Length > maximumLength)
            throw new InvalidDataException(
                $"O {subject} excede o limite seguro de {maximumLength} caracteres.");
    }

    private static void EnsureNoReparsePoints(string rootPath, string candidatePath)
    {
        var canonicalRoot = Path.GetFullPath(rootPath);
        var canonicalCandidate = Path.GetFullPath(candidatePath);
        EnsureWithinRoot(
            canonicalCandidate,
            canonicalRoot,
            "O destino físico saiu da pasta escolhida.");

        if (!Directory.Exists(canonicalRoot))
            throw new InvalidDataException("A raiz física do destino não existe.");

        var volumeRoot = Path.GetPathRoot(canonicalCandidate);
        if (string.IsNullOrWhiteSpace(volumeRoot))
            throw new InvalidDataException("O destino não possui raiz física.");
        var current = volumeRoot;
        EnsureExistingPathIsNotReparsePoint(current);
        foreach (var segment in Path.GetRelativePath(volumeRoot, canonicalCandidate).Split(
                     [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                     StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            try
            {
                EnsureExistingPathIsNotReparsePoint(current);
            }
            catch (FileNotFoundException)
            {
                break;
            }
            catch (DirectoryNotFoundException)
            {
                break;
            }
        }
    }

    private static void EnsureExistingPathIsNotReparsePoint(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException(
                $"O destino contém um atalho ou junção não autorizado: {path}");
    }

    private static void EnsureTreeContainsNoReparsePoints(
        string rootPath,
        ExtractionDeadline? deadline = null,
        int maximumVisitedEntries = int.MaxValue,
        PathIdentity.DirectoryTreeLease? destinationTree = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumVisitedEntries);
        deadline?.ThrowIfExpired(cancellationToken);
        var canonicalRoot = Path.GetFullPath(rootPath);
        _ = destinationTree?.EnsureDirectory(canonicalRoot);
        destinationTree?.Revalidate();

        var pending = new Stack<string>();
        var visitedEntries = 0;
        pending.Push(canonicalRoot);
        while (pending.Count > 0)
        {
            deadline?.ThrowIfExpired(cancellationToken);
            var current = pending.Pop();
            foreach (var child in Directory.EnumerateFileSystemEntries(current))
            {
                deadline?.ThrowIfExpired(cancellationToken);
                visitedEntries++;
                if (visitedEntries > maximumVisitedEntries)
                    throw new InvalidDataException(
                        "A árvore extraída excedeu o limite estrutural permitido.");
                var attributes = File.GetAttributes(child);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException(
                        $"A extração produziu um link ou reparse point não autorizado: {child}");
                if ((attributes & FileAttributes.Directory) != 0)
                {
                    _ = destinationTree?.EnsureDirectory(child);
                    pending.Push(child);
                }
            }
        }
        destinationTree?.Revalidate();
    }

    private static bool IsWithinRoot(string candidatePath, string rootPath)
    {
        var canonicalRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(rootPath));
        var canonicalCandidate = Path.GetFullPath(candidatePath);
        if (FileSystemPathComparer.Equals(canonicalCandidate, canonicalRoot)) return true;
        var rootPrefix = Path.EndsInDirectorySeparator(canonicalRoot)
            ? canonicalRoot
            : canonicalRoot + Path.DirectorySeparatorChar;
        return canonicalCandidate.StartsWith(
            rootPrefix,
            OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal);
    }

    private static void DeleteStagingDirectory(string? stagingPath)
    {
        if (string.IsNullOrWhiteSpace(stagingPath) || !Directory.Exists(stagingPath)) return;
        try
        {
            var stagingContainer = Directory.GetParent(stagingPath)?.FullName;
            if (string.IsNullOrEmpty(stagingContainer) || !IsWithinRoot(stagingPath, stagingContainer)) return;
            _ = PathIdentity.DeleteDirectoryTreeExact(stagingPath, stagingContainer);
        }
        catch (Exception exception) when (exception is IOException
                                           or UnauthorizedAccessException
                                           or InvalidDataException)
        {
            // Fail closed. A future retry uses another GUID and never follows or
            // removes an object whose identity could not be retained.
        }
    }

    private sealed record PlannedEntry(
        IArchiveEntry Entry,
        string RelativePath,
        long DeclaredSize,
        long DeclaredCompressedSize);

    private sealed record ArchiveSourceIdentity(
        long Length,
        long LastWriteUtcTicks,
        string Sha256,
        string ManifestIdentity,
        string ArtifactId,
        int ArtifactVersion,
        PathIdentity.HandleIdentity PathIdentity);

    private sealed record ExtractionCompletionMarker(
        int SchemaVersion,
        long ArchiveLength,
        string ArchiveSha256,
        string ManifestIdentity,
        string ArtifactId,
        int ArtifactVersion,
        string Category,
        string Item,
        string StableItemId,
        ExtractionInventoryEntry[] Inventory);

    private sealed record ExtractionInventoryEntry(
        string RelativePath,
        long Length,
        string Sha256);

    private sealed class ExtractionPlan : List<PlannedEntry>
    {
        public ExtractionPlan(
            IEnumerable<PlannedEntry> entries,
            int fileCount,
            long totalUncompressedBytes,
            long totalCompressedBytes) : base(entries)
        {
            FileCount = fileCount;
            TotalUncompressedBytes = totalUncompressedBytes;
            TotalCompressedBytes = totalCompressedBytes;
        }

        public int FileCount { get; }
        public long TotalUncompressedBytes { get; }
        public long TotalCompressedBytes { get; }
    }

    private sealed class ExtractionDeadline
    {
        private readonly long _startedAt = Stopwatch.GetTimestamp();
        private readonly TimeSpan _maximumDuration;

        public ExtractionDeadline(TimeSpan maximumDuration)
            => _maximumDuration = maximumDuration;

        public bool IsExpired => Stopwatch.GetElapsedTime(_startedAt) >= _maximumDuration;

        public void ThrowIfExpired(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (IsExpired)
                throw new OperationCanceledException(
                    "A extração excedeu o tempo máximo configurado.");
        }
    }
}
