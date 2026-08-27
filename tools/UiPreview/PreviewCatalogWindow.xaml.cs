using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace Turborama.UiPreview;

public partial class PreviewCatalogWindow : Window
{
    private const int PageSize = 8;
    private readonly PreviewCatalogData _catalog;
    private readonly PreviewImageCache _imageCache = new();
    private readonly ObservableCollection<PreviewCard> _cards = [];
    private readonly DateTimeOffset _expiresAtUtc;
    private readonly TimeSpan _initialRemaining;
    private readonly Stopwatch _sessionClock = Stopwatch.StartNew();
    private readonly DispatcherTimer _expiryTimer;
    private PreviewCatalogCategory? _currentCategory;
    private IReadOnlyList<PreviewCatalogItem> _filteredItems = [];
    private int _currentPage;
    private bool _mediaAvailable;
    private bool _closing;

    internal PreviewCatalogWindow(
        PreviewCatalogData catalog,
        DateTimeOffset expiresAtUtc)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _expiresAtUtc = expiresAtUtc.ToUniversalTime();
        _initialRemaining = _expiresAtUtc - DateTimeOffset.UtcNow;
        if (_initialRemaining <= TimeSpan.Zero)
            throw new InvalidOperationException("Preview credential has expired.");

        InitializeComponent();
        CategoryList.ItemsSource = _catalog.Categories;
        CardsList.ItemsSource = _cards;
        _expiryTimer = new DispatcherTimer(
            TimeSpan.FromSeconds(1),
            DispatcherPriority.Normal,
            ExpiryTimer_Tick,
            Dispatcher);
        _expiryTimer.Start();

