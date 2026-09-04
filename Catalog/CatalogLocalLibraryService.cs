using System.Buffers;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace TurboBoxManager.Catalog;

public enum CatalogLocalGameStatus
{
    Scanning,
    NotDownloaded,
    Downloaded,
    Incomplete,
    Unrecognized,
    Unsafe,
    Unavailable
}

public sealed class CatalogLocalGameEntry : INotifyPropertyChanged
{
    private static readonly SolidColorBrush DownloadedBrush = CreateBrush("#9DFF00");
    private static readonly SolidColorBrush MissingBrush = CreateBrush("#8E988C");
    private static readonly SolidColorBrush WarningBrush = CreateBrush("#FFB020");
    private static readonly SolidColorBrush UnsafeBrush = CreateBrush("#FF5060");
    private CatalogLocalGameStatus _status = CatalogLocalGameStatus.Scanning;
    private string _statusDetail = "Analisando a pasta de ROMs...";
    private string _localPath = string.Empty;
    private readonly string _category;
    private readonly CatalogLocalOrphanInspection? _orphan;

    public CatalogLocalGameEntry(CatalogItem item)
    {
        Item = item ?? throw new ArgumentNullException(nameof(item));
        _category = item.Category;
    }

    internal CatalogLocalGameEntry(
        string category,
        CatalogLocalOrphanInspection orphan)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(category);
        _orphan = orphan ?? throw new ArgumentNullException(nameof(orphan));
        _category = category;
        _localPath = orphan.LocalPath;
        _statusDetail = orphan.Detail;
        _status = orphan.Status;
    }

    public CatalogItem? Item { get; }
    internal CatalogLocalOrphanInspection? Orphan => _orphan;
    public bool IsOrphan => _orphan is not null;
    public string ItemId => Item?.Id ?? $"orphan:{_orphan!.LocalPath}";
    public string Title => Item?.Title ?? _orphan!.Name;
    public string Subtitle => Item?.Subtitle
                              ?? (_orphan!.IsDirectory
                                  ? "Pasta local não reconhecida pelo catálogo"
                                  : "Arquivo local não reconhecido pelo catálogo");
    public string Category => Item?.Category ?? _category;
    public string ImageAltText => Item?.ImageAltText ?? "Item local não reconhecido";
    public BitmapSource? Thumbnail160 => Item?.Thumbnail160;

    public CatalogLocalGameStatus Status
    {
        get => _status;
        private set
        {
            if (_status == value) return;
            _status = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(StatusLabel));
            OnPropertyChanged(nameof(StatusBrush));
            OnPropertyChanged(nameof(CanDelete));
            OnPropertyChanged(nameof(DeleteLabel));
        }
    }

    public string StatusDetail
    {
        get => _statusDetail;
        private set
        {
            if (_statusDetail == value) return;
            _statusDetail = value;
            OnPropertyChanged();
        }
    }

    public string LocalPath
    {
        get => _localPath;
        private set
        {
            if (_localPath == value) return;
            _localPath = value;
            OnPropertyChanged();
        }
    }

    public string StatusLabel => Status switch
    {
        CatalogLocalGameStatus.Scanning => "ANALISANDO",
        CatalogLocalGameStatus.NotDownloaded => "NÃO BAIXADO",
        CatalogLocalGameStatus.Downloaded => "BAIXADO",
        CatalogLocalGameStatus.Incomplete => "ARQUIVO INCOMPLETO",
        CatalogLocalGameStatus.Unrecognized => "NÃO RECONHECIDO",
        CatalogLocalGameStatus.Unsafe => "PRECISA DE REVISÃO",
        _ => "INDISPONÍVEL"
    };

    public Brush StatusBrush => Status switch
    {
        CatalogLocalGameStatus.Downloaded => DownloadedBrush,
        CatalogLocalGameStatus.Incomplete => WarningBrush,
        CatalogLocalGameStatus.Unrecognized => WarningBrush,
        CatalogLocalGameStatus.Unsafe => UnsafeBrush,
        _ => MissingBrush
    };

    public bool CanDelete => _orphan?.CanDelete
                             ?? Status is CatalogLocalGameStatus.Downloaded
                                 or CatalogLocalGameStatus.Incomplete;
    public string DeleteLabel => Status == CatalogLocalGameStatus.Incomplete
        ? "LIMPAR"
        : "EXCLUIR";

    internal void Apply(CatalogLocalGameInspection inspection)
    {
        ArgumentNullException.ThrowIfNull(inspection);
        LocalPath = inspection.ExpectedPath;
        StatusDetail = inspection.Detail;
        Status = inspection.Status;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    private static SolidColorBrush CreateBrush(string value)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(value));
        brush.Freeze();
        return brush;
    }
}

internal sealed record CatalogLocalGameInspection(
    CatalogLocalGameStatus Status,
    string ExpectedPath,
    string Detail);

internal sealed record CatalogLocalOrphanInspection(
    string Name,
    string LocalPath,
    bool IsDirectory,
    CatalogLocalGameStatus Status,
    string Detail)
{
    public bool CanDelete => Status == CatalogLocalGameStatus.Unrecognized;
}

