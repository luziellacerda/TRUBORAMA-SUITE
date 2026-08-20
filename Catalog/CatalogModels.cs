using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;
using System.Windows.Media;

namespace TurboBoxManager.Catalog;

public sealed class CatalogManifest
{
    public int SchemaVersion { get; init; } = 3;
    public string DefaultImage { get; init; } = string.Empty;
    public bool EnableTestDownloads { get; init; }
    public CatalogDownloadDefinition TestDownload { get; init; } = new();
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
    public string DownloadUrl { get; init; } = string.Empty;
    public string Sha256 { get; init; } = string.Empty;
    public string DownloadFileExtension { get; init; } = string.Empty;
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

public sealed class CatalogDownloadDefinition
{
    public string Url { get; init; } = string.Empty;
    public string Sha256 { get; init; } = string.Empty;
    public string Size { get; init; } = string.Empty;
    public string FileExtension { get; init; } = string.Empty;
}

public enum CatalogDownloadState
{
    Idle,
    Queued,
    Downloading,
    Completed,
    Failed,
    Canceled
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
    public string SystemCode { get; init; } = string.Empty;
    public string SystemGlyph { get; init; } = string.Empty;
    public int Order { get; init; }
    public SolidColorBrush AccentBrush { get; init; } = Brushes.LawnGreen;
    public string DownloadUrl { get; init; } = string.Empty;
    public string Sha256 { get; init; } = string.Empty;
    public string DownloadFileExtension { get; init; } = string.Empty;

    public string ImageSource
    {
        get => _imageSource;
        internal set
        {
            if (_imageSource == value) return;
            _imageSource = value;
            OnPropertyChanged();
        }
    }

    public CatalogDownloadState DownloadState
    {
        get => _downloadState;
        private set
        {
            if (_downloadState == value) return;
            _downloadState = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsDownloading));
            OnPropertyChanged(nameof(CanCancel));
            OnPropertyChanged(nameof(CanDownload));
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

    public bool IsDownloading => DownloadState is CatalogDownloadState.Queued or CatalogDownloadState.Downloading;
    public bool CanCancel => IsDownloading;
    public bool CanDownload => !IsDownloading;
    public bool CanOpen => DownloadState == CatalogDownloadState.Completed && LocalFilePath.Length > 0;

    public string DownloadActionLabel => DownloadState switch
    {
        CatalogDownloadState.Queued => "NA FILA • CANCELAR",
        CatalogDownloadState.Downloading => $"{ProgressPercentage:0}% • CANCELAR",
        CatalogDownloadState.Completed => "ABRIR ARQUIVO  ✓",
        CatalogDownloadState.Failed => "REPETIR DOWNLOAD",
        CatalogDownloadState.Canceled => "TENTAR NOVAMENTE",
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
        if (state is CatalogDownloadState.Idle or CatalogDownloadState.Queued)
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
        ProgressPercentage = 100;
        DownloadState = CatalogDownloadState.Completed;
        DownloadStatus = "Download concluído e verificado";
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
