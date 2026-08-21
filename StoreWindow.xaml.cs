using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Win32;
using TurboBoxManager.Catalog;

namespace TurboBoxManager;

public partial class StoreWindow : Window, INotifyPropertyChanged
{
    private const int CatalogPageSize = 4;

    private readonly CatalogDownloadService _downloadService = new(new CatalogDownloadOptions
    {
        MaximumFileSizeBytes = 512L * 1024L * 1024L * 1024L,
        AllowedHosts = new HashSet<string>(
            [
                "github.com",
                "objects.githubusercontent.com",
                "release-assets.githubusercontent.com",
                "raw.githubusercontent.com",
                "cucunot.sambox.club",
                "detroit.sambox.club",
                "miami.sambox.buzz"
            ],
            StringComparer.OrdinalIgnoreCase)
    });
    private readonly Dictionary<string, CatalogDownloadJob> _downloadJobsByItem =
        new(StringComparer.OrdinalIgnoreCase);
    private CatalogRepository? _catalogRepository;
    private CatalogCategory? _selectedCategory;
    private int _currentCatalogPage = 1;
    private string _catalogSearchText = string.Empty;
    private string _installFolderPath = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
    private string _temporaryFolderPath = string.Empty;
    private bool _hasCatalogItems;
    private bool _canGoToPreviousPage;
    private bool _canGoToNextPage;

    public ObservableCollection<CatalogCategory> CatalogCategories { get; } = [];
    public ObservableCollection<CatalogCategory> FeaturedCategories { get; } = [];
    public ObservableCollection<CatalogItem> CatalogItems { get; } = [];
    public ObservableCollection<PageNumber> PageNumbers { get; } = [];
    public ObservableCollection<CatalogDownloadJob> DownloadJobs { get; } = [];

    public bool HasDownloads => DownloadJobs.Count > 0;
    public Visibility DownloadsEmptyVisibility => HasDownloads ? Visibility.Collapsed : Visibility.Visible;
    public Visibility DownloadsListVisibility => HasDownloads ? Visibility.Visible : Visibility.Collapsed;

    public CatalogCategory? SelectedCategory
    {
        get => _selectedCategory;
        private set
        {
            if (ReferenceEquals(_selectedCategory, value)) return;
            _selectedCategory = value;
            OnPropertyChanged();
        }
    }

