using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace TurboBoxManager.Catalog;

public sealed class CatalogManifest
{
    public int SchemaVersion { get; init; }
    public string DefaultImage { get; init; } = string.Empty;
    public List<CatalogCategory> Categories { get; init; } = [];
    public List<CatalogItemDefinition> Items { get; init; } = [];

    // Compatibility with the first Turborama manifest. When Items is empty,
    // these templates are expanded to each category's SourceItemCount.
    public List<CatalogPackageTemplate> PackageTemplates { get; init; } = [];
}

public sealed class CatalogCategory : INotifyPropertyChanged
{
    private bool _isSelected;
    private SolidColorBrush? _accentBrush;

    public string Id { get; init; } = string.Empty;
    public string DisplayName { get; init; } = string.Empty;
    public string ShortCode { get; init; } = string.Empty;
    public string Glyph { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public string Accent { get; init; } = "#9DFF00";
    public int Order { get; init; }
    public int SourceItemCount { get; init; }

    [JsonIgnore]
    public string CountLabel => SourceItemCount == 1 ? "1 item" : $"{SourceItemCount} itens";

    [JsonIgnore]
    public SolidColorBrush AccentBrush => _accentBrush ??= CreateBrush(Accent);

    [JsonIgnore]
    public string MenuIconSource
    {
        get
        {
            var filePath = Path.Combine(
                AppContext.BaseDirectory,
                "Assets",
                "Catalog",
                "MenuIcons",
                $"{Id}.png");
            return File.Exists(filePath)
                ? new Uri(filePath).AbsoluteUri
                : $"pack://application:,,,/Assets/Catalog/MenuIcons/{Id}.png";
        }
    }

    [JsonIgnore]
    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected == value) return;
            _isSelected = value;
            OnPropertyChanged();
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    private static SolidColorBrush CreateBrush(string color)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));
        brush.Freeze();
        return brush;
    }
}

public sealed class CatalogItemDefinition
{
    public string Id { get; init; } = string.Empty;
    public string CategoryId { get; init; } = string.Empty;
    // DisplayName/ImagePath are the canonical fields emitted by the audited
    // Manager importer. Title/Image remain supported for schema-v2 manifests.
    public string DisplayName { get; init; } = string.Empty;
    public string Title { get; init; } = string.Empty;
    public string Subtitle { get; init; } = string.Empty;
    public string Category { get; init; } = string.Empty;
    public string ImagePath { get; init; } = string.Empty;
    public string Image { get; init; } = string.Empty;
    public string ImageAltText { get; init; } = string.Empty;
    public string Badge { get; init; } = string.Empty;
    public string Size { get; init; } = string.Empty;
    public string Version { get; init; } = string.Empty;
    public string Keywords { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public string DownloadUrl { get; init; } = string.Empty;
    public string Sha256 { get; init; } = string.Empty;
    public string DownloadFileExtension { get; init; } = string.Empty;
    public bool Extract { get; init; }
    public int Order { get; init; }
}

public sealed class CatalogPackageTemplate
{
    public string Id { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string Subtitle { get; init; } = string.Empty;
    public string Image { get; init; } = string.Empty;
    public string ImageAltText { get; init; } = string.Empty;
    public string Badge { get; init; } = string.Empty;
    public string Size { get; init; } = string.Empty;
    public string Version { get; init; } = string.Empty;
    public string Keywords { get; init; } = string.Empty;
    public int Order { get; init; }
}

public enum CatalogDownloadState
{
    Idle,
    Queued,
    Downloading,
    WaitingForNetwork,
    Paused,
    Verifying,
    Extracting,
    AwaitingExtractionLocation,
    ExtractionFailed,
    Completed,
    Failed,
    Canceled,
    Discarded
}

public enum CatalogExtractPolicy
{
    None,
    ExtractArchive
}

/// <summary>
/// Immutable, server-authorized identity of one downloadable artifact. A URL
/// is deliberately not part of this descriptor: transport grants are minted
/// per request and must never become catalog or resume-state authority.
/// </summary>
public sealed record CatalogArtifactDescriptor
{
    public const string TurboramaSuiteProductId = "TURBORAMA_SUITE";