internal sealed record CatalogLocalSystemInspection(
    string CategoryPath,
    CatalogLocalGameStatus CategoryStatus,
    string CategoryDetail,
    IReadOnlyList<CatalogLocalGameInspection> CatalogItems,
    IReadOnlyList<CatalogLocalOrphanInspection> Orphans);

internal sealed class CatalogLocalLibraryService
{
    private const int CompletionMarkerSchemaVersion = 2;
    private const int MaximumCompletionMarkerBytes = 64 * 1024 * 1024;
    private const int MaximumInventoryEntries = 100_000;
    private const int MaximumRelativePathLength = 1_024;
    private const int MaximumPathDepth = 24;
    private const int MaximumPathSegmentLength = 180;
    private const int MaximumCategoryEntries = 100_000;
    private const int MaximumDeletionEntries = 2_500_000;
    private static readonly StringComparer LocalPathComparer =
        OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;
    private static readonly JsonSerializerOptions MarkerJsonOptions = new()
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 8
    };

    private readonly CatalogDownloadService _downloadService;

    internal CatalogLocalLibraryService(CatalogDownloadService downloadService)
    {
        _downloadService = downloadService
                           ?? throw new ArgumentNullException(nameof(downloadService));
    }

    internal Task<IReadOnlyList<CatalogLocalGameInspection>> InspectAsync(
        string gameLibraryRoot,
        IReadOnlyList<CatalogItem> items,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(items);
        return Task.Run<IReadOnlyList<CatalogLocalGameInspection>>(() =>
        {
            var completedDirectDownloads = PrepareDirectDownloads(
                gameLibraryRoot,
                items,
                cancellationToken);
            var results = new List<CatalogLocalGameInspection>(items.Count);
            foreach (var item in items)
            {
                cancellationToken.ThrowIfCancellationRequested();
                results.Add(Inspect(
                    gameLibraryRoot,
                    item,
                    completedDirectDownloads,
                    cancellationToken));
            }
            return results;
        }, cancellationToken);
    }

    internal Task<CatalogLocalSystemInspection> InspectSystemAsync(
        string gameLibraryRoot,
        string category,
        IReadOnlyList<CatalogItem> items,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(category);
        ArgumentNullException.ThrowIfNull(items);
        return Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var completedDirectDownloads = PrepareDirectDownloads(
                gameLibraryRoot,
                items,
                cancellationToken);
            var catalogItems = new List<CatalogLocalGameInspection>(items.Count);
            foreach (var item in items)
            {
                cancellationToken.ThrowIfCancellationRequested();
                catalogItems.Add(Inspect(
                    gameLibraryRoot,
                    item,
                    completedDirectDownloads,
                    cancellationToken));
            }

            string categoryPath;
            try
            {
                categoryPath = CatalogArchiveExtractor.BuildCategoryDestinationPath(
                    gameLibraryRoot,
                    category);
            }
            catch (Exception exception) when (IsLocalInspectionException(exception))
            {
                return new CatalogLocalSystemInspection(
                    string.Empty,
                    CatalogLocalGameStatus.Unsafe,
                    $"A pasta física do sistema não pôde ser validada: {exception.Message}",
                    catalogItems,
                    Array.Empty<CatalogLocalOrphanInspection>());
            }

            try
            {
                if (HasAnyInstalledReceipt(gameLibraryRoot))
                    return new CatalogLocalSystemInspection(
                        categoryPath,
                        CatalogLocalGameStatus.Unsafe,
                        "Há comprovantes de pacote mesclado na biblioteca; por segurança, nenhum item físico será oferecido como órfão removível.",
                        catalogItems,
                        Array.Empty<CatalogLocalOrphanInspection>());
                var recognizedPaths = BuildRecognizedCategoryPaths(
                    gameLibraryRoot,
                    categoryPath,
                    items);
                var categoryInspection = InspectCategoryChildren(
                    categoryPath,
                    recognizedPaths,
                    cancellationToken);
                return new CatalogLocalSystemInspection(
                    categoryPath,
                    categoryInspection.Status,
                    categoryInspection.Detail,
                    catalogItems,
                    categoryInspection.Orphans);
            }
            catch (Exception exception) when (IsLocalInspectionException(exception))
            {
                return new CatalogLocalSystemInspection(
                    categoryPath,
                    CatalogLocalGameStatus.Unsafe,
                    $"A pasta física do sistema não pôde ser analisada: {exception.Message}",
                    catalogItems,
                    Array.Empty<CatalogLocalOrphanInspection>());
            }
        }, cancellationToken);
    }

    internal static Task<bool> DeleteOrphanAsync(
        string gameLibraryRoot,
        string category,
        CatalogLocalOrphanInspection orphan,
        IReadOnlyList<CatalogItem> currentItems,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(category);
        ArgumentNullException.ThrowIfNull(orphan);
        ArgumentNullException.ThrowIfNull(currentItems);
        return Task.Run(() => RunUnderLibraryMutationGate(gameLibraryRoot, () =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var categoryPath = CatalogArchiveExtractor.BuildCategoryDestinationPath(
                gameLibraryRoot,
                category);
            if (HasAnyInstalledReceipt(gameLibraryRoot))
                throw new InvalidDataException(
                    "Há comprovantes de pacote mesclado na biblioteca; a exclusão como órfão foi bloqueada por segurança.");
            var candidatePath = PathIdentity.Canonicalize(orphan.LocalPath);
            EnsureDirectChild(candidatePath, categoryPath);
            if (!Path.GetFileName(candidatePath).Equals(
                    orphan.Name,
                    OperatingSystem.IsWindows()
                        ? StringComparison.OrdinalIgnoreCase
                        : StringComparison.Ordinal))
                throw new InvalidDataException(
                    "O nome do item local não corresponde ao caminho que será excluído.");

            var recognizedPaths = BuildRecognizedCategoryPaths(
                gameLibraryRoot,
                categoryPath,
                currentItems);
            if (recognizedPaths.Contains(candidatePath))
                throw new InvalidDataException(
                    "O item passou a pertencer ao catálogo e não será excluído como órfão.");

            if (!File.Exists(candidatePath) && !Directory.Exists(candidatePath)) return false;
            using var categoryTree = PathIdentity.OpenDirectoryTree(categoryPath);
            var current = InspectUnrecognizedChild(
                categoryTree,
                candidatePath,
                cancellationToken);
            categoryTree.Revalidate();
            if (!current.CanDelete)
                throw new InvalidDataException(
                    "O item órfão não passou pela revalidação de segurança e não será excluído.");
            if (current.IsDirectory != orphan.IsDirectory)
                throw new IOException(
                    "O tipo do item órfão mudou desde a análise e a exclusão foi recusada.");

            cancellationToken.ThrowIfCancellationRequested();
            if (current.IsDirectory)
            {
                EnsureDeletionTreeContainsNoReparsePoints(
                    candidatePath,
                    cancellationToken);
                return PathIdentity.DeleteDirectoryTreeExact(
                    candidatePath,
                    categoryPath,
                    MaximumDeletionEntries);
            }

            return PathIdentity.DeleteFileExact(candidatePath, categoryPath);
        }), cancellationToken);
    }

    internal Task<bool> DeleteAsync(
        string gameLibraryRoot,
        CatalogItem item,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(item);
        return Task.Run(() => RunUnderLibraryMutationGate(gameLibraryRoot, () =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var completedDirectDownloads = PrepareDirectDownloads(
                gameLibraryRoot,
                [item],
                cancellationToken);
            var inspection = Inspect(
                gameLibraryRoot,
                item,
                completedDirectDownloads,
                cancellationToken);
            if (inspection.Status is CatalogLocalGameStatus.NotDownloaded
                or CatalogLocalGameStatus.Unavailable)
                return false;
            if (inspection.Status is CatalogLocalGameStatus.Unrecognized
                or CatalogLocalGameStatus.Unsafe)
                throw new InvalidDataException(
                    "O caminho local não passou pela validação de segurança e não será excluído.");

            cancellationToken.ThrowIfCancellationRequested();
            if (!item.HasAuthorizedArtifact)
            {
                var directMatches = FindCompletedDirectDownloads(
                    completedDirectDownloads,
                    item.Id);
                if (directMatches.Length == 1
                    && LocalPathsEqual(
                        directMatches[0].LocalFilePath,
                        inspection.ExpectedPath))
                    return _downloadService.DiscardCompletedDirectDownload(
                        gameLibraryRoot,
                        item.Id,
                        directMatches[0].LocalFilePath,
                        cancellationToken);
                if (directMatches.Length > 0)
                    throw new InvalidDataException(
                        "O conteúdo direto local ficou ambíguo e não será excluído automaticamente.");

                EnsureDeletionTreeContainsNoReparsePoints(
                    inspection.ExpectedPath,
                    cancellationToken);
                return PathIdentity.DeleteDirectoryTreeExact(
                    inspection.ExpectedPath,
                    gameLibraryRoot,
                    MaximumDeletionEntries);
            }

            var artifact = RequireArtifact(item);
            if (IsMergedMarkerDirectory(gameLibraryRoot, inspection.ExpectedPath))
                throw new InvalidDataException(
                    "Este pacote foi integrado à pasta mestre de ROMs. Por segurança, exclua seus arquivos pela pasta do sistema.");
            if (artifact.ExtractPolicy != CatalogExtractPolicy.ExtractArchive
                && !CatalogArchivePolicy.IsRecognizedArchive(artifact))
                return PathIdentity.DeleteFileExact(inspection.ExpectedPath, gameLibraryRoot);

            EnsureDeletionTreeContainsNoReparsePoints(
                inspection.ExpectedPath,
                cancellationToken);
            return PathIdentity.DeleteDirectoryTreeExact(
                inspection.ExpectedPath,
                gameLibraryRoot,
                MaximumDeletionEntries);
        }), cancellationToken);
    }

    private static bool RunUnderLibraryMutationGate(
        string gameLibraryRoot,
        Func<bool> action) => CatalogGamePackageOrganizer.WithLibraryMutationLock(
        gameLibraryRoot,
        action);

    internal string BuildExpectedPath(string gameLibraryRoot, CatalogItem item)
    {
        var artifact = RequireArtifact(item);
        if (artifact.ExtractPolicy == CatalogExtractPolicy.ExtractArchive
            || CatalogArchivePolicy.IsRecognizedArchive(artifact))
        {
            var stablePath = CatalogArchiveExtractor.BuildGameDestinationPath(
                gameLibraryRoot,
                string.IsNullOrWhiteSpace(item.Category) ? item.CategoryId : item.Category,
                item.Id);
            var markerDirectory = CatalogGamePackageOrganizer.BuildMarkerDirectory(
                gameLibraryRoot,
                Path.GetFileName(stablePath));
            return Directory.Exists(markerDirectory) ? markerDirectory : stablePath;
        }
        return artifact.ExtractPolicy == CatalogExtractPolicy.ExtractArchive
            ? CatalogArchiveExtractor.BuildGameDestinationPath(
                gameLibraryRoot,
                string.IsNullOrWhiteSpace(item.Category) ? item.CategoryId : item.Category,
                item.Id)
            : _downloadService.BuildSafeDestinationPath(gameLibraryRoot, item);
    }

    private List<CatalogCompletedDirectDownload> PrepareDirectDownloads(
        string gameLibraryRoot,
        IReadOnlyList<CatalogItem> items,
        CancellationToken cancellationToken)
    {
        var records = _downloadService
            .DiscoverCompletedDirectDownloads(gameLibraryRoot, cancellationToken)
            .ToList();
        foreach (var item in items)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!item.HasAuthorizedArtifact
                || item.Artifact!.ExtractPolicy != CatalogExtractPolicy.None
                || CatalogArchivePolicy.IsRecognizedArchive(item.Artifact))
                continue;

            string expectedPath;
            try
            {
                expectedPath = _downloadService.BuildSafeDestinationPath(
                    gameLibraryRoot,
                    item);
            }
            catch (Exception exception) when (IsLocalInspectionException(exception))
            {
                continue;
            }

            if (records.Any(record =>
                    record.ItemId.Equals(item.Id, StringComparison.Ordinal)
                    && LocalPathsEqual(record.LocalFilePath, expectedPath)))
                continue;

            var migrated = _downloadService.TryRecordCompletedDirectDownload(
                gameLibraryRoot,
                item,
                cancellationToken);
            if (migrated is not null) records.Add(migrated);
        }
        return records;
    }

    private static CatalogCompletedDirectDownload[] FindCompletedDirectDownloads(
        IReadOnlyList<CatalogCompletedDirectDownload> records,
        string itemId) => records
        .Where(record => record.ItemId.Equals(itemId, StringComparison.Ordinal))
        .ToArray();

    private static bool LocalPathsEqual(string firstPath, string secondPath)
    {
        try
        {
            return LocalPathComparer.Equals(
                PathIdentity.Canonicalize(firstPath),
                PathIdentity.Canonicalize(secondPath));
        }
        catch (Exception exception) when (IsLocalInspectionException(exception))
        {
            return false;
        }
    }

    private CatalogLocalGameInspection Inspect(
        string gameLibraryRoot,
        CatalogItem item,
        IReadOnlyList<CatalogCompletedDirectDownload> completedDirectDownloads,
        CancellationToken cancellationToken)
    {
        var expectedPath = string.Empty;
        try
        {
            if (!item.HasAuthorizedArtifact)
            {
                expectedPath = CatalogArchiveExtractor.BuildGameDestinationPath(
                    gameLibraryRoot,
                    string.IsNullOrWhiteSpace(item.Category) ? item.CategoryId : item.Category,
                    item.Id);
                var directMatches = FindCompletedDirectDownloads(
                    completedDirectDownloads,
                    item.Id);
                var stablePathExists = Directory.Exists(expectedPath)
                                       || File.Exists(expectedPath);
                if (directMatches.Length == 1 && !stablePathExists)
                    return new CatalogLocalGameInspection(
                        CatalogLocalGameStatus.Downloaded,
                        directMatches[0].LocalFilePath,
                        "Arquivo direto concluído e validado pelo estado local da autorização anterior.");
                if (directMatches.Length > 0)
                    return new CatalogLocalGameInspection(
                        CatalogLocalGameStatus.Unsafe,
                        directMatches[0].LocalFilePath,
                        directMatches.Length > 1
                            ? "Mais de um arquivo direto afirma pertencer a este item; revise antes de excluir."
                            : "Há conteúdo direto e uma instalação estável para o mesmo item; revise antes de excluir.");
                return InspectUnavailableCatalogDirectory(
                    expectedPath,
                    cancellationToken);
            }

            expectedPath = BuildExpectedPath(gameLibraryRoot, item);
            var artifact = RequireArtifact(item);
            if (artifact.ExtractPolicy == CatalogExtractPolicy.ExtractArchive
                || CatalogArchivePolicy.IsRecognizedArchive(artifact))
                return InspectExtractedDirectory(
                    expectedPath,
                    item,
                    artifact,
                    cancellationToken,
                    IsMergedMarkerDirectory(gameLibraryRoot, expectedPath)
                        ? gameLibraryRoot
                        : null);

            var authorizedDirectMatches = FindCompletedDirectDownloads(
                    completedDirectDownloads,
                    item.Id)
                .Where(record => LocalPathsEqual(record.LocalFilePath, expectedPath))
                .ToArray();
            if (authorizedDirectMatches.Length == 1)
                return new CatalogLocalGameInspection(
                    CatalogLocalGameStatus.Downloaded,
                    expectedPath,
                    "Arquivo direto concluído; tamanho e SHA-256 foram conferidos.");
            if (authorizedDirectMatches.Length > 1)
                return new CatalogLocalGameInspection(
                    CatalogLocalGameStatus.Unsafe,
                    expectedPath,
                    "Há estados locais duplicados para o mesmo arquivo direto.");
            return InspectDirectFile(expectedPath, artifact, cancellationToken);
        }
        catch (Exception exception) when (IsLocalInspectionException(exception))
        {
            return new CatalogLocalGameInspection(
                CatalogLocalGameStatus.Unsafe,
                expectedPath,
                $"A pasta local não pôde ser validada: {exception.Message}");
        }
    }

    private static CatalogLocalGameInspection InspectUnavailableCatalogDirectory(
        string expectedPath,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!Directory.Exists(expectedPath))
        {
            if (File.Exists(expectedPath))
                return new CatalogLocalGameInspection(
                    CatalogLocalGameStatus.Unsafe,
                    expectedPath,
                    "Há um arquivo no caminho reservado à instalação deste item.");
            return new CatalogLocalGameInspection(
                CatalogLocalGameStatus.Unavailable,
                expectedPath,
                "Este item não possui conteúdo autorizado nesta sessão e não está instalado.");
        }

        using var destinationTree = PathIdentity.OpenDirectoryTree(expectedPath);
        destinationTree.Revalidate();
        return new CatalogLocalGameInspection(
            CatalogLocalGameStatus.Incomplete,
            expectedPath,
            "Conteúdo local encontrado, mas o item não possui artefato autorizado nesta sessão.");
    }

    private static CatalogLocalGameInspection InspectDirectFile(
        string expectedPath,
        CatalogArtifactDescriptor artifact,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!File.Exists(expectedPath))
        {
            if (Directory.Exists(expectedPath))
                return new CatalogLocalGameInspection(
                    CatalogLocalGameStatus.Unsafe,
                    expectedPath,
                    "Há uma pasta onde deveria existir o arquivo deste jogo.");
            return new CatalogLocalGameInspection(
                CatalogLocalGameStatus.NotDownloaded,
                expectedPath,
                "Nenhum arquivo local foi encontrado para este jogo.");
        }

        var parent = Path.GetDirectoryName(expectedPath)
                     ?? throw new InvalidDataException("O arquivo local não possui pasta-pai.");
        using var tree = PathIdentity.OpenDirectoryTree(parent);
        using var stream = tree.OpenFile(
            expectedPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            4 * 1024,
            FileOptions.SequentialScan);
        cancellationToken.ThrowIfCancellationRequested();
        var identity = PathIdentity.CaptureFileIdentity(stream.SafeFileHandle, expectedPath);
        tree.Revalidate();
        _ = PathIdentity.RevalidateFile(stream.SafeFileHandle, expectedPath, identity);
        if (stream.Length <= 0
            || artifact.ContentLength > 0 && stream.Length != artifact.ContentLength)
            return new CatalogLocalGameInspection(
                CatalogLocalGameStatus.Incomplete,
                expectedPath,
                "O arquivo existe, mas o tamanho não corresponde ao pacote concluído.");

        return new CatalogLocalGameInspection(
            CatalogLocalGameStatus.Incomplete,
            expectedPath,
            "O arquivo existe, mas não pôde ter tamanho e SHA-256 confirmados pelo estado autorizado.");
    }

    private static CatalogLocalGameInspection InspectExtractedDirectory(
        string expectedPath,
        CatalogItem item,
        CatalogArtifactDescriptor artifact,
        CancellationToken cancellationToken,
        string? inventoryBaseRoot = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!Directory.Exists(expectedPath))
        {
            if (File.Exists(expectedPath))
                return new CatalogLocalGameInspection(
                    CatalogLocalGameStatus.Unsafe,
                    expectedPath,
                    "Há um arquivo onde deveria existir a pasta extraída deste jogo.");
            return new CatalogLocalGameInspection(
                CatalogLocalGameStatus.NotDownloaded,
                expectedPath,
                "Nenhuma instalação local foi encontrada para este jogo.");
        }

        using var destinationTree = PathIdentity.OpenDirectoryTree(expectedPath);
        var markerPath = Path.Combine(
            expectedPath,
            CatalogArchiveExtractor.CompletionMarkerFileName);
        if (!File.Exists(markerPath))
            return new CatalogLocalGameInspection(
                CatalogLocalGameStatus.Incomplete,
                expectedPath,
                "A pasta existe, mas não possui o comprovante de extração concluída.");

        ExtractionCompletionMarker? marker;
        using (var markerStream = destinationTree.OpenFile(
                   markerPath,
                   FileMode.Open,
                   FileAccess.Read,
                   FileShare.Read,
                   64 * 1024,
                   FileOptions.SequentialScan))
        {
            if (markerStream.Length is <= 0 or > MaximumCompletionMarkerBytes)
                return new CatalogLocalGameInspection(
                    CatalogLocalGameStatus.Incomplete,
                    expectedPath,
                    "O comprovante da extração está incompleto.");
            try
            {
                marker = JsonSerializer.Deserialize<ExtractionCompletionMarker>(
                    markerStream,
                    MarkerJsonOptions);
            }
            catch (JsonException)
            {
                return new CatalogLocalGameInspection(
                    CatalogLocalGameStatus.Incomplete,
                    expectedPath,
                    "O comprovante da extração contém JSON inválido.");
            }
        }

        var category = string.IsNullOrWhiteSpace(item.Category) ? item.CategoryId : item.Category;
        var markerInventory = marker?.Inventory;
        if (marker is null
            || marker.SchemaVersion != CompletionMarkerSchemaVersion
            || marker.ArchiveLength != artifact.ContentLength
            || !string.Equals(marker.ArchiveSha256, artifact.Sha256, StringComparison.Ordinal)
            || !string.Equals(marker.ManifestIdentity, artifact.ManifestIdentity, StringComparison.Ordinal)
            || !string.Equals(marker.ArtifactId, artifact.ArtifactId, StringComparison.Ordinal)
            || marker.ArtifactVersion != artifact.ArtifactVersion
            || !string.Equals(marker.Category, category, StringComparison.Ordinal)
            || !string.Equals(marker.StableItemId, item.Id, StringComparison.OrdinalIgnoreCase)
            || markerInventory is null
            || markerInventory.Length > MaximumInventoryEntries)
            return new CatalogLocalGameInspection(
                CatalogLocalGameStatus.Incomplete,
                expectedPath,
                "A instalação existe, mas seu comprovante não corresponde ao catálogo atual.");

        string? previousRelativePath = null;
        foreach (var inventoryEntry in markerInventory)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (inventoryEntry is null
                || !TryNormalizeInventoryPath(
                    inventoryEntry.RelativePath,
                    out var normalizedRelativePath)
                || inventoryEntry.Length < 0
                || !IsCanonicalSha256(inventoryEntry.Sha256)
                || previousRelativePath is not null
                && string.CompareOrdinal(previousRelativePath, normalizedRelativePath) >= 0)
                return new CatalogLocalGameInspection(
                    CatalogLocalGameStatus.Incomplete,
                    expectedPath,
                    "O inventário da instalação local é inválido.");

            previousRelativePath = normalizedRelativePath;
            var inventoryRoot = inventoryBaseRoot is null
                ? expectedPath
                : Path.GetFullPath(inventoryBaseRoot);
            var localFile = Path.GetFullPath(Path.Combine(
                inventoryRoot,
                normalizedRelativePath.Replace('/', Path.DirectorySeparatorChar)));
            EnsureWithin(localFile, inventoryRoot);
            if (!File.Exists(localFile))
                return new CatalogLocalGameInspection(
                    CatalogLocalGameStatus.Incomplete,
                    expectedPath,
                    "A instalação local perdeu um ou mais arquivos.");

            var parent = Path.GetDirectoryName(localFile)
                         ?? throw new InvalidDataException(
                             "Um arquivo do inventário não possui pasta-pai.");
            using var parentTree = PathIdentity.OpenDirectoryTree(parent);
            using var localStream = parentTree.OpenFile(
                localFile,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                4 * 1024,
                FileOptions.SequentialScan);
            var identity = PathIdentity.CaptureFileIdentity(
                localStream.SafeFileHandle,
                localFile);
            parentTree.Revalidate();
            _ = PathIdentity.RevalidateFile(localStream.SafeFileHandle, localFile, identity);
            if (localStream.Length != inventoryEntry.Length)
                return new CatalogLocalGameInspection(
                    CatalogLocalGameStatus.Incomplete,
                    expectedPath,
                    "A instalação local possui arquivo incompleto ou alterado.");

            var actualSha256 = ComputeSha256(localStream, cancellationToken);
            parentTree.Revalidate();
            _ = PathIdentity.RevalidateFile(localStream.SafeFileHandle, localFile, identity);
            var expectedSha256 = Convert.FromHexString(inventoryEntry.Sha256!);
            if (!CryptographicOperations.FixedTimeEquals(actualSha256, expectedSha256))
                return new CatalogLocalGameInspection(
                    CatalogLocalGameStatus.Incomplete,
                    expectedPath,
                    "A instalação local possui arquivo incompleto ou alterado.");
        }

        destinationTree.Revalidate();
        return new CatalogLocalGameInspection(
            CatalogLocalGameStatus.Downloaded,
            expectedPath,
            "Extração local concluída e inventário conferido.");
    }

    private static byte[] ComputeSha256(
        Stream stream,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        stream.Position = 0;
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var bytesRead = stream.Read(buffer, 0, buffer.Length);
                if (bytesRead == 0) break;
                hash.AppendData(buffer, 0, bytesRead);
            }

            cancellationToken.ThrowIfCancellationRequested();
            return hash.GetHashAndReset();
        }
        finally
        {
            CryptographicOperations.ZeroMemory(buffer);
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static HashSet<string> BuildRecognizedCategoryPaths(
        string gameLibraryRoot,
        string requestedCategoryPath,
        IReadOnlyList<CatalogItem> items)
    {
        var recognized = new HashSet<string>(LocalPathComparer);
        foreach (var item in items)
        {
            if (item.HasAuthorizedArtifact
                && item.Artifact!.ExtractPolicy != CatalogExtractPolicy.ExtractArchive
                && !CatalogArchivePolicy.IsRecognizedArchive(item.Artifact))
                continue;

            try
            {
                var itemCategory = string.IsNullOrWhiteSpace(item.Category)
                    ? item.CategoryId
                    : item.Category;
                var itemPath = CatalogArchiveExtractor.BuildGameDestinationPath(
                    gameLibraryRoot,
                    itemCategory,
                    item.Id);
                var itemCategoryPath = Path.GetDirectoryName(itemPath);
                if (itemCategoryPath is not null
                    && LocalPathComparer.Equals(
                        PathIdentity.Canonicalize(itemCategoryPath),
                        PathIdentity.Canonicalize(requestedCategoryPath)))
                    recognized.Add(PathIdentity.Canonicalize(itemPath));
            }
            catch (Exception exception) when (IsLocalInspectionException(exception))
            {
                // An invalid catalog identity is already reported by the catalog-item
                // inspection. It must not reserve an arbitrary physical child here.
            }
        }
        return recognized;
    }

    private static bool HasAnyInstalledReceipt(string gameLibraryRoot)
    {
        var receiptRoot = PathIdentity.Canonicalize(Path.Combine(
            gameLibraryRoot,
            CatalogGamePackageOrganizer.ReceiptFolderName));
        if (File.Exists(receiptRoot)) return true;
        if (!Directory.Exists(receiptRoot)) return false;

        using var receiptTree = PathIdentity.OpenDirectoryTree(receiptRoot);
        using var entries = Directory.EnumerateFileSystemEntries(receiptRoot).GetEnumerator();
        var containsAnyEntry = entries.MoveNext();
        receiptTree.Revalidate();
        return containsAnyEntry;
    }

    private static CategoryChildrenInspection InspectCategoryChildren(
        string categoryPath,
        HashSet<string> recognizedPaths,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!Directory.Exists(categoryPath))
        {
            if (File.Exists(categoryPath))
                return new CategoryChildrenInspection(
                    CatalogLocalGameStatus.Unsafe,
                    "Há um arquivo onde deveria existir a pasta deste sistema.",
                    Array.Empty<CatalogLocalOrphanInspection>());
            return new CategoryChildrenInspection(
                CatalogLocalGameStatus.NotDownloaded,
                "A pasta física deste sistema ainda não existe.",
                Array.Empty<CatalogLocalOrphanInspection>());
        }

        using var categoryTree = PathIdentity.OpenDirectoryTree(categoryPath);
        var orphans = new List<CatalogLocalOrphanInspection>();
        var visited = 0;
        foreach (var child in Directory.EnumerateFileSystemEntries(categoryPath))
        {
            cancellationToken.ThrowIfCancellationRequested();
            visited = checked(visited + 1);
            if (visited > MaximumCategoryEntries)
                throw new InvalidDataException(
                    "A pasta do sistema excedeu o limite seguro de itens físicos.");

            var canonicalChild = PathIdentity.Canonicalize(child);
            EnsureDirectChild(canonicalChild, categoryPath);
            if (recognizedPaths.Contains(canonicalChild)) continue;
            orphans.Add(InspectUnrecognizedChild(
                categoryTree,
                canonicalChild,
                cancellationToken));
        }
        categoryTree.Revalidate();
        orphans.Sort((left, right) =>
            LocalPathComparer.Compare(left.Name, right.Name));
        return new CategoryChildrenInspection(
            CatalogLocalGameStatus.Downloaded,
            orphans.Count == 0
                ? "Pasta física conferida; nenhum item não reconhecido foi encontrado."
                : $"Pasta física conferida; {orphans.Count} item(ns) não reconhecido(s).",
            orphans);
    }

    private static CatalogLocalOrphanInspection InspectUnrecognizedChild(
        PathIdentity.DirectoryTreeLease categoryTree,
        string childPath,
        CancellationToken cancellationToken)
    {
        var name = Path.GetFileName(childPath);
        var isDirectory = false;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var attributes = File.GetAttributes(childPath);
            isDirectory = (attributes & FileAttributes.Directory) != 0;
            if ((attributes & FileAttributes.ReparsePoint) != 0)
                return new CatalogLocalOrphanInspection(
                    name,
                    childPath,
                    isDirectory,
                    CatalogLocalGameStatus.Unsafe,
                    "Link, junction ou reparse point não reconhecido; a aplicação não o seguirá nem excluirá.");

            if (isDirectory)
            {
                using var childTree = PathIdentity.OpenDirectoryTree(childPath);
                childTree.Revalidate();
            }
            else
            {
                using var stream = categoryTree.OpenFile(
                    childPath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read,
                    1,
                    FileOptions.SequentialScan);
                var identity = PathIdentity.CaptureFileIdentity(
                    stream.SafeFileHandle,
                    childPath);
                categoryTree.Revalidate();
                _ = PathIdentity.RevalidateFile(
                    stream.SafeFileHandle,
                    childPath,
                    identity);
            }

            return new CatalogLocalOrphanInspection(
                name,
                childPath,
                isDirectory,
                CatalogLocalGameStatus.Unrecognized,
                isDirectory
                    ? "Pasta local não vinculada a nenhum jogo deste sistema."
                    : "Arquivo local não vinculado a nenhum jogo deste sistema.");
        }
        catch (Exception exception) when (IsLocalInspectionException(exception))
        {
            return new CatalogLocalOrphanInspection(
                name,
                childPath,
                isDirectory,
                CatalogLocalGameStatus.Unsafe,
                $"O item local não pôde ser validado com segurança: {exception.Message}");
        }
    }

    private static void EnsureDeletionTreeContainsNoReparsePoints(
        string directoryPath,
        CancellationToken cancellationToken)
    {
        var pending = new Stack<string>();
        var visited = 0;
        pending.Push(PathIdentity.Canonicalize(directoryPath));
        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var current = pending.Pop();
            using var currentTree = PathIdentity.OpenDirectoryTree(current);
            foreach (var child in Directory.EnumerateFileSystemEntries(current))
            {
                cancellationToken.ThrowIfCancellationRequested();
                visited = checked(visited + 1);
                if (visited > MaximumDeletionEntries)
                    throw new InvalidDataException(
                        "A limpeza segura excedeu o limite de entradas.");
                var attributes = File.GetAttributes(child);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException(
                        "A limpeza foi recusada porque a árvore contém um reparse point.");
                if ((attributes & FileAttributes.Directory) != 0)
                {
                    pending.Push(child);
                    continue;
                }

                using var stream = currentTree.OpenFile(
                    child,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read,
                    1,
                    FileOptions.SequentialScan);
                var identity = PathIdentity.CaptureFileIdentity(
                    stream.SafeFileHandle,
                    child);
                currentTree.Revalidate();
                _ = PathIdentity.RevalidateFile(
                    stream.SafeFileHandle,
                    child,
                    identity);
            }
            currentTree.Revalidate();
        }
    }

    private static void EnsureDirectChild(string candidatePath, string parentPath)
    {
        var candidate = PathIdentity.Canonicalize(candidatePath);
        var parent = PathIdentity.Canonicalize(parentPath);
        var actualParent = Path.GetDirectoryName(candidate)
                           ?? throw new InvalidDataException(
                               "O item físico não possui pasta-pai.");
        if (!LocalPathComparer.Equals(
                PathIdentity.Canonicalize(actualParent),
                parent))
            throw new InvalidDataException(
                "O item físico não é filho direto da pasta exata deste sistema.");
    }

    private static bool IsLocalInspectionException(Exception exception) =>
        exception is IOException
            or UnauthorizedAccessException
            or InvalidDataException
            or JsonException
            or ArgumentException
            or NotSupportedException
            or FormatException;

    private static CatalogArtifactDescriptor RequireArtifact(CatalogItem item) =>
        item.Artifact
        ?? throw new InvalidDataException("O item não possui artefato autorizado.");

    private static bool TryNormalizeInventoryPath(string? value, out string normalized)
    {
        normalized = string.Empty;
        if (string.IsNullOrWhiteSpace(value)
            || value.Length > MaximumRelativePathLength
            || value.Contains('\\')
            || value.Contains('\0')
            || value.StartsWith('/')
            || value.EndsWith('/')
            || Path.IsPathFullyQualified(value))
            return false;

        var segments = value.Split('/');
        if (segments.Length is < 1 or > MaximumPathDepth
            || segments.Any(segment => segment.Length is < 1 or > MaximumPathSegmentLength
                                       || segment is "." or ".."
                                       || segment.Contains(':')
                                       || segment.Any(char.IsControl)))
            return false;
        normalized = string.Join('/', segments);
        return normalized.Equals(value, StringComparison.Ordinal)
               && !normalized.Equals(
                   CatalogArchiveExtractor.CompletionMarkerFileName,
                   StringComparison.OrdinalIgnoreCase);
    }

    private static void EnsureWithin(string candidatePath, string rootPath)
    {
        var candidate = PathIdentity.Canonicalize(candidatePath);
        var root = PathIdentity.Canonicalize(rootPath);
        var prefix = Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar;
        if (!candidate.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("O inventário local saiu da pasta autorizada.");
    }

    private static bool IsCanonicalSha256(string? value) =>
        value is { Length: 64 }
        && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static bool IsMergedMarkerDirectory(string gameLibraryRoot, string candidatePath)
    {
        var receiptRoot = PathIdentity.Canonicalize(Path.Combine(
            gameLibraryRoot,
            CatalogGamePackageOrganizer.ReceiptFolderName));
        var candidate = PathIdentity.Canonicalize(candidatePath);
        var prefix = Path.TrimEndingDirectorySeparator(receiptRoot)
                     + Path.DirectorySeparatorChar;
        return candidate.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    private sealed record ExtractionCompletionMarker(
        int SchemaVersion,
        long ArchiveLength,
        string? ArchiveSha256,
        string? ManifestIdentity,
        string? ArtifactId,
        int ArtifactVersion,
        string? Category,
        string? Item,
        string? StableItemId,
        ExtractionInventoryEntry?[]? Inventory);

    private sealed record ExtractionInventoryEntry(
        string? RelativePath,
        long Length,
        string? Sha256);

    private sealed record CategoryChildrenInspection(
        CatalogLocalGameStatus Status,
        string Detail,
        IReadOnlyList<CatalogLocalOrphanInspection> Orphans);
}
