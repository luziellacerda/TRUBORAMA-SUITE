using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace TurboBoxManager.Catalog;

public sealed class CatalogDownloadJob : INotifyPropertyChanged, IDisposable
{
    public CatalogDownloadJob(CatalogItem item)
    {
        Item = item ?? throw new ArgumentNullException(nameof(item));
        Item.PropertyChanged += Item_PropertyChanged;
    }

    public CatalogItem Item { get; }
    public string ItemId => Item.Id;
    public string Title => Item.Title;
    public string Category => Item.Category;
    public CatalogDownloadState State => Item.DownloadState;
    public double ProgressPercentage => Item.ProgressPercentage;
    public string StatusText => Item.DownloadStatus;
    public string LocalFilePath => Item.LocalFilePath;
    public bool CanCancel => Item.CanCancel;
    public bool CanPause => Item.CanPause;
    public bool CanResume => Item.CanResume;
    public bool CanDiscard => Item.CanDiscard;
    public bool CanRetryExtraction => Item.CanRetryExtraction;
    public bool CanOpen => Item.CanOpen;

    public string StateLabel => State switch
    {
        CatalogDownloadState.Queued => "Na fila",
        CatalogDownloadState.Downloading => "Baixando",
        CatalogDownloadState.WaitingForNetwork => "Aguardando internet",
        CatalogDownloadState.Paused => "Pausado",
        CatalogDownloadState.Verifying => "Verificando",
        CatalogDownloadState.Extracting => "Descompactando",
        CatalogDownloadState.AwaitingExtractionLocation => "Sem espaço",
        CatalogDownloadState.ExtractionFailed => "Extração falhou",
        CatalogDownloadState.Completed => "Concluído",
        CatalogDownloadState.Failed => "Falhou",
        CatalogDownloadState.Canceled => "Cancelado",
        CatalogDownloadState.Discarded => "Removido",
        _ => "Pronto"
    };

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Item_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(CatalogItem.DownloadState):
                OnPropertyChanged(nameof(State));
                OnPropertyChanged(nameof(StateLabel));
                OnPropertyChanged(nameof(CanCancel));
                OnPropertyChanged(nameof(CanPause));
                OnPropertyChanged(nameof(CanResume));
                OnPropertyChanged(nameof(CanDiscard));
                OnPropertyChanged(nameof(CanRetryExtraction));
                OnPropertyChanged(nameof(CanOpen));
                break;
            case nameof(CatalogItem.ProgressPercentage):
                OnPropertyChanged(nameof(ProgressPercentage));
                break;
            case nameof(CatalogItem.DownloadStatus):
                OnPropertyChanged(nameof(StatusText));
                break;
            case nameof(CatalogItem.LocalFilePath):
                OnPropertyChanged(nameof(LocalFilePath));
                OnPropertyChanged(nameof(CanOpen));
                break;
            case nameof(CatalogItem.CanCancel):
                OnPropertyChanged(nameof(CanCancel));
                break;
            case nameof(CatalogItem.CanPause):
                OnPropertyChanged(nameof(CanPause));
                break;
            case nameof(CatalogItem.CanResume):
                OnPropertyChanged(nameof(CanResume));
                break;
            case nameof(CatalogItem.CanDiscard):
                OnPropertyChanged(nameof(CanDiscard));
                break;
            case nameof(CatalogItem.CanRetryExtraction):
                OnPropertyChanged(nameof(CanRetryExtraction));
                break;
            case nameof(CatalogItem.CanOpen):
                OnPropertyChanged(nameof(CanOpen));
                break;
        }
    }

    public void Dispose() => Item.PropertyChanged -= Item_PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
