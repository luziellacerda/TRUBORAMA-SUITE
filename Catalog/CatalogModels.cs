using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;
using System.Windows.Media;

namespace TurboBoxManager.Catalog;

public sealed class CatalogManifest
{
    public List<CatalogCategory> Categories { get; init; } = [];
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

public sealed class CatalogPackageTemplate
{
    public string Id { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string Subtitle { get; init; } = string.Empty;
    public string Badge { get; init; } = string.Empty;
    public string Size { get; init; } = string.Empty;
    public string Version { get; init; } = string.Empty;
    public string Keywords { get; init; } = string.Empty;
    public int Order { get; init; }
}

public sealed class CatalogItem
{
    public string Id { get; init; } = string.Empty;
    public string CategoryId { get; init; } = string.Empty;
    public string Title { get; init; } = string.Empty;
    public string Subtitle { get; init; } = string.Empty;
    public string Badge { get; init; } = string.Empty;
    public string Size { get; init; } = string.Empty;
    public string Version { get; init; } = string.Empty;
    public string Keywords { get; init; } = string.Empty;
    public string SystemCode { get; init; } = string.Empty;
    public string SystemGlyph { get; init; } = string.Empty;
    public int Order { get; init; }
    public SolidColorBrush AccentBrush { get; init; } = Brushes.LawnGreen;
    public string DownloadUrl { get; init; } = string.Empty;
}

public sealed record CatalogQueryResult(
    IReadOnlyList<CatalogItem> Items,
    int TotalItems,
    int CurrentPage,
    int TotalPages);

public sealed class PageNumber
{
    public int Number { get; init; }
    public bool IsCurrent { get; init; }
}