    public string ProductId { get; } = TurboramaSuiteProductId;
    public required string ArtifactId { get; init; }
    public required int ArtifactVersion { get; init; }
    public required long ContentLength { get; init; }
    public required string Sha256 { get; init; }
    public required string SafeFileName { get; init; }
    public required string FileExtension { get; init; }
    public CatalogExtractPolicy ExtractPolicy { get; init; }
    public required string ManifestIdentity { get; init; }
}

public sealed record CatalogDownloadValidators(string ETag = "", string LastModified = "");

/// <summary>
/// Creates one complete, single-use GET request for the current session. The
/// returned request (including Authorization/proof) is consumed and disposed
/// without ever being serialized to disk.
/// </summary>
public interface ICatalogDownloadRequestProvider
{
    ValueTask<HttpRequestMessage> CreateRequestAsync(
        string itemId,
        CatalogArtifactDescriptor artifact,
        long offset,
        CatalogDownloadValidators validators,
        CancellationToken cancellationToken);
}

public sealed class CatalogItem : INotifyPropertyChanged
{
    private string _imageSource = string.Empty;
    private CatalogDownloadState _downloadState;
    private double _progressPercentage;
    private long _bytesReceived;
    private long? _totalBytes;
    private string _downloadStatus = "Pronto para baixar";
    private string _localFilePath = string.Empty;
    private string _archiveFilePath = string.Empty;

    public string Id { get; init; } = string.Empty;
    public string CategoryId { get; init; } = string.Empty;
    public string Title { get; init; } = string.Empty;
    public string Subtitle { get; init; } = string.Empty;
    public string Category { get; init; } = string.Empty;
    public string Image { get; init; } = string.Empty;
    public string FallbackImage { get; init; } = string.Empty;
    public string ImageAltText { get; init; } = string.Empty;
    public string Badge { get; init; } = string.Empty;
    public string Size { get; init; } = string.Empty;
    public string Version { get; init; } = string.Empty;
    public string Keywords { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public string SystemCode { get; init; } = string.Empty;
    public string SystemGlyph { get; init; } = string.Empty;
    public int Order { get; init; }
    public SolidColorBrush AccentBrush { get; init; } = Brushes.LawnGreen;
    // Untrusted visual-catalog hint retained only to reject a mismatch with the
    // server-authorized artifact. It must never authorize extraction by itself.
    public bool Extract { get; init; }
    public CatalogArtifactDescriptor? Artifact { get; init; }
    public bool IsMaintenance { get; init; }
    public string MaintenanceReasonCode { get; init; } = string.Empty;

    public bool HasExtractPolicyConflict => Artifact is not null
        && Extract != (Artifact.ExtractPolicy == CatalogExtractPolicy.ExtractArchive);
    public bool HasAuthorizedArtifact => Artifact is not null && !HasExtractPolicyConflict;

    public string ImageSource
    {
        get => _imageSource;
        internal set
        {
            if (_imageSource == value) return;
            _imageSource = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(Thumbnail384));
            OnPropertyChanged(nameof(Thumbnail160));
        }
    }

    [JsonIgnore]
    public BitmapSource? Thumbnail384 => CatalogThumbnailLoader.Load(ImageSource, 384);

    [JsonIgnore]
    public BitmapSource? Thumbnail160 => CatalogThumbnailLoader.Load(ImageSource, 160);

    public CatalogDownloadState DownloadState
    {
        get => _downloadState;
        private set
        {
            if (_downloadState == value) return;
            _downloadState = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsDownloading));
            OnPropertyChanged(nameof(IsBusy));
            OnPropertyChanged(nameof(CanCancel));
            OnPropertyChanged(nameof(CanPause));
            OnPropertyChanged(nameof(CanResume));
            OnPropertyChanged(nameof(CanDiscard));
            OnPropertyChanged(nameof(CanRetryExtraction));
            OnPropertyChanged(nameof(CanDownload));
            OnPropertyChanged(nameof(CanOpen));
            OnPropertyChanged(nameof(DownloadActionLabel));
        }
    }

