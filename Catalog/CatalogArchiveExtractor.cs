using System.IO;
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
    /// number of entries. The payload-size limit remains controlled by disk space.
    /// </summary>
    public int MaximumEntryCount { get; init; } = 250_000;

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

    public CatalogArchiveExtractor(CatalogArchiveExtractionOptions? options = null)
    {
        _options = options ?? new CatalogArchiveExtractionOptions();
        if (_options.MinimumFreeSpaceReserveBytes < 0)
            throw new ArgumentOutOfRangeException(nameof(options), "A reserva de espaço não pode ser negativa.");
        if (_options.MaximumEntryCount <= 0)
            throw new ArgumentOutOfRangeException(nameof(options), "O limite de entradas deve ser positivo.");
        if (_options.CopyBufferSize < 4 * 1024)
            throw new ArgumentOutOfRangeException(nameof(options), "O buffer deve ter pelo menos 4 KB.");
    }

    public async Task<CatalogArchiveExtractionResult> ExtractAsync(
        string archivePath,
        string baseDirectory,
        string category,
        string item,
        IProgress<CatalogArchiveExtractionProgress>? progress = null,
        CancellationToken cancellationToken = default,
        bool baseDirectoryIsGameLibrary = false)
    {
        string canonicalArchivePath = string.Empty;
        string destinationPath = string.Empty;
        string? stagingPath = null;
        long requiredBytes = 0;
        long availableBytes = 0;
        long extractedBytes = 0;
        var extractedFileCount = 0;
        var libraryFolderName = baseDirectoryIsGameLibrary
            ? GameLibraryFolderName
            : LibraryFolderName;

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            canonicalArchivePath = RequireExistingArchive(archivePath);
            var canonicalBase = RequireBaseDirectory(baseDirectory);
            var safeCategory = SanitizeDestinationSegment(category, "categoria");
            var safeItem = SanitizeDestinationSegment(item, "item");
            var libraryRoot = baseDirectoryIsGameLibrary
                ? RequireGameLibraryRoot(canonicalBase)
                : Path.GetFullPath(Path.Combine(canonicalBase, LibraryFolderName));
            destinationPath = Path.GetFullPath(Path.Combine(libraryRoot, safeCategory, safeItem));

            EnsureWithinRoot(
                destinationPath,
                libraryRoot,
                $"O destino calculado saiu da biblioteca {libraryFolderName}.");
            EnsureNoReparsePoints(canonicalBase, destinationPath);
            if (IsWithinRoot(canonicalArchivePath, destinationPath))
                throw new InvalidDataException(
                    "O pacote compactado está dentro da pasta que seria substituída. " +
                    "Mova o pacote para Downloads antes de extrair.");
            if (Directory.Exists(destinationPath)
                && IsCompletedExtraction(destinationPath, canonicalArchivePath, safeCategory, safeItem))
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

            using var archive = ArchiveFactory.OpenArchive(
                canonicalArchivePath,
                new ReaderOptions { LeaveStreamOpen = false });

            EnsureSupportedArchive(archive);
            var plans = BuildAndValidatePlans(archive, cancellationToken);
            var totalUncompressedBytes = SumUncompressedBytes(plans);
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
            EnsureNoReparsePoints(canonicalBase, stagingContainer);
            Directory.CreateDirectory(stagingContainer);
            EnsureNoReparsePoints(canonicalBase, stagingContainer);
            stagingPath = Path.GetFullPath(Path.Combine(stagingContainer, Guid.NewGuid().ToString("N")));
            EnsureWithinRoot(stagingPath, stagingContainer, "A área temporária calculada é inválida.");
            Directory.CreateDirectory(stagingPath);

            foreach (var plan in plans)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var outputPath = Path.GetFullPath(Path.Combine(stagingPath, plan.RelativePath));
                EnsureWithinRoot(outputPath, stagingPath, "Uma entrada do pacote tentou sair da área segura.");

                if (plan.Entry.IsDirectory)
                {
                    Directory.CreateDirectory(outputPath);
                    continue;
                }

                Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
                await using var input = await plan.Entry.OpenEntryStreamAsync(cancellationToken);
                await using var output = new FileStream(
                    outputPath,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    _options.CopyBufferSize,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);

                var copied = await CopyEntryAsync(
                    input,
                    output,
                    plan.Entry.Size,
                    _options.CopyBufferSize,
                    cancellationToken);
                extractedBytes = checked(extractedBytes + copied);
                extractedFileCount++;

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

            WriteCompletionMarker(
                stagingPath,
                canonicalArchivePath,
                safeCategory,
                safeItem);

            EnsureNoReparsePoints(canonicalBase, Path.GetDirectoryName(destinationPath)!);
            Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
            EnsureNoReparsePoints(canonicalBase, Path.GetDirectoryName(destinationPath)!);
            Directory.Move(stagingPath, destinationPath);
            stagingPath = null;

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
            return new CatalogArchiveExtractionResult(
                CatalogArchiveExtractionStatus.Canceled,
                "Extração cancelada. O pacote compactado foi preservado e pode ser usado novamente.",
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
        CancellationToken cancellationToken)
    {
        var plannedEntries = new List<PlannedEntry>();
        var targets = new HashSet<string>(FileSystemPathComparer);

        foreach (var entry in archive.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (plannedEntries.Count >= _options.MaximumEntryCount)
                throw new InvalidDataException(
                    $"O pacote excede o limite de {_options.MaximumEntryCount:N0} entradas.");
            if (entry.IsEncrypted)
                throw new InvalidDataException("Pacotes protegidos por senha não são aceitos.");
            if (!string.IsNullOrWhiteSpace(entry.LinkTarget))
                throw new InvalidDataException(
                    $"A entrada '{entry.Key}' é um link e foi bloqueada por segurança.");
            if (entry.Size < 0)
                throw new InvalidDataException($"A entrada '{entry.Key}' possui tamanho inválido.");

            var relativePath = NormalizeArchiveEntryPath(entry.Key ?? string.Empty);
            if (relativePath.Equals(CompletionMarkerFileName, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException(
                    $"A entrada reservada '{entry.Key}' foi bloqueada.");
            if (!targets.Add(relativePath))
                throw new InvalidDataException(
                    $"O pacote contém caminhos duplicados: '{entry.Key}'.");
            plannedEntries.Add(new PlannedEntry(entry, relativePath));
        }

        if (plannedEntries.Count == 0)
            throw new InvalidDataException("O pacote não contém arquivos para extrair.");

        ValidatePathHierarchy(plannedEntries);
        return new ExtractionPlan(
            plannedEntries,
            plannedEntries.Count(entry => !entry.Entry.IsDirectory));
    }

    private static long SumUncompressedBytes(IEnumerable<PlannedEntry> entries)
    {
        long total = 0;
        foreach (var entry in entries)
        {
            if (!entry.Entry.IsDirectory) total = checked(total + entry.Entry.Size);
        }
        return total;
    }

    private static void ValidatePathHierarchy(IEnumerable<PlannedEntry> entries)
    {
        var filePaths = entries
            .Where(entry => !entry.Entry.IsDirectory)
            .Select(entry => entry.RelativePath)
            .ToHashSet(FileSystemPathComparer);

        foreach (var entry in entries)
        {
            var parent = Path.GetDirectoryName(entry.RelativePath);
            while (!string.IsNullOrEmpty(parent))
            {
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
        int bufferSize,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[bufferSize];
        long copied = 0;
        while (true)
        {
            var read = await input.ReadAsync(buffer, cancellationToken);
            if (read == 0) break;
            copied = checked(copied + read);
            if (copied > declaredSize)
                throw new InvalidDataException("Uma entrada excedeu o tamanho declarado no pacote.");
            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
        }

        await output.FlushAsync(cancellationToken);
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
        return canonicalPath;
    }

    private static string RequireBaseDirectory(string baseDirectory)
    {
        if (string.IsNullOrWhiteSpace(baseDirectory))
            throw new ArgumentException("Informe a pasta-base da biblioteca.", nameof(baseDirectory));
        var canonicalPath = Path.GetFullPath(baseDirectory);
        var root = Path.GetPathRoot(canonicalPath);
        if (string.IsNullOrEmpty(root) || !Directory.Exists(root))
            throw new DirectoryNotFoundException("A unidade escolhida não está disponível.");
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

    private static string NormalizeArchiveEntryPath(string key)
    {
        if (string.IsNullOrWhiteSpace(key))
            throw new InvalidDataException("O pacote contém uma entrada sem nome.");
        if (key.IndexOf('\0') >= 0)
            throw new InvalidDataException("O pacote contém um caminho inválido.");

        var normalized = key.Replace('\\', '/');
        if (normalized[0] == '/')
            throw new InvalidDataException($"O caminho absoluto '{key}' foi bloqueado.");

        var segments = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0)
            throw new InvalidDataException($"O caminho '{key}' é inválido.");

        foreach (var segment in segments)
        {
            if (segment is "." or "..")
                throw new InvalidDataException($"O caminho relativo '{key}' foi bloqueado.");
            ValidateArchivePathSegment(segment, key);
        }

        return Path.Combine(segments);
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
        string archivePath,
        string category,
        string item)
    {
        var archiveInfo = new FileInfo(archivePath);
        var marker = new ExtractionCompletionMarker(
            archiveInfo.Length,
            archiveInfo.LastWriteTimeUtc.Ticks,
            category,
            item);
        var markerPath = Path.Combine(stagingPath, CompletionMarkerFileName);
        File.WriteAllText(markerPath, JsonSerializer.Serialize(marker));
        try
        {
            File.SetAttributes(markerPath, FileAttributes.Hidden);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // The marker contents, not the cosmetic hidden attribute, provide recovery.
        }
    }

    private static bool IsCompletedExtraction(
        string destinationPath,
        string archivePath,
        string category,
        string item)
    {
        try
        {
            var markerPath = Path.Combine(destinationPath, CompletionMarkerFileName);
            if (!File.Exists(markerPath)) return false;
            var marker = JsonSerializer.Deserialize<ExtractionCompletionMarker>(
                File.ReadAllText(markerPath));
            if (marker is null) return false;
            var archiveInfo = new FileInfo(archivePath);
            return marker.ArchiveLength == archiveInfo.Length
                   && marker.ArchiveLastWriteUtcTicks == archiveInfo.LastWriteTimeUtc.Ticks
                   && marker.Category.Equals(category, StringComparison.Ordinal)
                   && marker.Item.Equals(item, StringComparison.Ordinal);
        }
        catch (Exception exception) when (exception is IOException
                                           or UnauthorizedAccessException
                                           or JsonException)
        {
            return false;
        }
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

    private static void EnsureNoReparsePoints(string rootPath, string candidatePath)
    {
        var canonicalRoot = Path.GetFullPath(rootPath);
        var canonicalCandidate = Path.GetFullPath(candidatePath);
        EnsureWithinRoot(
            canonicalCandidate,
            canonicalRoot,
            "O destino físico saiu da pasta escolhida.");

        var relative = Path.GetRelativePath(canonicalRoot, canonicalCandidate);
        if (relative == ".") return;
        var current = canonicalRoot;
        foreach (var segment in relative.Split(
                     [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                     StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            FileAttributes attributes;
            try
            {
                attributes = File.GetAttributes(current);
            }
            catch (FileNotFoundException)
            {
                continue;
            }
            catch (DirectoryNotFoundException)
            {
                continue;
            }

            if ((attributes & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException(
                    $"O destino contém um atalho ou junção não autorizado: {current}");
        }
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
            if ((File.GetAttributes(stagingPath) & FileAttributes.ReparsePoint) != 0) return;
            Directory.Delete(stagingPath, recursive: true);
        }
        catch (IOException)
        {
            // A future retry uses another GUID and remains independent from this staging directory.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private sealed record PlannedEntry(IArchiveEntry Entry, string RelativePath);

    private sealed record ExtractionCompletionMarker(
        long ArchiveLength,
        long ArchiveLastWriteUtcTicks,
        string Category,
        string Item);

    private sealed class ExtractionPlan : List<PlannedEntry>
    {
        public ExtractionPlan(IEnumerable<PlannedEntry> entries, int fileCount) : base(entries)
        {
            FileCount = fileCount;
        }

        public int FileCount { get; }
    }
}