    public bool HasCatalogItems
    {
        get => _hasCatalogItems;
        private set
        {
            if (_hasCatalogItems == value) return;
            _hasCatalogItems = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(CatalogEmptyVisibility));
            OnPropertyChanged(nameof(CatalogResultsVisibility));
        }
    }

    public Visibility CatalogEmptyVisibility => HasCatalogItems ? Visibility.Collapsed : Visibility.Visible;
    public Visibility CatalogResultsVisibility => HasCatalogItems ? Visibility.Visible : Visibility.Collapsed;

    public bool CanGoToPreviousPage
    {
        get => _canGoToPreviousPage;
        private set
        {
            if (_canGoToPreviousPage == value) return;
            _canGoToPreviousPage = value;
            OnPropertyChanged();
        }
    }

    public bool CanGoToNextPage
    {
        get => _canGoToNextPage;
        private set
        {
            if (_canGoToNextPage == value) return;
            _canGoToNextPage = value;
            OnPropertyChanged();
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public StoreWindow() : this(false)
    {
    }

    public StoreWindow(bool skipLogin)
    {
        InitializeComponent();
        DataContext = this;
        _installFolderPath = LocalDataPaths.ReadInstallFolder() ?? _installFolderPath;
        InitializeCatalog();
        UpdateFolderLabels();

        if (!skipLogin) return;

        SetVisibility("LoginView", false);
        SetVisibility("AppView", true);
        ShowPage("Home");
    }

    private void InitializeCatalog()
    {
        try
        {
            var catalogDirectory = Path.Combine(AppContext.BaseDirectory, "Assets", "Catalog");
            var privateManifestPath = Path.Combine(catalogDirectory, "catalog.full.json");
            var manifestPath = File.Exists(privateManifestPath)
                ? privateManifestPath
                : Path.Combine(catalogDirectory, "catalog.json");
            _catalogRepository = CatalogRepository.Load(manifestPath);

            CatalogCategories.Clear();
            foreach (var category in _catalogRepository.Categories)
                CatalogCategories.Add(category);

            FeaturedCategories.Clear();
            foreach (var category in CatalogCategories.Take(6))
                FeaturedCategories.Add(category);

            if (CatalogCategories.Count > 0)
            {
                SelectCategory(CatalogCategories[0]);
                RefreshCatalog();
            }
        }
        catch (Exception exception) when (exception is IOException
                                           or UnauthorizedAccessException
                                           or InvalidDataException
                                           or System.Text.Json.JsonException
                                           or ArgumentException)
        {
            _catalogRepository = null;
            CatalogCategories.Clear();
            FeaturedCategories.Clear();
            CatalogItems.Clear();
            PageNumbers.Clear();
            HasCatalogItems = false;
            SetCatalogStatus($"Catálogo indisponível: {exception.Message}");
        }
    }

    private void Enter_Click(object sender, RoutedEventArgs e)
    {
        var licenseInput = FindNamed<TextBox>("LicenseInput");
        var loginStatus = FindNamed<TextBlock>("LoginStatus");
        if (string.IsNullOrWhiteSpace(licenseInput?.Text))
        {
            if (loginStatus is not null)
            {
                loginStatus.Text = "Digite uma licença para continuar.";
                loginStatus.Visibility = Visibility.Visible;
            }
            return;
        }

        if (loginStatus is not null) loginStatus.Visibility = Visibility.Collapsed;
        SetVisibility("LoginView", false);
        SetVisibility("AppView", true);
        ShowPage("Home");
    }

    private void Nav_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string page }) ShowPage(page);
    }

    private void Category_Click(object sender, RoutedEventArgs e)
    {
        var source = sender as FrameworkElement;
        var category = source?.Tag as CatalogCategory ?? source?.DataContext as CatalogCategory;
        if (category is null && source?.Tag is string categoryId)
        {
            category = CatalogCategories.FirstOrDefault(candidate =>
                candidate.Id.Equals(categoryId, StringComparison.OrdinalIgnoreCase));
        }

        if (category is null)
        {
            SetCatalogStatus("Não foi possível identificar o sistema selecionado.");
            return;
        }

        OpenCatalog(category);
    }

    private void OpenFirstCatalog_Click(object sender, RoutedEventArgs e)
    {
        var category = FeaturedCategories.FirstOrDefault() ?? CatalogCategories.FirstOrDefault();
        if (category is null)
        {
            SetCatalogStatus("Nenhum sistema está disponível no catálogo.");
            return;
        }

        OpenCatalog(category);
    }

    private void OpenCatalog(CatalogCategory category)
    {
        SelectCategory(category);
        _currentCatalogPage = 1;
        SetSearchText(string.Empty);
        ShowPage("Catalog");
    }

    private void SelectCategory(CatalogCategory category)
    {
        foreach (var candidate in CatalogCategories)
            candidate.IsSelected = ReferenceEquals(candidate, category);

        SelectedCategory = category;
    }

    private void Search_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_catalogRepository is null) return;

        _catalogSearchText = sender is TextBox textBox ? textBox.Text : string.Empty;
        _currentCatalogPage = 1;
        RefreshCatalog();
    }

    private void PreviousPage_Click(object sender, RoutedEventArgs e)
    {
        if (!CanGoToPreviousPage) return;
        _currentCatalogPage--;
        RefreshCatalog();
    }

    private void NextPage_Click(object sender, RoutedEventArgs e)
    {
        if (!CanGoToNextPage) return;
        _currentCatalogPage++;
        RefreshCatalog();
    }

    private void SelectPage_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement source) return;

        var page = source.Tag switch
        {
            int number => number,
            PageNumber pageNumber when !pageNumber.IsEllipsis => pageNumber.Page,
            string text when int.TryParse(text, out var number) => number,
            _ when source.DataContext is PageNumber { IsEllipsis: false } pageNumber => pageNumber.Page,
            _ => 0
        };

        if (page < 1 || page == _currentCatalogPage) return;
        _currentCatalogPage = page;
        RefreshCatalog();
    }

    private void RefreshCatalog()
    {
        if (_catalogRepository is null || SelectedCategory is null)
        {
            CatalogItems.Clear();
            PageNumbers.Clear();
            HasCatalogItems = false;
            UpdateCatalogNavigation(1, 1);
            return;
        }

        var result = _catalogRepository.Query(
            SelectedCategory.Id,
            _catalogSearchText,
            _currentCatalogPage,
            CatalogPageSize);

        _currentCatalogPage = result.CurrentPage;

        CatalogItems.Clear();
        foreach (var item in result.Items)
            CatalogItems.Add(item);

        BuildCompactPageNumbers(result.CurrentPage, result.TotalPages);

        HasCatalogItems = result.TotalItems > 0;
        UpdateCatalogNavigation(result.CurrentPage, result.TotalPages);
        UpdateCatalogHeader(result);

        if (result.TotalItems == 0)
            SetCatalogStatus("Nenhum pacote corresponde à busca neste sistema.");
        else
            SetCatalogStatus(string.Empty);
    }

    private void BuildCompactPageNumbers(int currentPage, int totalPages)
    {
        PageNumbers.Clear();
        var visiblePages = new SortedSet<int> { 1, totalPages };
        for (var page = currentPage - 2; page <= currentPage + 2; page++)
        {
            if (page >= 1 && page <= totalPages) visiblePages.Add(page);
        }

        var previousPage = 0;
        foreach (var page in visiblePages)
        {
            if (previousPage > 0 && page - previousPage > 1)
                PageNumbers.Add(new PageNumber { IsEllipsis = true });

            PageNumbers.Add(new PageNumber
            {
                Page = page,
                IsCurrent = page == currentPage
            });
            previousPage = page;
        }
    }

    private void UpdateCatalogNavigation(int currentPage, int totalPages)
    {
        CanGoToPreviousPage = currentPage > 1;
        CanGoToNextPage = currentPage < totalPages;

        if (FindNamed<Button>("PreviousPageButton") is { } previousButton)
            previousButton.IsEnabled = CanGoToPreviousPage;
        if (FindNamed<Button>("NextPageButton") is { } nextButton)
            nextButton.IsEnabled = CanGoToNextPage;
    }

    private void UpdateCatalogHeader(CatalogQueryResult result)
    {
        if (SelectedCategory is null) return;

        SetText("PageTitle", SelectedCategory.DisplayName);
        SetText("PageSubtitle", SelectedCategory.Description);
        SetText("CatalogPageTitle", SelectedCategory.DisplayName);
        SetText("CatalogPageDescription", SelectedCategory.Description);
        SetText("CatalogSystemCode", SelectedCategory.ShortCode);

        var itemLabel = result.TotalItems == 1 ? "1 item" : $"{result.TotalItems} itens";
        var pageLabel = result.TotalPages == 1
            ? "1 página"
            : $"página {result.CurrentPage} de {result.TotalPages}";
        SetText("CatalogResultCount", $"{itemLabel} • {pageLabel}");
    }

    private void ChooseInstallFolder_Click(object sender, RoutedEventArgs e)
    {
        var selectedFolder = ChooseFolder("Escolha a pasta de instalação", _installFolderPath);
        if (selectedFolder is null) return;

        _installFolderPath = selectedFolder;
        UpdateFolderLabels();
        SetCatalogStatus("Pasta de instalação atualizada.");
    }

    private void ChooseTempFolder_Click(object sender, RoutedEventArgs e)
    {
        var initialFolder = string.IsNullOrWhiteSpace(_temporaryFolderPath)
            ? Path.GetTempPath()
            : _temporaryFolderPath;
        var selectedFolder = ChooseFolder("Escolha a pasta temporária", initialFolder);
        if (selectedFolder is null) return;

        _temporaryFolderPath = selectedFolder;
        UpdateFolderLabels();
        SetCatalogStatus("Pasta temporária atualizada.");
    }

    private void ResetTempFolder_Click(object sender, RoutedEventArgs e)
    {
        _temporaryFolderPath = string.Empty;
        UpdateFolderLabels();
        SetCatalogStatus("A pasta temporária voltou ao padrão do sistema.");
    }

    private void OpenInstallFolder_Click(object sender, RoutedEventArgs e)
    {
        if (!Directory.Exists(_installFolderPath))
        {
            SetCatalogStatus("A pasta de instalação não existe. Escolha uma pasta válida.");
            return;
        }

        try
        {
            var startInfo = new ProcessStartInfo("explorer.exe") { UseShellExecute = true };
            startInfo.ArgumentList.Add(_installFolderPath);
            Process.Start(startInfo);
            SetCatalogStatus("Pasta de instalação aberta.");
        }
        catch (Exception exception) when (exception is InvalidOperationException or Win32Exception)
        {
            SetCatalogStatus($"Não foi possível abrir a pasta: {exception.Message}");
        }
    }

    private void Support_Click(object sender, RoutedEventArgs e)
    {
        SetCatalogStatus("O canal de suporte ainda não foi configurado nesta versão.");
    }

    private async void DownloadItem_Click(object sender, RoutedEventArgs e)
    {
        var source = sender as FrameworkElement;
        var item = source?.Tag as CatalogItem ?? source?.DataContext as CatalogItem;
        if (item is null && source?.Tag is string itemId)
            item = _catalogRepository?.FindById(itemId);

        if (item is null)
        {
            SetCatalogStatus("Não foi possível identificar o pacote selecionado. Nenhum download foi iniciado.");
            return;
        }

        if (item.CanOpen)
        {
            OpenDownloadedFile(item.LocalFilePath);
            return;
        }

        if (item.IsDownloading)
        {
            if (_downloadService.Cancel(item.Id))
                SetCatalogStatus($"Cancelando {item.Title}...");
            return;
        }

        if (string.IsNullOrWhiteSpace(item.DownloadUrl))
        {
            SetCatalogStatus($"{item.Title}: nenhum endereço de download foi configurado. Nada foi iniciado.");
            return;
        }

        EnsureDownloadJob(item);
        SetCatalogStatus($"{item.Title}: iniciando download de teste verificado...");
        var result = await _downloadService.DownloadAsync(item, _installFolderPath);
        SetCatalogStatus(result.Message);
    }

    private void CancelDownloadJob_Click(object sender, RoutedEventArgs e)
    {
        var job = ResolveDownloadJob(sender as FrameworkElement);
        if (job is null)
        {
            SetCatalogStatus("Não foi possível identificar o download para cancelar.");
            return;
        }

        if (_downloadService.Cancel(job.ItemId))
            SetCatalogStatus($"Cancelando {job.Title}...");
    }

    private void OpenDownloadJob_Click(object sender, RoutedEventArgs e)
    {
        var job = ResolveDownloadJob(sender as FrameworkElement);
        if (job is null || !job.CanOpen)
        {
            SetCatalogStatus("O arquivo deste download ainda não está disponível.");
            return;
        }

        OpenDownloadedFile(job.LocalFilePath);
    }

    private void ClearDownloadHistory_Click(object sender, RoutedEventArgs e)
    {
        for (var index = DownloadJobs.Count - 1; index >= 0; index--)
        {
            var job = DownloadJobs[index];
            if (job.CanCancel) continue;
            DownloadJobs.RemoveAt(index);
            _downloadJobsByItem.Remove(job.ItemId);
            job.Dispose();
        }

        NotifyDownloadCollectionChanged();
    }

    private CatalogDownloadJob EnsureDownloadJob(CatalogItem item)
    {
        if (_downloadJobsByItem.TryGetValue(item.Id, out var existing)) return existing;

        var job = new CatalogDownloadJob(item);
        _downloadJobsByItem.Add(item.Id, job);
        DownloadJobs.Insert(0, job);
        NotifyDownloadCollectionChanged();
        return job;
    }

    private CatalogDownloadJob? ResolveDownloadJob(FrameworkElement? source)
    {
        if (source?.Tag is CatalogDownloadJob taggedJob) return taggedJob;
        if (source?.DataContext is CatalogDownloadJob dataJob) return dataJob;
        return source?.Tag is string itemId
            ? _downloadJobsByItem.GetValueOrDefault(itemId)
            : null;
    }

    private void NotifyDownloadCollectionChanged()
    {
        OnPropertyChanged(nameof(HasDownloads));
        OnPropertyChanged(nameof(DownloadsEmptyVisibility));
        OnPropertyChanged(nameof(DownloadsListVisibility));
    }

    private void OpenDownloadedFile(string localFilePath)
    {
        if (string.IsNullOrWhiteSpace(localFilePath) || !File.Exists(localFilePath))
        {
            SetCatalogStatus("O arquivo baixado não foi encontrado.");
            return;
        }

        var canonicalRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(_installFolderPath))
                            + Path.DirectorySeparatorChar;
        var canonicalFile = Path.GetFullPath(localFilePath);
        if (!canonicalFile.StartsWith(canonicalRoot, StringComparison.OrdinalIgnoreCase))
        {
            SetCatalogStatus("O arquivo está fora da pasta autorizada e não será aberto.");
            return;
        }

        var containingDirectory = Path.GetDirectoryName(canonicalFile);
        if (containingDirectory is null) return;

        try
        {
            var startInfo = new ProcessStartInfo("explorer.exe") { UseShellExecute = true };
            startInfo.ArgumentList.Add(containingDirectory);
            Process.Start(startInfo);
            SetCatalogStatus("Pasta do arquivo baixado aberta.");
        }
        catch (Exception exception) when (exception is InvalidOperationException or Win32Exception)
        {
            SetCatalogStatus($"Não foi possível abrir a pasta do download: {exception.Message}");
        }
    }

    private void ShowPage(string page)
    {
        var normalizedPage = page.Trim();
        var isHome = normalizedPage.Equals("Store", StringComparison.OrdinalIgnoreCase)
                     || normalizedPage.Equals("Home", StringComparison.OrdinalIgnoreCase);
        var isLibrary = normalizedPage.Equals("Library", StringComparison.OrdinalIgnoreCase);
        var isDownloads = normalizedPage.Equals("Downloads", StringComparison.OrdinalIgnoreCase);
        var isCatalog = normalizedPage.Equals("Catalog", StringComparison.OrdinalIgnoreCase)
                        || normalizedPage.Equals("Retro", StringComparison.OrdinalIgnoreCase);

        SetVisibility("HomePage", isHome);
        SetVisibility("StorePage", isHome);
        SetVisibility("LibraryPage", isLibrary);
        SetVisibility("DownloadsPage", isDownloads);
        SetVisibility("CatalogPage", isCatalog);

        if (isCatalog && SelectedCategory is not null)
        {
            RefreshCatalog();
            return;
        }

        var (title, subtitle) = isLibrary
            ? ("Minha biblioteca", "Acompanhe sua coleção Turborama")
            : isDownloads
                ? ("Downloads", "Gerencie instalações, atualizações e fila")
                : ("Descobrir", "Encontre sua próxima experiência");
        SetText("PageTitle", title);
        SetText("PageSubtitle", subtitle);
    }

    private void SetSearchText(string value)
    {
        _catalogSearchText = value;
        if (FindNamed<TextBox>("CatalogSearchBox") is { } searchBox && searchBox.Text != value)
            searchBox.Text = value;
    }

    private static string? ChooseFolder(string title, string initialDirectory)
    {
        var dialog = new OpenFolderDialog
        {
            Title = title,
            InitialDirectory = Directory.Exists(initialDirectory)
                ? initialDirectory
                : Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            Multiselect = false
        };
        return dialog.ShowDialog() == true ? dialog.FolderName : null;
    }

    private void UpdateFolderLabels()
    {
        SetText("InstallFolderPath", _installFolderPath);
        SetText("InstallFolderText", _installFolderPath);
        var temporaryFolderLabel = string.IsNullOrWhiteSpace(_temporaryFolderPath)
            ? "(padrão do sistema)"
            : _temporaryFolderPath;
        SetText("TempFolderPath", temporaryFolderLabel);
        SetText("TempFolderText", temporaryFolderLabel);
    }

    private void SetCatalogStatus(string message)
    {
        if (FindNamed<TextBlock>("CatalogActionStatus") is not { } status) return;
        status.Text = message;
        status.Visibility = string.IsNullOrWhiteSpace(message) ? Visibility.Collapsed : Visibility.Visible;
    }

    private void SetVisibility(string elementName, bool isVisible)
    {
        if (FindNamed<UIElement>(elementName) is { } element)
            element.Visibility = isVisible ? Visibility.Visible : Visibility.Collapsed;
    }

    private void SetText(string elementName, string value)
    {
        if (FindNamed<TextBlock>(elementName) is { } textBlock)
            textBlock.Text = value;
    }

    private T? FindNamed<T>(string name) where T : DependencyObject => FindName(name) as T;

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2) ToggleMaximize();
        else DragMove();
    }

    private void ToggleMaximize() =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void Maximize_Click(object sender, RoutedEventArgs e) => ToggleMaximize();
    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    protected override void OnClosed(EventArgs e)
    {
        foreach (var job in DownloadJobs) job.Dispose();
        _downloadService.Dispose();
        base.OnClosed(e);
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