        Loaded += PreviewCatalogWindow_Loaded;
        Closing += PreviewCatalogWindow_Closing;
        Closed += PreviewCatalogWindow_Closed;
        StateChanged += PreviewCatalogWindow_StateChanged;
    }

    private void PreviewCatalogWindow_Loaded(object sender, RoutedEventArgs e)
    {
        if (CategoryList.Items.Count != 0)
            CategoryList.SelectedIndex = 0;
        UpdateExpiryText();
    }

    private void CategoryList_SelectionChanged(
        object sender,
        SelectionChangedEventArgs e)
    {
        if (CategoryList.SelectedItem is not PreviewCatalogCategory category)
            return;
        _currentCategory = category;
        _currentPage = 0;
        CategoryCodeText.Text = $"{category.Glyph}  {category.ShortCode}  •  {category.Items.Count} ITENS";
        CategoryTitleText.Text = category.DisplayName;
        CategoryDescriptionText.Text = category.Description;
        SearchInput.Clear();
        ApplyFilter();
        SelectItem(category.Items.Count == 0 ? null : category.Items[0]);
    }

    private void SearchInput_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_currentCategory is null)
            return;
        _currentPage = 0;
        ApplyFilter();
    }

    private void ApplyFilter()
    {
        if (_currentCategory is null)
            return;
        var query = SearchInput.Text.Trim();
        _filteredItems = query.Length == 0
            ? _currentCategory.Items
            : _currentCategory.Items
                .Where(item => item.Title.Contains(query, StringComparison.CurrentCultureIgnoreCase)
                               || item.Keywords.Contains(
                                   query,
                                   StringComparison.CurrentCultureIgnoreCase))
                .ToArray();
        UpdateCards();
    }

    private void UpdateCards()
    {
        var pageCount = Math.Max(1, (int)Math.Ceiling(
            _filteredItems.Count / (double)PageSize));
        _currentPage = Math.Clamp(_currentPage, 0, pageCount - 1);
        _cards.Clear();
        foreach (var item in _filteredItems
                     .Skip(_currentPage * PageSize)
                     .Take(PageSize))
        {
            BitmapSource image;
            try
            {
                image = _imageCache.Load(item.ImagePath);
            }
            catch (Exception exception) when (exception is IOException
                                               or UnauthorizedAccessException
                                               or NotSupportedException)
            {
                image = _imageCache.Load(_catalog.DefaultImagePath);
            }
            _cards.Add(new PreviewCard(
                item,
                image,
                item.Title,
                item.Subtitle,
                item.Badge));
        }

        PreviousButton.IsEnabled = _currentPage > 0;
        NextButton.IsEnabled = _currentPage + 1 < pageCount;
        PageText.Text = _filteredItems.Count == 0
            ? "NENHUM ITEM NESTA BUSCA"
            : string.Create(
                CultureInfo.InvariantCulture,
                $"PÁGINA {_currentPage + 1} DE {pageCount}  •  {_filteredItems.Count} ITENS");
    }

    private void PreviousButton_Click(object sender, RoutedEventArgs e)
    {
        if (_currentPage == 0)
            return;
        _currentPage--;
        UpdateCards();
    }

    private void NextButton_Click(object sender, RoutedEventArgs e)
    {
        if ((_currentPage + 1) * PageSize >= _filteredItems.Count)
            return;
        _currentPage++;
        UpdateCards();
    }

    private void Card_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: PreviewCatalogItem item })
            SelectItem(item);
    }

    private void SelectItem(PreviewCatalogItem? item)
    {
        if (_currentCategory is null)
            return;
        SelectedItemTitleText.Text = item?.Title ?? _currentCategory.DisplayName;
        SelectedItemDescriptionText.Text = string.IsNullOrWhiteSpace(item?.Description)
            ? _currentCategory.Description
            : item.Description;
        PlayLocalVideo(item?.VideoPath ?? _currentCategory.BackgroundVideoPath);
    }

    private void PlayLocalVideo(string path)
    {
        StopMedia();
        try
        {
            var source = new Uri(path, UriKind.Absolute);
            if (!source.IsFile || source.IsUnc)
                throw new InvalidDataException("Only local media is approved.");
            PreviewVideo.Source = source;
            PreviewVideo.Position = TimeSpan.Zero;
            PreviewVideo.Play();
            _mediaAvailable = true;
            PreviewVideo.Visibility = Visibility.Visible;
            MediaPlaceholder.Visibility = Visibility.Collapsed;
        }
        catch (Exception exception) when (exception is UriFormatException
                                           or InvalidDataException
                                           or NotSupportedException)
        {
            ShowMediaFallback("VÍDEO LOCAL INDISPONÍVEL");
        }
    }

    private void PreviewVideo_MediaEnded(object sender, RoutedEventArgs e)
    {
        if (!_mediaAvailable)
            return;
        PreviewVideo.Position = TimeSpan.Zero;
        PreviewVideo.Play();
    }

    private void PreviewVideo_MediaFailed(
        object sender,
        ExceptionRoutedEventArgs e)
        => ShowMediaFallback("CODEC OU VÍDEO INDISPONÍVEL");

    private void ShowMediaFallback(string message)
    {
        StopMedia();
        MediaStatusText.Text = message;
        MediaPlaceholder.Visibility = Visibility.Visible;
    }

    private void StopMedia()
    {
        _mediaAvailable = false;
        PreviewVideo.Stop();
        PreviewVideo.Source = null;
        PreviewVideo.Visibility = Visibility.Collapsed;
    }

    private void PreviewCatalogWindow_StateChanged(object? sender, EventArgs e)
    {
        if (!_mediaAvailable)
            return;
        if (WindowState == WindowState.Minimized)
            PreviewVideo.Pause();
        else
            PreviewVideo.Play();
    }

    private void ExpiryTimer_Tick(object? sender, EventArgs e)
    {
        if (_sessionClock.Elapsed >= _initialRemaining
            || DateTimeOffset.UtcNow >= _expiresAtUtc)
        {
            _expiryTimer.Stop();
            MessageBox.Show(
                this,
                "A senha temporária desta prévia expirou.",
                "Turborama UI Preview",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            Close();
            return;
        }
        UpdateExpiryText();
    }

    private void UpdateExpiryText()
    {
        var remaining = _initialRemaining - _sessionClock.Elapsed;
        if (remaining < TimeSpan.Zero)
            remaining = TimeSpan.Zero;
        ExpiryText.Text = string.Create(
            CultureInfo.InvariantCulture,
            $"SESSÃO LOCAL EXPIRA EM {Math.Floor(remaining.TotalHours):00}:{remaining.Minutes:00}:{remaining.Seconds:00}");
    }

    private void PreviewCatalogWindow_Closing(object? sender, CancelEventArgs e)
    {
        if (_closing)
            return;
        _closing = true;
        _expiryTimer.Stop();
        StopMedia();
        _imageCache.Clear();
    }

    private static void PreviewCatalogWindow_Closed(object? sender, EventArgs e)
        => Application.Current.Shutdown();

    internal sealed record PreviewCard(
        PreviewCatalogItem Item,
        BitmapSource Image,
        string Title,
        string Subtitle,
        string Badge);
}