    public double ProgressPercentage
    {
        get => _progressPercentage;
        private set
        {
            if (Math.Abs(_progressPercentage - value) < 0.01) return;
            _progressPercentage = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(DownloadActionLabel));
        }
    }

    public long BytesReceived
    {
        get => _bytesReceived;
        private set
        {
            if (_bytesReceived == value) return;
            _bytesReceived = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(CanResume));
            OnPropertyChanged(nameof(CanDiscard));
            OnPropertyChanged(nameof(DownloadActionLabel));
        }
    }

    public long? TotalBytes
    {
        get => _totalBytes;
        private set
        {
            if (_totalBytes == value) return;
            _totalBytes = value;
            OnPropertyChanged();
        }
    }

    public string DownloadStatus
    {
        get => _downloadStatus;
        private set
        {
            if (_downloadStatus == value) return;
            _downloadStatus = value;
            OnPropertyChanged();
        }
    }

    public string LocalFilePath
    {
        get => _localFilePath;
        private set
        {
            if (_localFilePath == value) return;
            _localFilePath = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(CanOpen));
        }
    }

    public string ArchiveFilePath
    {
        get => _archiveFilePath;
        private set
        {
            if (_archiveFilePath == value) return;
            _archiveFilePath = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(CanDiscard));
            OnPropertyChanged(nameof(CanRetryExtraction));
        }
    }

    public bool IsDownloading => DownloadState is CatalogDownloadState.Queued
        or CatalogDownloadState.Downloading
        or CatalogDownloadState.WaitingForNetwork;
    public bool IsBusy => IsDownloading || DownloadState is CatalogDownloadState.Verifying
        or CatalogDownloadState.Extracting;
    public bool CanPause => IsDownloading;
    public bool CanCancel => CanPause;
    public bool CanResume => HasAuthorizedArtifact
        && DownloadState is (CatalogDownloadState.Paused
            or CatalogDownloadState.Canceled
            or CatalogDownloadState.Failed);
    public bool CanDiscard => !IsBusy
        && DownloadState is not (CatalogDownloadState.Completed or CatalogDownloadState.Discarded)
        && (BytesReceived > 0
            || ArchiveFilePath.Length > 0
            || DownloadState == CatalogDownloadState.Paused);
    public bool CanRetryExtraction => !IsBusy
        && HasAuthorizedArtifact
        && Artifact!.ExtractPolicy == CatalogExtractPolicy.ExtractArchive
        && ArchiveFilePath.Length > 0
        && DownloadState is CatalogDownloadState.AwaitingExtractionLocation
            or CatalogDownloadState.ExtractionFailed;
    public bool CanDownload => HasAuthorizedArtifact && !IsBusy;
    public bool CanOpen => HasAuthorizedArtifact
        && DownloadState == CatalogDownloadState.Completed
        && LocalFilePath.Length > 0;

    public string DownloadActionLabel => IsMaintenance
        ? "EM MANUTENÇÃO"
        : !HasAuthorizedArtifact
        ? "INDISPONÍVEL"
        : DownloadState switch
        {
            CatalogDownloadState.Queued => "NA FILA • PAUSAR",
            CatalogDownloadState.Downloading => $"{ProgressPercentage:0}% • PAUSAR",
            CatalogDownloadState.WaitingForNetwork => "SEM INTERNET • PAUSAR",
            CatalogDownloadState.Paused => "CONTINUAR DOWNLOAD",
            CatalogDownloadState.Verifying => "VERIFICANDO...",
            CatalogDownloadState.Extracting => "DESCOMPACTANDO...",
            CatalogDownloadState.AwaitingExtractionLocation => "ESCOLHER OUTRO HD",
            CatalogDownloadState.ExtractionFailed => "TENTAR EXTRAÇÃO",
            CatalogDownloadState.Completed => "ABRIR PASTA  ✓",
            CatalogDownloadState.Failed when BytesReceived > 0 => "CONTINUAR DOWNLOAD",
            CatalogDownloadState.Failed => "REPETIR DOWNLOAD",
            CatalogDownloadState.Canceled => "CONTINUAR DOWNLOAD",
            _ => "BAIXAR PACOTE  ↓"
        };

    public event PropertyChangedEventHandler? PropertyChanged;

    public void UseFallbackImage()
    {
        if (FallbackImage.Length > 0) ImageSource = FallbackImage;
    }

    internal void SetDownloadState(CatalogDownloadState state, string status)
    {
        DownloadState = state;
        DownloadStatus = status;
        if (state == CatalogDownloadState.Idle)
            UpdateDownloadProgress(0, null);
    }

    internal void UpdateDownloadProgress(long bytesReceived, long? totalBytes)
    {
        BytesReceived = Math.Max(0, bytesReceived);
        TotalBytes = totalBytes is > 0 ? totalBytes : null;
        ProgressPercentage = TotalBytes is > 0
            ? Math.Clamp(BytesReceived * 100d / TotalBytes.Value, 0d, 100d)
            : 0d;
        if (DownloadState == CatalogDownloadState.Downloading)
        {
            DownloadStatus = TotalBytes is > 0
                ? $"Baixando {ProgressPercentage:0}%"
                : $"Baixando {FormatBytes(BytesReceived)}";
        }
    }

    internal void CompleteDownload(string localFilePath)
    {
        LocalFilePath = localFilePath;
        ArchiveFilePath = HasAuthorizedArtifact
                          && Artifact!.ExtractPolicy == CatalogExtractPolicy.ExtractArchive
            ? localFilePath
            : string.Empty;
        ProgressPercentage = 100;
        DownloadState = CatalogDownloadState.Completed;
        DownloadStatus = "Download concluído e verificado";
    }

    internal void RestoreDownload(long bytesReceived, long? totalBytes, bool isPaused)
    {
        UpdateDownloadProgress(bytesReceived, totalBytes);
        DownloadState = isPaused ? CatalogDownloadState.Paused : CatalogDownloadState.Queued;
        DownloadStatus = isPaused
            ? $"Pausado em {FormatBytes(bytesReceived)} — pronto para continuar"
            : $"Retomando de {FormatBytes(bytesReceived)}";
    }

    internal void PauseDownload(string status = "Download pausado — o progresso foi preservado") =>
        SetDownloadState(CatalogDownloadState.Paused, status);

    internal void WaitForNetwork(TimeSpan retryDelay) =>
        SetDownloadState(
            CatalogDownloadState.WaitingForNetwork,
            $"Sem internet — nova tentativa em {Math.Max(1, (int)Math.Ceiling(retryDelay.TotalSeconds))} s");

    internal void BeginVerification() =>
        SetDownloadState(CatalogDownloadState.Verifying, "Verificando o arquivo baixado");

    internal void MarkArchiveReady(string archivePath)
    {
        ArchiveFilePath = archivePath;
        LocalFilePath = archivePath;
        ProgressPercentage = 100;
        SetDownloadState(CatalogDownloadState.Verifying, "Download concluído — preparando extração");
    }

    internal void BeginExtraction() =>
        SetDownloadState(CatalogDownloadState.Extracting, "Descompactando automaticamente");

    internal void AwaitExtractionLocation(string status) =>
        SetDownloadState(CatalogDownloadState.AwaitingExtractionLocation, status);

    internal void FailExtraction(string status) =>
        SetDownloadState(CatalogDownloadState.ExtractionFailed, status);

    internal void CompleteExtraction(string extractionPath)
    {
        LocalFilePath = extractionPath;
        ArchiveFilePath = string.Empty;
        ProgressPercentage = 100;
        SetDownloadState(CatalogDownloadState.Completed, "Download e extração concluídos");
    }

    internal void DiscardDownload()
    {
        LocalFilePath = string.Empty;
        ArchiveFilePath = string.Empty;
        UpdateDownloadProgress(0, null);
        SetDownloadState(CatalogDownloadState.Discarded, "Download removido pelo cliente");
    }

    private static string FormatBytes(long bytes) => bytes switch
    {
        >= 1024L * 1024L * 1024L => $"{bytes / (1024d * 1024d * 1024d):0.0} GB",
        >= 1024L * 1024L => $"{bytes / (1024d * 1024d):0.0} MB",
        >= 1024L => $"{bytes / 1024d:0.0} KB",
        _ => $"{bytes} B"
    };

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

public sealed class LibrarySystemSummary
{
    public required CatalogCategory Category { get; init; }
    public string CategoryId => Category.Id;
    public string DisplayName => Category.DisplayName;
    public string ShortCode => Category.ShortCode;
    public string MenuIconSource => Category.MenuIconSource;
    public SolidColorBrush AccentBrush => Category.AccentBrush;
    public Color AccentColor => Category.AccentBrush.Color;
    public required string CoverImageSource { get; init; }
    public BitmapSource? CoverThumbnail320 =>
        CatalogThumbnailLoader.Load(CoverImageSource, 320);
    public required int ItemCount { get; init; }
    public string CountLabel => ItemCount == 1 ? "1 jogo" : $"{ItemCount} jogos";
}

public sealed record CatalogQueryResult(
    IReadOnlyList<CatalogItem> Items,
    int TotalItems,
    int CurrentPage,
    int TotalPages);

public sealed class PageNumber
{
    public int Page { get; init; }
    public bool IsCurrent { get; init; }
    public bool IsEllipsis { get; init; }
    public string Number => IsEllipsis ? "…" : Page.ToString(CultureInfo.InvariantCulture);
    public bool IsEnabled => !IsEllipsis && !IsCurrent;
}
