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
    public bool CanOpen => Item.CanOpen;

    public string StateLabel => State switch
    {
        CatalogDownloadState.Queued => "Na fila",
        CatalogDownloadState.Downloading => "Baixando",
        CatalogDownloadState.Completed => "Concluído",
        CatalogDownloadState.Failed => "Falhou",
        CatalogDownloadState.Canceled => "Cancelado",
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
            case nameof(CatalogItem.CanOpen):
                OnPropertyChanged(nameof(CanOpen));
                break;
        }
    }

    public void Dispose() => Item.PropertyChanged -= Item_PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
