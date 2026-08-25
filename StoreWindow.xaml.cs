using System.Collections.ObjectModel;
using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;
using TurboBoxManager.Catalog;

namespace TurboBoxManager;

public partial class StoreWindow : Window, INotifyPropertyChanged
{
    private const int CatalogPageSize = 4;
    private const string RetroGamesCategoryId = "retro-games";
    private const int MaximumRetroSystemVideoManifestBytes = 256 * 1024;
    private const int MaximumRetroPlatformDescriptionsBytes = 512 * 1024;
    private static readonly Lazy<IReadOnlyDictionary<string, string>> RetroSystemVideoMap =
        new(LoadRetroSystemVideoMap);
    private static readonly Lazy<IReadOnlyDictionary<string, RetroSystemVideoIntegrity>> RetroSystemVideoIntegrityMap =
        new(LoadRetroSystemVideoIntegrityMap);
    private static readonly ConcurrentDictionary<string, bool> RetroSystemVideoIntegrityCache =
        new(StringComparer.OrdinalIgnoreCase);
    private static readonly Lazy<IReadOnlyDictionary<string, string>> RetroPlatformDescriptions =
        new(LoadRetroPlatformDescriptions);
    private static readonly IReadOnlyDictionary<string, string> BuiltInRetroPlatformDescriptions =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["34f240aa037536c21f26f2fbc809df7a"] = "Portátil da Nintendo lançado em 2011, destacou-se pela tela 3D sem óculos e por preservar a extensa biblioteca da família DS.",
            ["06dd41e4f20e06358854ea408a527c5b"] = "Uma viagem pelos fliperamas: ação imediata, placares, fichas e clássicos criados para partidas rápidas em gabinetes de várias gerações.",
            ["a0fa214c4ba7900cb3e5f5f93b03c434"] = "O Atari 2600 popularizou os cartuchos intercambiáveis e levou a experiência dos arcades para milhões de casas a partir de 1977.",
            ["024bf0731df29bf69ec8e69491ee0194"] = "Sucessor do 2600, o Atari 7800 chegou em 1986 com gráficos aprimorados e compatibilidade com grande parte da biblioteca anterior.",
            ["a8c41dac772fe65dec2b981fadd7b171"] = "Placa de arcade da Sammy baseada na arquitetura do Dreamcast, conhecida por cartuchos e jogos de luta, tiro e ação dos anos 2000.",
            ["26bd034725f7eb39d253423620030dc7"] = "Console de 1982 lembrado por conversões de arcade visualmente avançadas para a época e por seus módulos de expansão.",
            ["f62f55e1cf036d18c5f49f917826878f"] = "A primeira geração do Capcom Play System marcou os arcades com Final Fight, Street Fighter II e grandes clássicos em sprites 2D.",
            ["65f49873f653efd7fe1dd90655725f05"] = "O CPS-2 refinou o hardware 2D da Capcom e virou referência para jogos de luta como Darkstalkers e a série Street Fighter Alpha.",
            ["0dfad3f4b9bc693f226e60efd08e3822"] = "Última plataforma 2D dedicada da Capcom, o CPS-3 ficou associado à animação detalhada de Street Fighter III e Red Earth.",
            ["2a26f76db24874f8c53749bc07ff2f8e"] = "Último console doméstico da Sega, lançado em 1998 com modem integrado, Visual Memory e forte ligação técnica com o arcade NAOMI.",
            ["24017ce00420dc620ff2aa450d4d1125"] = "FinalBurn Neo é um emulador multissistema focado em arcades e consoles selecionados, com ampla cobertura de placas clássicas.",
            ["d348643c67127ed318a092bbbbeea322"] = "Acessório japonês do Famicom lançado em 1986, usava discos regraváveis e recebeu capítulos importantes de séries da Nintendo.",
            ["cf3dd769ee22b9dd6bd65f0ba5dabbf0"] = "Série de jogos portáteis LCD iniciada pela Nintendo em 1980, combinando partidas simples, relógio e design compacto.",
            ["6925b710469d641432e0ccf27257e070"] = "Portátil colorido da Sega lançado em 1990, levou versões de sucessos do Master System e dos arcades para uma tela iluminada.",
            ["9f8c1e89915bc22c3864d24f57eb188f"] = "O Game Boy estreou em 1989 com cartuchos, longa autonomia e Tetris, tornando o jogo portátil um fenômeno mundial.",
            ["aa28453c4bedc6d9c13ee5b164e12a99"] = "Lançado em 2001, o Game Boy Advance evoluiu os portáteis Nintendo com hardware 32-bit e uma biblioteca 2D muito variada.",
            ["706970f4c52ddc44cb736b594eb39ee2"] = "A evolução colorida do Game Boy chegou em 1998, mantendo compatibilidade e dando nova vida a séries como Pokémon e Zelda.",
            ["7c1f5d91f7df9ab699f0a9b2c8579650"] = "Último console doméstico da Atari, o Jaguar chegou em 1993 com arquitetura incomum e jogos que misturavam experiências 2D e 3D.",
            ["95053bc97e884d28673b542078222180"] = "MAME documenta e reproduz o funcionamento de máquinas históricas, priorizando educação, pesquisa e preservação digital.",
            ["194e852fd237069b4f69ba1927371c5e"] = "Console 8-bit da Sega com forte herança dos arcades, tornou-se especialmente duradouro no Brasil com uma biblioteca muito querida.",
            ["024f0c5e1d41076d4c8a184252519c74"] = "Seleção do Master System voltada ao público brasileiro, reunindo a tradição 8-bit da Sega com jogos apresentados em português.",
            ["e40b4b32538a2fa0421cada2aa563bc3"] = "O Mega Drive levou a velocidade e o estilo dos arcades da Sega à era 16-bit, consagrando séries como Sonic, Streets of Rage e Shinobi.",
            ["7fe64f6cdfddcd4d4a0061897a5adda9"] = "Coleção brasileira do Mega Drive, organizada para destacar clássicos 16-bit da Sega com conteúdo e apresentação em português.",
            ["3d7cc8e6cab99045ca6fda67c98285ef"] = "A placa Sega Model 2 ajudou a definir os arcades 3D dos anos 1990 com Virtua Fighter 2, Daytona USA e gráficos poligonais texturizados.",
            ["5defaaf20c099e421ac7650513928f68"] = "O Sega Model 3 elevou os arcades 3D com cenários mais complexos e jogos como Scud Race, Daytona USA 2 e Star Wars Trilogy Arcade.",
            ["9086ec1a6c3b9bc5b6ce792842d51278"] = "Evolução do padrão japonês MSX, o MSX2 ampliou cores, resolução e recursos gráficos para jogos e computadores domésticos.",
            ["bd228881732fca8fece60591036efd31"] = "O Nintendo 64 chegou em 1996 com controle analógico, quatro portas e jogos 3D que definiram novas referências para exploração e multiplayer.",
            ["8afd5bc6acf8295b20eac474c0c5b4eb"] = "Seleção em português do Nintendo 64, organizada em torno de seus grandes mundos 3D, corridas e experiências multiplayer locais.",
            ["392bd490ef4995fc8a28f3bc83ffea07"] = "Sucessora do Model 3, a placa NAOMI estreou em 1998 e compartilhou arquitetura com o Dreamcast, facilitando conversões entre arcade e console.",
            ["9421dd44b900c9b8c0dfef4fcd8d9186"] = "O Nintendo DS popularizou duas telas, toque e novas formas de jogar, reunindo experiências tradicionais e ideias experimentais.",
            ["2ee15b9e606401e983548b3ab50ef577"] = "O Neo Geo aproximou arcade e sala de casa com hardware equivalente, sprites marcantes e uma linhagem histórica de jogos de luta.",
            ["1b1945631404247aa8142017ad2cae1d"] = "O Neo Geo CD levou a biblioteca da SNK ao formato CD em 1994, reduzindo o custo da mídia e preservando a identidade dos arcades.",
            ["338e68fbc0c76f7ac47bd2c1f09349a2"] = "Lançado como Famicom em 1983 e NES no exterior, o 8-bit da Nintendo consolidou Mario, Zelda, Metroid e muitos pilares dos videogames.",
            ["5684bc4cf46234d4000e9313d0f798b6"] = "Portátil monocromático da SNK lançado em 1998, destacou-se pelo controle direcional preciso e por versões compactas de séries da empresa.",
            ["c925b4b4f9e304987f73f91ce6903a79"] = "A versão colorida do portátil da SNK chegou em 1999 com jogos expressivos, ótima direção artística e forte presença de luta e ação.",
            ["91895e79ed1e98ebfdbbadb30c6a5e19"] = "Conhecido como Odyssey² e Philips Videopac, o console combinou cartuchos programáveis, teclado e experiências domésticas no fim dos anos 1970.",
            ["132878ef366258c1877b73cc4109fd86"] = "O compacto PC Engine estreou em 1987 com HuCards e gráficos coloridos, tornando-se uma das plataformas mais marcantes do Japão.",
            ["2f07dd96e8e9f990c9b0764a81bd9761"] = "O CD-ROM² expandiu o PC Engine com muito mais espaço, áudio em CD e apresentações que anteciparam a era multimídia dos consoles.",
            ["052ee8ef7a9394d34415db80f5508fe7"] = "O 32X foi um acessório de 1994 que acrescentou processamento 32-bit ao Mega Drive durante a transição da Sega para uma nova geração.",
            ["ce76bd51b7f0f8fcf7bce68725c30783"] = "Primeiro console doméstico da Sega, o SG-1000 estreou em 1983 e abriu o caminho técnico que levaria ao Master System.",
            ["d077fc407cdd0905903cef2e1548f621"] = "O Super Nintendo levou a Nintendo à era 16-bit em 1990, com som avançado, efeitos gráficos e uma biblioteca célebre de RPGs e ação.",
            ["a242e8f52abcd785f1cdccf98eede8ce"] = "Coleção em português do Super Nintendo, reunindo aventuras, RPGs, ação e corridas da geração 16-bit com organização regional.",
            ["db53c1e82b2763927e9233c609ceefaf"] = "Acessório da Bandai para Super Famicom lançado em 1996, usava cartuchos compactos e permitia combinar dados entre alguns jogos.",
            ["3dd6aceff47bf496baa2eab0433af276"] = "Versão ampliada do PC Engine lançada em 1989, o SuperGrafx ganhou hardware gráfico reforçado e uma pequena biblioteca exclusiva.",
            ["70065c3c55f2d95c857444c546a4776e"] = "Plataforma de arcade criada por Nintendo, Sega e Namco com base no GameCube, usada em títulos como Mario Kart Arcade GP e F-Zero AX."
        };

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
    private readonly Dictionary<string, string> _downloadRootsByItem =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _extractionRootsByItem =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _approvedOpenRoots = new(StringComparer.OrdinalIgnoreCase);
    private readonly CatalogArchiveExtractor _archiveExtractor = new();
    private readonly SemaphoreSlim _extractionQueue = new(1, 1);
    private CatalogRepository? _catalogRepository;
    private CatalogCategory? _selectedCategory;
    private int _currentCatalogPage = 1;
    private string _catalogSearchText = string.Empty;
    private string _installFolderPath = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
    private string _gameLibraryFolderPath = string.Empty;
    private string _temporaryFolderPath = string.Empty;
    private bool _hasCatalogItems;
    private int _libraryTotalItemCount;
    private bool _canGoToPreviousPage;
    private bool _canGoToNextPage;
    private readonly List<CatalogItem> _retroCarouselItems = [];
    private readonly Dictionary<int, ContentControl> _retroCarouselControlsByOffset = [];
    private int _retroCarouselIndex;
    private int _pendingRetroCarouselSteps;
    private int _remainingRetroCarouselClickSteps;
    private bool _isRetroCarouselAnimating;
    private int _retroSystemVideoRequestVersion;
    private int _activeRetroSystemVideoGeneration;
    private string _activeRetroSystemVideoItemId = string.Empty;
    private string _activeRetroSystemVideoPath = string.Empty;
    private MediaElement? _retroSystemVideoPlayer;
    private MediaElement? _retroUniversalVideoPlayer;
    private bool _retroSystemVideoPausedForWindow;
    private bool _retroSystemVideoRestartOnResume;

    private readonly record struct RetroCarouselSlot(
        double Left,
        double Top,
        double Width,
        double Height,
        double Opacity,
        int ZIndex);

    private readonly record struct RetroSystemVideoIntegrity(string Sha256, long Length);

    public ObservableCollection<CatalogCategory> CatalogCategories { get; } = [];
    public ObservableCollection<CatalogCategory> FeaturedCategories { get; } = [];
    public ObservableCollection<CatalogItem> CatalogItems { get; } = [];
    public ObservableCollection<LibrarySystemSummary> LibrarySystems { get; } = [];
    public ObservableCollection<PageNumber> PageNumbers { get; } = [];
    public ObservableCollection<CatalogDownloadJob> DownloadJobs { get; } = [];

    public int LibraryTotalItemCount
    {
        get => _libraryTotalItemCount;
        private set
        {
            if (_libraryTotalItemCount == value) return;
            _libraryTotalItemCount = value;
            OnPropertyChanged();
        }
    }

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
        _gameLibraryFolderPath = LocalDataPaths.ReadGameLibraryFolder() ?? string.Empty;
        RememberApprovedRoot(_installFolderPath);
        if (IsExistingGameLibraryFolder(_gameLibraryFolderPath))
            RememberApprovedRoot(_gameLibraryFolderPath);
        InitializeCatalog();
        UpdateFolderLabels();
        Loaded += StoreWindow_Loaded;
        StateChanged += StoreWindow_StateChanged;

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
            var publicManifestPath = Path.Combine(catalogDirectory, "catalog.json");
            if (!PrivateCatalogResource.TryLoadRepository(
                    publicManifestPath,
                    out var repository))
                repository = CatalogRepository.Load(publicManifestPath);
            _catalogRepository = repository
                ?? throw new InvalidDataException("O catálogo incorporado está vazio.");

            CatalogCategories.Clear();
            foreach (var category in _catalogRepository.Categories)
                CatalogCategories.Add(category);

            FeaturedCategories.Clear();
            foreach (var category in CatalogCategories.Take(6))
                FeaturedCategories.Add(category);

            RefreshLibrarySystems();

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
            LibrarySystems.Clear();
            LibraryTotalItemCount = 0;
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

    private void LibrarySystem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string categoryId }) return;
        var category = CatalogCategories.FirstOrDefault(candidate =>
            candidate.Id.Equals(categoryId, StringComparison.OrdinalIgnoreCase));
        if (category is not null) OpenCatalog(category);
    }

    private void RefreshLibrarySystems()
    {
        LibrarySystems.Clear();
        LibraryTotalItemCount = 0;
        if (_catalogRepository is null) return;

        foreach (var category in CatalogCategories)
        {
            var result = _catalogRepository.Query(
                category.Id,
                searchText: null,
                requestedPage: 1,
                pageSize: Math.Max(1, _catalogRepository.ItemCount));
            var cover = result.Items.FirstOrDefault()?.ImageSource ?? string.Empty;
            LibrarySystems.Add(new LibrarySystemSummary
            {
                Category = category,
                CoverImageSource = cover,
                ItemCount = result.TotalItems
            });
            LibraryTotalItemCount += result.TotalItems;
        }
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
        CancelRetroCarouselAnimation();
        SelectCategory(category);
        _currentCatalogPage = 1;
        _retroCarouselIndex = 0;
        _pendingRetroCarouselSteps = 0;
        SetSearchText(string.Empty);
        ShowPage("Catalog");
    }

    private void SelectCategory(CatalogCategory category)
    {
        foreach (var candidate in CatalogCategories)
            candidate.IsSelected = ReferenceEquals(candidate, category);

        SelectedCategory = category;
        ApplyCategoryTheme(category);
    }

    private void ApplyCategoryTheme(CatalogCategory category)
    {
        Color accent;
        try
        {
            accent = (Color)ColorConverter.ConvertFromString(category.Accent);
        }
        catch (FormatException)
        {
            accent = Color.FromRgb(35, 137, 255);
        }

        // Paletas medidas nos próprios vídeos entregues. Esses tons mantêm
        // contraste suficiente para os controles sem brigar com o fundo.
        var categoryId = category.Id.ToLowerInvariant();
        accent = categoryId switch
        {
            "retro-games" => Color.FromRgb(141, 226, 44),
            "playstation-3" => Color.FromRgb(15, 164, 222),
            "playstation-4" => Color.FromRgb(91, 174, 218),
            "playstation-5" => Color.FromRgb(66, 137, 222),
            "xbox" => Color.FromRgb(72, 126, 60),
            "xbox-360" => Color.FromRgb(67, 134, 57),
            "xbox-one" => Color.FromRgb(67, 138, 69),
            "xbox-series" => Color.FromRgb(62, 130, 66),
            _ => accent
        };

        var bright = Color.FromRgb(
            BlendThemeChannel(accent.R, 255, .22),
            BlendThemeChannel(accent.G, 255, .22),
            BlendThemeChannel(accent.B, 255, .22));
        var perceivedBrightness = (accent.R * 299 + accent.G * 587 + accent.B * 114) / 1000;
        var contrast = perceivedBrightness >= 170
            ? Color.FromRgb(7, 12, 7)
            : Colors.White;

        Resources["CurrentSystemAccentColor"] = accent;
        SetCategoryThemeBrush("CurrentSystemAccentBrush", accent);
        SetCategoryThemeBrush("CurrentSystemBrightBrush", bright);
        SetCategoryThemeBrush("CurrentSystemContrastBrush", contrast);
        SetCategoryThemeBrush(
            "CurrentSystemSurfaceBrush",
            Color.FromArgb(10, accent.R, accent.G, accent.B));
        SetCategoryThemeBrush(
            "CurrentSystemPanelBrush",
            Color.FromArgb(34, accent.R, accent.G, accent.B));
        SetCategoryThemeBrush(
            "CurrentSystemLineBrush",
            Color.FromArgb(76, accent.R, accent.G, accent.B));
        SetCategoryThemeBrush(
            "CurrentSystemHeaderBrush",
            Color.FromArgb(
                232,
                BlendThemeChannel(accent.R, 6, .88),
                BlendThemeChannel(accent.G, 9, .88),
                BlendThemeChannel(accent.B, 12, .88)));
        var sidebar = categoryId == "playstation-3"
            ? Color.FromRgb(2, 4, 6)
            : Color.FromArgb(
                255,
                BlendThemeChannel(accent.R, 5, .95),
                BlendThemeChannel(accent.G, 8, .95),
                BlendThemeChannel(accent.B, 10, .95));
        SetCategoryThemeBrush("CurrentSystemSidebarBrush", sidebar);

        var videoOverlay = new LinearGradientBrush
        {
            StartPoint = new Point(0, .5),
            EndPoint = new Point(1, .5)
        };
        videoOverlay.GradientStops.Add(new GradientStop(Color.FromArgb(255, 0, 0, 0), 0));
        videoOverlay.GradientStops.Add(new GradientStop(Color.FromArgb(210, 0, 0, 0), .30));
        videoOverlay.GradientStops.Add(new GradientStop(Color.FromArgb(92, 0, 0, 0), .62));
        videoOverlay.GradientStops.Add(new GradientStop(Color.FromArgb(0, 0, 0, 0), 1));
        videoOverlay.Freeze();
        Resources["CurrentSystemVideoOverlayBrush"] = videoOverlay;
    }

    private void SetCategoryThemeBrush(string key, Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        Resources[key] = brush;
    }

    private static byte BlendThemeChannel(byte source, byte target, double amount) =>
        (byte)Math.Clamp(
            (int)Math.Round(source + (target - source) * amount),
            byte.MinValue,
            byte.MaxValue);

    private void Search_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_catalogRepository is null) return;

        CancelRetroCarouselAnimation();
        _catalogSearchText = sender is TextBox textBox ? textBox.Text : string.Empty;
        _currentCatalogPage = 1;
        _retroCarouselIndex = 0;
        _pendingRetroCarouselSteps = 0;
        RefreshCatalog();
    }

    private void PreviousPage_Click(object sender, RoutedEventArgs e)
    {
        if (IsRetroCarouselMode) return;
        if (!CanGoToPreviousPage) return;
        _currentCatalogPage--;
        RefreshCatalog();
    }

    private void NextPage_Click(object sender, RoutedEventArgs e)
    {
        if (IsRetroCarouselMode) return;
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
            _retroCarouselItems.Clear();
            PageNumbers.Clear();
            HasCatalogItems = false;
            ConfigureCatalogLayout(isRetroCarousel: false, hasItems: false);
            UpdateCatalogNavigation(1, 1);
            return;
        }

        if (IsRetroCarouselMode)
        {
            RefreshRetroCarousel();
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
        ConfigureCatalogLayout(isRetroCarousel: false, hasItems: HasCatalogItems);
        UpdateCatalogNavigation(result.CurrentPage, result.TotalPages);
        UpdateCatalogHeader(result);

        if (result.TotalItems == 0)
            SetCatalogStatus("Nenhum pacote corresponde à busca neste sistema.");
        else
            SetCatalogStatus(string.Empty);
    }

    private bool IsRetroCarouselMode => SelectedCategory is not null;

    // Every collection now uses the same 2:3 poster presentation approved for
    // the retro carousel. The poster itself is the cell; the action stays below.
    private bool UsesPortraitCarouselCovers => true;

    private void RefreshRetroCarousel()
    {
        if (_catalogRepository is null || SelectedCategory is null) return;

        var previousItemId = _retroCarouselItems.Count > 0 && _retroCarouselIndex < _retroCarouselItems.Count
            ? _retroCarouselItems[_retroCarouselIndex].Id
            : string.Empty;
        var result = _catalogRepository.Query(
            SelectedCategory.Id,
            _catalogSearchText,
            requestedPage: 1,
            pageSize: Math.Max(1, _catalogRepository.ItemCount));

        _retroCarouselItems.Clear();
        _retroCarouselItems.AddRange(result.Items);

        CatalogItems.Clear();
        foreach (var item in result.Items)
            CatalogItems.Add(item);

        if (_retroCarouselItems.Count == 0)
        {
            _retroCarouselIndex = 0;
        }
        else
        {
            var retainedIndex = string.IsNullOrEmpty(previousItemId)
                ? -1
                : _retroCarouselItems.FindIndex(item =>
                    item.Id.Equals(previousItemId, StringComparison.OrdinalIgnoreCase));
            _retroCarouselIndex = retainedIndex >= 0
                ? retainedIndex
                : Math.Clamp(_retroCarouselIndex, 0, _retroCarouselItems.Count - 1);
        }

        PageNumbers.Clear();
        HasCatalogItems = result.TotalItems > 0;
        ConfigureCatalogLayout(isRetroCarousel: true, hasItems: HasCatalogItems);
        CanGoToPreviousPage = _retroCarouselItems.Count > 1;
        CanGoToNextPage = _retroCarouselItems.Count > 1;
        UpdateCatalogHeader(result);
        UpdateRetroCarouselBindings();

        var describesSystems = SelectedCategory.Id.Equals(
            RetroGamesCategoryId,
            StringComparison.OrdinalIgnoreCase);
        SetText("CatalogResultCount", result.TotalItems == 1
            ? describesSystems ? "1 sistema" : "1 item"
            : describesSystems ? $"{result.TotalItems} sistemas" : $"{result.TotalItems} itens");

        if (result.TotalItems == 0)
            SetCatalogStatus("Nenhum item corresponde à busca nesta coleção.");
        else
            SetCatalogStatus(string.Empty);
    }

    private void ConfigureCatalogLayout(bool isRetroCarousel, bool hasItems)
    {
        if (!isRetroCarousel)
            CancelRetroCarouselAnimation();
        if (!isRetroCarousel || !hasItems)
        {
            StopRetroSystemVideo(clearFallback: true);
            StopRetroUniversalVideo();
        }

        SetVisibility("CatalogFolderPanel", !isRetroCarousel);
        SetVisibility("RetroCatalogFolderBar", isRetroCarousel);
        SetVisibility("CatalogGridScrollViewer", !isRetroCarousel && hasItems);
        SetVisibility("RetroCarouselHost", isRetroCarousel && hasItems);
        SetVisibility("CatalogPaginationPanel", !isRetroCarousel && hasItems);
        SetVisibility("CatalogManifestPanel", !isRetroCarousel);
        SetVisibility("RetroCarouselFooter", isRetroCarousel && hasItems);

        if (FindNamed<RowDefinition>("CatalogFooterRow") is { } footerRow)
            footerRow.Height = new GridLength(isRetroCarousel ? 48 : 52);

        // Recover enough vertical room for the 398 px carousel even at the 680 px minimum.
        if (FindNamed<Grid>("CatalogContentPanel") is { } contentPanel)
            contentPanel.Margin = isRetroCarousel
                ? new Thickness(32, 0, 28, 8)
                : new Thickness(32, 0, 28, 22);
    }

    private void UpdateRetroCarouselBindings()
    {
        CancelRetroCarouselAnimation();

        if (!TryGetRetroCarouselControls(out var controls))
        {
            SetText("RetroCarouselPosition", "0 / 0");
            return;
        }

        _retroCarouselControlsByOffset.Clear();
        foreach (var control in controls)
        {
            ClearRetroCarouselControlAnimations(control);
            control.Content = null;
            control.Visibility = Visibility.Collapsed;
            SetRetroCarouselControlInteractive(control, false);
        }

        if (_retroCarouselItems.Count == 0)
        {
            if (FindNamed<ContentControl>("RetroCarouselActionBar") is { } emptyAction)
            {
                emptyAction.Content = null;
                emptyAction.Visibility = Visibility.Collapsed;
                SetRetroCarouselActionInteractive(emptyAction, false);
            }

            SetText("RetroCarouselPosition", "0 / 0");
            StopRetroSystemVideo(clearFallback: true);
            StopRetroUniversalVideo();
            return;
        }

        _retroCarouselIndex = WrapRetroCarouselIndex(_retroCarouselIndex);
        var lastVisibleOffset = GetRetroCarouselLastVisibleOffset();

        for (var offset = 0; offset <= lastVisibleOffset; offset++)
        {
            var control = controls[offset];
            control.Content = GetRetroCarouselItem(offset);
            control.Visibility = Visibility.Visible;
            control.Tag = offset;
            ApplyRetroCarouselSlot(control, GetRetroCarouselSlot(offset));
            SetRetroCarouselControlInteractive(control, true);
            _retroCarouselControlsByOffset[offset] = control;
        }

        // For 2–6 results one unique item waits outside, so movement stays continuous
        // without showing the same cover twice. Seven or more keeps five visible minis.
        if (_retroCarouselItems.Count > 1)
        {
            var spare = controls[^1];
            spare.Content = GetRetroCarouselItem(lastVisibleOffset + 1);
            spare.Visibility = Visibility.Visible;
            spare.Tag = 6;
            ApplyRetroCarouselSlot(spare, GetRetroCarouselSlot(6));
            SetRetroCarouselControlInteractive(spare, false);
            _retroCarouselControlsByOffset[6] = spare;
        }

        UpdateRetroCarouselSelection();
        if (FindNamed<ContentControl>("RetroCarouselActionBar") is { } actionBar)
        {
            actionBar.VerticalAlignment = UsesPortraitCarouselCovers
                ? VerticalAlignment.Bottom
                : VerticalAlignment.Top;
            actionBar.Margin = UsesPortraitCarouselCovers
                ? new Thickness(30, 0, 0, 0)
                : new Thickness(30, 195, 0, 0);
            SetRetroCarouselActionInteractive(actionBar, true);
        }
        StartRetroUniversalVideo();
        StopRetroSystemVideo(clearFallback: true);
    }

    private bool TryGetRetroCarouselControls(out ContentControl[] controls)
    {
        var names = new[]
        {
            "RetroCarouselCurrent",
            "RetroCarouselMini1",
            "RetroCarouselMini2",
            "RetroCarouselMini3",
            "RetroCarouselMini4",
            "RetroCarouselMini5",
            "RetroCarouselIncoming"
        };

        controls = new ContentControl[names.Length];
        for (var index = 0; index < names.Length; index++)
        {
            if (FindNamed<ContentControl>(names[index]) is not { } control)
            {
                controls = [];
                return false;
            }

            controls[index] = control;
        }

        return true;
    }

    private int GetRetroCarouselLastVisibleOffset() =>
        _retroCarouselItems.Count <= 1
            ? 0
            : Math.Min(5, _retroCarouselItems.Count - 2);

    private int GetExpectedRetroCarouselControlCount() =>
        _retroCarouselItems.Count <= 1
            ? _retroCarouselItems.Count
            : GetRetroCarouselLastVisibleOffset() + 2;

    private static void SetRetroCarouselControlInteractive(ContentControl control, bool isInteractive)
    {
        control.IsEnabled = isInteractive;
        control.IsHitTestVisible = isInteractive;
        control.Focusable = false;
        KeyboardNavigation.SetIsTabStop(control, false);
        AutomationProperties.SetIsOffscreenBehavior(
            control,
            isInteractive ? IsOffscreenBehavior.Onscreen : IsOffscreenBehavior.Offscreen);
        AutomationProperties.SetName(
            control,
            isInteractive && control.Content is CatalogItem item ? item.Title : string.Empty);
        AutomationProperties.SetHelpText(control, string.Empty);
    }

    private static void SetRetroCarouselActionInteractive(ContentControl actionBar, bool isInteractive)
    {
        actionBar.IsEnabled = isInteractive;
        actionBar.IsHitTestVisible = isInteractive;
        actionBar.Focusable = false;
        KeyboardNavigation.SetIsTabStop(actionBar, false);
        AutomationProperties.SetIsOffscreenBehavior(
            actionBar,
            isInteractive ? IsOffscreenBehavior.Onscreen : IsOffscreenBehavior.Offscreen);
    }

    private void UpdateRetroCarouselSelection()
    {
        if (_retroCarouselItems.Count == 0) return;

        var current = _retroCarouselItems[WrapRetroCarouselIndex(_retroCarouselIndex)];
        SetText("RetroCarouselTitle", current.Title);
        SetText("RetroCarouselPackageType", $"{current.Category}  •  {current.Version}");
        SetText(
            "RetroCarouselPackageDescription",
            FormatRetroPlatformDescription(
                RetroPlatformDescriptions.Value.TryGetValue(current.Id, out var platformDescription)
                    ? platformDescription
                    : current.Description.Length > 0
                        ? current.Description
                        : BuildCatalogPackageDescription(current)));
        SetText("RetroCarouselSystemGlyph", string.IsNullOrWhiteSpace(current.SystemGlyph)
            ? current.SystemCode
            : current.SystemGlyph);
        SetText("RetroCarouselPosition", $"{_retroCarouselIndex + 1} / {_retroCarouselItems.Count}");
        UpdateRetroCarouselSystemIcon(current);

        if (FindNamed<ContentControl>("RetroCarouselActionBar") is { } actionBar)
        {
            actionBar.Content = current;
            actionBar.Visibility = Visibility.Visible;
        }

        if (FindNamed<Button>("RetroCarouselPreviousButton") is { } previousButton)
            previousButton.IsEnabled = _retroCarouselItems.Count > 1;
        if (FindNamed<Button>("RetroCarouselNextButton") is { } nextButton)
            nextButton.IsEnabled = _retroCarouselItems.Count > 1;
    }

    private bool HasPendingRetroCarouselMotion =>
        _isRetroCarouselAnimating
        || _pendingRetroCarouselSteps != 0
        || _remainingRetroCarouselClickSteps != 0;

    private void StartRetroUniversalVideo()
    {
        if (!IsRetroCarouselVisible || WindowState == WindowState.Minimized)
            return;

        var videoPath = ResolveRetroUniversalVideoPath(SelectedCategory?.Id);
        if (videoPath is null || FindNamed<Grid>("RetroUniversalVideoPlayerHost") is not { } host)
        {
            StopRetroUniversalVideo();
            return;
        }

        if (_retroUniversalVideoPlayer is { Source: not null } active
            && active.Source.IsFile
            && Path.GetFullPath(active.Source.LocalPath).Equals(videoPath, StringComparison.OrdinalIgnoreCase))
        {
            try { active.Play(); }
            catch (InvalidOperationException) { StopRetroUniversalVideo(); }
            return;
        }

        StopRetroUniversalVideo();
        var player = new MediaElement
        {
            LoadedBehavior = MediaState.Manual,
            UnloadedBehavior = MediaState.Manual,
            IsMuted = true,
            Volume = 0,
            Stretch = Stretch.UniformToFill,
            Focusable = false,
            IsHitTestVisible = false
        };
        player.MediaEnded += RetroUniversalVideo_MediaEnded;
        player.MediaFailed += RetroUniversalVideo_MediaFailed;
        _retroUniversalVideoPlayer = player;
        host.Children.Add(player);
        try
        {
            player.Source = new Uri(videoPath, UriKind.Absolute);
            player.Play();
        }
        catch (Exception exception) when (exception is InvalidOperationException
                                           or UriFormatException
                                           or NotSupportedException)
        {
            Debug.WriteLine($"Vídeo universal indisponível: {exception.Message}");
            StopRetroUniversalVideo();
        }
    }

    private void RetroUniversalVideo_MediaEnded(object? sender, RoutedEventArgs e)
    {
        if (sender is not MediaElement player || !ReferenceEquals(player, _retroUniversalVideoPlayer))
            return;
        if (!IsRetroCarouselVisible || WindowState == WindowState.Minimized)
            return;
        try
        {
            player.Position = TimeSpan.Zero;
            player.Play();
        }
        catch (InvalidOperationException) { StopRetroUniversalVideo(); }
    }

    private void RetroUniversalVideo_MediaFailed(object? sender, ExceptionRoutedEventArgs e)
    {
        if (sender is MediaElement player && ReferenceEquals(player, _retroUniversalVideoPlayer))
        {
            Debug.WriteLine($"Vídeo universal não pôde ser reproduzido: {e.ErrorException?.Message}");
            StopRetroUniversalVideo();
        }
    }

    private void PauseRetroUniversalVideo()
    {
        if (_retroUniversalVideoPlayer is not { Source: not null } player) return;
        try { player.Pause(); }
        catch (InvalidOperationException) { }
    }

    private void ResumeRetroUniversalVideo()
    {
        if (!IsRetroCarouselVisible || WindowState == WindowState.Minimized) return;
        if (_retroUniversalVideoPlayer is { Source: not null } player)
        {
            try { player.Play(); return; }
            catch (InvalidOperationException) { StopRetroUniversalVideo(); }
        }
        StartRetroUniversalVideo();
    }

    private void StopRetroUniversalVideo()
    {
        var player = _retroUniversalVideoPlayer;
        _retroUniversalVideoPlayer = null;
        if (player is null) return;
        player.MediaEnded -= RetroUniversalVideo_MediaEnded;
        player.MediaFailed -= RetroUniversalVideo_MediaFailed;
        try { player.Stop(); }
        catch (InvalidOperationException) { }
        try
        {
            player.Close();
            player.Source = null;
        }
        catch (InvalidOperationException) { }
        if (FindNamed<Grid>("RetroUniversalVideoPlayerHost") is { } host)
            host.Children.Remove(player);
    }

    private void SwitchRetroSystemVideo(CatalogItem item)
    {
        var videoPath = ResolveRetroSystemVideoPath(item);
        if (videoPath is null || !IsRetroCarouselVisible || WindowState == WindowState.Minimized)
        {
            CloseRetroSystemVideoCore(clearFallback: false);
            if (WindowState == WindowState.Minimized && IsRetroCarouselMode)
                _retroSystemVideoPausedForWindow = true;
            return;
        }

        _retroSystemVideoPausedForWindow = false;
        if (_retroSystemVideoPlayer is { Source: not null } currentPlayer
            && _activeRetroSystemVideoPath.Equals(videoPath, StringComparison.OrdinalIgnoreCase)
            && IsActiveRetroSystemVideo(currentPlayer))
        {
            try { currentPlayer.Play(); }
            catch (InvalidOperationException exception)
            {
                Debug.WriteLine($"Não foi possível continuar o vídeo universal: {exception.Message}");
            }
            return;
        }

        var requestVersion = ++_retroSystemVideoRequestVersion;
        CloseRetroSystemVideoCore(clearFallback: false);
        if (Dispatcher.HasShutdownStarted) return;

        Dispatcher.BeginInvoke(
            DispatcherPriority.ContextIdle,
            new Action(() =>
            {
                if (requestVersion != _retroSystemVideoRequestVersion
                    || !IsRetroCarouselVisible
                    || WindowState == WindowState.Minimized)
                    return;

                OpenRetroSystemVideo(item, videoPath, requestVersion);
            }));
    }

    private void OpenRetroSystemVideo(CatalogItem item, string videoPath, int generation)
    {
        if (FindNamed<Grid>("RetroSystemVideoPlayerHost") is not { } playerHost)
            return;

        CloseRetroSystemVideoCore(clearFallback: false);
        var player = new MediaElement
        {
            LoadedBehavior = MediaState.Manual,
            UnloadedBehavior = MediaState.Manual,
            IsMuted = true,
            Volume = 0,
            Stretch = Stretch.UniformToFill,
            Opacity = 0,
            Focusable = false,
            IsHitTestVisible = false,
            Tag = generation
        };
        player.MediaOpened += RetroSystemVideo_MediaOpened;
        player.MediaEnded += RetroSystemVideo_MediaEnded;
        player.MediaFailed += RetroSystemVideo_MediaFailed;

        _retroSystemVideoPlayer = player;
        _activeRetroSystemVideoGeneration = generation;
        _activeRetroSystemVideoItemId = item.Id;
        _activeRetroSystemVideoPath = videoPath;
        playerHost.Children.Add(player);

        try
        {
            player.Source = new Uri(videoPath, UriKind.Absolute);
            player.Play();
        }
        catch (Exception exception) when (exception is InvalidOperationException
                                           or UriFormatException
                                           or NotSupportedException)
        {
            Debug.WriteLine($"Vídeo de sistema indisponível: {exception.Message}");
            CloseRetroSystemVideoCore(clearFallback: false);
        }
    }

    private void RetroSystemVideo_MediaOpened(object? sender, RoutedEventArgs e)
    {
        if (sender is not MediaElement player || !IsActiveRetroSystemVideo(player))
            return;

        if (!IsReadyRetroSystemVideoSession(player))
        {
            player.Pause();
            return;
        }

        if (!IsRetroCarouselVisible || WindowState == WindowState.Minimized)
        {
            player.Pause();
            _retroSystemVideoPausedForWindow = true;
            return;
        }

        player.IsMuted = true;
        player.Volume = 0;
        player.BeginAnimation(OpacityProperty, null);
        player.Opacity = 1;
        player.Play();
    }

    private void RetroSystemVideo_MediaEnded(object? sender, RoutedEventArgs e)
    {
        if (sender is not MediaElement player || !IsActiveRetroSystemVideo(player))
            return;

        if (WindowState == WindowState.Minimized || _retroSystemVideoPausedForWindow)
        {
            _retroSystemVideoRestartOnResume = true;
            return;
        }

        if (!IsReadyRetroSystemVideoSession(player) || !IsRetroCarouselVisible)
            return;

        try
        {
            player.Position = TimeSpan.Zero;
            _retroSystemVideoRestartOnResume = false;
            player.Play();
        }
        catch (InvalidOperationException exception)
        {
            Debug.WriteLine($"Não foi possível repetir o vídeo de sistema: {exception.Message}");
            StopRetroSystemVideo(clearFallback: false);
        }
    }

    private void RetroSystemVideo_MediaFailed(object? sender, ExceptionRoutedEventArgs e)
    {
        if (sender is not MediaElement player || !IsReadyRetroSystemVideoSession(player))
            return;

        Debug.WriteLine($"Vídeo de sistema não pôde ser reproduzido: {e.ErrorException?.Message}");
        StopRetroSystemVideo(clearFallback: false);
    }

    private bool IsActiveRetroSystemVideo(MediaElement player)
    {
        if (!ReferenceEquals(player, _retroSystemVideoPlayer)
            || player.Tag is not int generation
            || generation != _activeRetroSystemVideoGeneration
            || player.Source is null
            || string.IsNullOrEmpty(_activeRetroSystemVideoItemId)
            || string.IsNullOrEmpty(_activeRetroSystemVideoPath))
            return false;

        try
        {
            var sourceSnapshot = Path.GetFullPath(player.Source.LocalPath);
            return player.Source.IsFile
                   && sourceSnapshot.Equals(_activeRetroSystemVideoPath, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (exception is ArgumentException
                                           or IOException
                                           or NotSupportedException)
        {
            Debug.WriteLine($"Origem de vídeo inválida: {exception.Message}");
            return false;
        }
    }

    private bool IsReadyRetroSystemVideoSession(MediaElement player) =>
        IsActiveRetroSystemVideo(player)
        && _activeRetroSystemVideoGeneration == _retroSystemVideoRequestVersion;

    private void PauseRetroSystemVideoForWindow()
    {
        PauseRetroUniversalVideo();
        if (!IsRetroCarouselMode) return;

        ++_retroSystemVideoRequestVersion;
        _retroSystemVideoPausedForWindow = true;
        if (_retroSystemVideoPlayer is not { Source: not null } player)
            return;

        try
        {
            player.Pause();
        }
        catch (InvalidOperationException exception)
        {
            Debug.WriteLine($"Não foi possível pausar o vídeo de sistema: {exception.Message}");
        }
    }

    private void ResumeRetroSystemVideoForWindow()
    {
        ResumeRetroUniversalVideo();
        if (!_retroSystemVideoPausedForWindow) return;
        _retroSystemVideoPausedForWindow = false;
        if (!IsRetroCarouselVisible || _retroCarouselItems.Count == 0) return;

        var current = _retroCarouselItems[_retroCarouselIndex];
        var expectedPath = ResolveRetroSystemVideoPath(current);
        if (expectedPath is not null
            && _retroSystemVideoPlayer is { Source: not null } player
            && _activeRetroSystemVideoPath.Equals(expectedPath, StringComparison.OrdinalIgnoreCase)
            && IsActiveRetroSystemVideo(player))
        {
            try
            {
                _activeRetroSystemVideoGeneration = _retroSystemVideoRequestVersion;
                player.Tag = _activeRetroSystemVideoGeneration;
                player.IsMuted = true;
                player.Volume = 0;
                if (_retroSystemVideoRestartOnResume)
                {
                    player.Position = TimeSpan.Zero;
                    _retroSystemVideoRestartOnResume = false;
                }
                player.Play();
                return;
            }
            catch (InvalidOperationException exception)
            {
                Debug.WriteLine($"Não foi possível retomar o vídeo de sistema: {exception.Message}");
            }
        }

        StopRetroSystemVideo(clearFallback: true);
    }

    private void StopRetroSystemVideo(bool clearFallback)
    {
        ++_retroSystemVideoRequestVersion;
        _retroSystemVideoPausedForWindow = false;
        CloseRetroSystemVideoCore(clearFallback);
    }

    private void CloseRetroSystemVideoCore(bool clearFallback)
    {
        var player = _retroSystemVideoPlayer;
        _retroSystemVideoPlayer = null;
        _activeRetroSystemVideoGeneration = 0;
        _activeRetroSystemVideoItemId = string.Empty;
        _activeRetroSystemVideoPath = string.Empty;
        _retroSystemVideoRestartOnResume = false;

        if (player is not null)
        {
            player.MediaOpened -= RetroSystemVideo_MediaOpened;
            player.MediaEnded -= RetroSystemVideo_MediaEnded;
            player.MediaFailed -= RetroSystemVideo_MediaFailed;
            player.BeginAnimation(OpacityProperty, null);
            player.Opacity = 0;
            player.Tag = null;
            try
            {
                player.Stop();
            }
            catch (InvalidOperationException exception)
            {
                Debug.WriteLine($"Não foi possível parar o vídeo de sistema: {exception.Message}");
            }

            try
            {
                player.Close();
                player.Source = null;
            }
            catch (InvalidOperationException exception)
            {
                Debug.WriteLine($"Não foi possível liberar o vídeo de sistema: {exception.Message}");
            }

            if (FindNamed<Grid>("RetroSystemVideoPlayerHost") is { } playerHost)
                playerHost.Children.Remove(player);
        }

        if (clearFallback && FindNamed<Image>("RetroSystemVideoFallback") is { } fallback)
            fallback.DataContext = null;
    }

    private static string? ResolveRetroSystemVideoPath(CatalogItem item)
    {
        var root = GetRetroSystemVideoRoot();
        if (root is null) return null;
        return RetroSystemVideoMap.Value.TryGetValue(item.Id, out var mappedFile)
            ? ResolveRetroSystemVideoCandidate(root, mappedFile)
            : ResolveRetroSystemVideoCandidate(root, $"{item.Id}.mp4");
    }

    private static string? ResolveRetroUniversalVideoPath(string? categoryId)
    {
        try
        {
            var fileName = categoryId?.ToLowerInvariant() switch
            {
                "system-tools" => "Turborama-background-system-tools.mp4",
                "playstation-2" or "playstation-2-br"
                    => "Turborama-background-ps2.mp4",
                "playstation-4" => "Turborama-background-ps4.mp4",
                "playstation-5" => "Turborama-background-ps5.mp4",
                "sega-saturn" => "Turborama-background-sega-saturn.mp4",
                "xbox" or "xbox-360" or "xbox-one" or "xbox-series"
                    => "Turborama-background-xbox-one-x.mp4",
                "nintendo-switch" => "Turborama-background-nintendo.mp4",
                "nintendo-wii" or "nintendo-wii-u"
                    => "Turborama-background-nintendo-wii.mp4",
                "windows" => "Turborama-background-windows.mp4",
                "retro-games" => "Turborama-background-retro.mp4",
                _ => "Turborama-background.mp4"
            };
            var path = Path.GetFullPath(Path.Combine(
                AppContext.BaseDirectory,
                fileName));
            if (!IsValidMp4File(path)
                && !fileName.Equals("Turborama-background.mp4", StringComparison.OrdinalIgnoreCase))
            {
                path = Path.GetFullPath(Path.Combine(
                    AppContext.BaseDirectory,
                    "Turborama-background.mp4"));
            }
            return IsValidMp4File(path) ? path : null;
        }
        catch (Exception exception) when (exception is IOException
                                           or UnauthorizedAccessException
                                           or ArgumentException
                                           or NotSupportedException)
        {
            return null;
        }
    }

    private static string? GetRetroSystemVideoRoot()
    {
        try
        {
            var installed = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "Turborama-system-videos"));
            if (Directory.Exists(installed) && !HasReparsePointInRetroSystemVideoPath(installed))
                return installed;

            var development = Path.GetFullPath(Path.Combine(
                AppContext.BaseDirectory,
                "Assets",
                "Catalog",
                "SystemVideos"));
            return Directory.Exists(development) && !HasReparsePointInRetroSystemVideoPath(development)
                ? development
                : null;
        }
        catch (Exception exception) when (exception is IOException
                                           or UnauthorizedAccessException
                                           or ArgumentException
                                           or NotSupportedException)
        {
            return null;
        }
    }

    private static string? ResolveRetroSystemVideoCandidate(string videoRoot, string? relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath)) return null;

        try
        {
            var fileName = relativePath.Trim();
            if (fileName.Length > 240
                || Path.IsPathFullyQualified(fileName)
                || !Path.GetFileName(fileName).Equals(fileName, StringComparison.Ordinal)
                || !Path.GetExtension(fileName).Equals(".mp4", StringComparison.OrdinalIgnoreCase)
                || !Directory.Exists(videoRoot)
                || HasReparsePointInRetroSystemVideoPath(videoRoot))
                return null;

            var candidate = Path.GetFullPath(Path.Combine(videoRoot, fileName));
            var normalizedRoot = Path.TrimEndingDirectorySeparator(videoRoot)
                                 + Path.DirectorySeparatorChar;
            if (!candidate.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase)
                || !IsValidMp4File(candidate)
                || !IsTrustedRetroSystemVideo(candidate, fileName)
                || HasReparsePointInRetroSystemVideoPath(videoRoot, candidate))
                return null;

            return candidate;
        }
        catch (Exception exception) when (exception is ArgumentException
                                           or IOException
                                           or UnauthorizedAccessException
                                           or NotSupportedException)
        {
            Debug.WriteLine($"Caminho de vídeo de sistema inválido: {exception.Message}");
            return null;
        }
    }

    private static bool HasReparsePointInRetroSystemVideoPath(
        string videoRoot,
        string? leafPath = null)
    {
        var paths = leafPath is null ? new[] { videoRoot } : new[] { videoRoot, leafPath };
        return paths.Any(path => File.Exists(path) || Directory.Exists(path)
            ? (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0
            : true);
    }

    private static bool IsValidMp4File(string path)
    {
        if (!File.Exists(path)) return false;
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length < 12) return false;
        Span<byte> header = stackalloc byte[12];
        if (stream.Read(header) != header.Length) return false;
        return header[4] == (byte)'f'
               && header[5] == (byte)'t'
               && header[6] == (byte)'y'
               && header[7] == (byte)'p';
    }

    private static bool IsTrustedRetroSystemVideo(string path, string fileName)
    {
        if (!RetroSystemVideoIntegrityMap.Value.TryGetValue(fileName, out var expected))
            return false;
        var file = new FileInfo(path);
        if (file.Length != expected.Length) return false;

        var cacheKey = $"{path}|{file.Length}|{file.LastWriteTimeUtc.Ticks}";
        return RetroSystemVideoIntegrityCache.GetOrAdd(cacheKey, _ =>
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            var actualHash = SHA256.HashData(stream);
            var expectedHash = Convert.FromHexString(expected.Sha256);
            return actualHash.Length == expectedHash.Length
                   && CryptographicOperations.FixedTimeEquals(actualHash, expectedHash);
        });
    }

    private static IReadOnlyDictionary<string, string> LoadRetroSystemVideoMap()
    {
        var entries = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var stream = typeof(StoreWindow).Assembly.GetManifestResourceStream(
                "Turborama.SystemVideos.json");
            if (stream is null) return entries;
            var manifestLength = stream.Length;
            if (manifestLength <= 0 || manifestLength > MaximumRetroSystemVideoManifestBytes)
                return entries;

            var manifestBytes = new byte[(int)manifestLength];
            stream.ReadExactly(manifestBytes);
            using var document = JsonDocument.Parse(
                manifestBytes,
                new JsonDocumentOptions
                {
                    AllowTrailingCommas = true,
                    CommentHandling = JsonCommentHandling.Skip,
                    MaxDepth = 4
                });

            var mapElement = document.RootElement;
            if (mapElement.ValueKind == JsonValueKind.Object
                && mapElement.TryGetProperty("videos", out var nestedVideos)
                && nestedVideos.ValueKind == JsonValueKind.Object)
                mapElement = nestedVideos;

            if (mapElement.ValueKind != JsonValueKind.Object)
                return entries;

            foreach (var entry in mapElement.EnumerateObject())
            {
                if (entry.Value.ValueKind != JsonValueKind.String) continue;
                var fileName = entry.Value.GetString()?.Trim();
                if (!string.IsNullOrWhiteSpace(entry.Name) && !string.IsNullOrWhiteSpace(fileName))
                    entries[entry.Name] = fileName;
            }
        }
        catch (Exception exception) when (exception is IOException
                                           or UnauthorizedAccessException
                                           or JsonException
                                           or ArgumentException
                                           or NotSupportedException)
        {
            Debug.WriteLine($"Manifesto de vídeos de sistema ignorado: {exception.Message}");
        }

        return entries;
    }

    private static IReadOnlyDictionary<string, RetroSystemVideoIntegrity> LoadRetroSystemVideoIntegrityMap()
    {
        var entries = new Dictionary<string, RetroSystemVideoIntegrity>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var stream = typeof(StoreWindow).Assembly.GetManifestResourceStream(
                "Turborama.SystemVideoIntegrity.json");
            if (stream is null || stream.Length <= 0 || stream.Length > MaximumRetroSystemVideoManifestBytes)
                return entries;
            using var document = JsonDocument.Parse(stream, new JsonDocumentOptions { MaxDepth = 4 });
            if (document.RootElement.ValueKind != JsonValueKind.Object) return entries;
            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (property.Value.ValueKind != JsonValueKind.Object
                    || !property.Value.TryGetProperty("sha256", out var hashElement)
                    || !property.Value.TryGetProperty("length", out var lengthElement))
                    continue;
                var hash = hashElement.GetString();
                if (hash is { Length: 64 }
                    && lengthElement.TryGetInt64(out var length)
                    && length > 0)
                    entries[property.Name] = new RetroSystemVideoIntegrity(hash, length);
            }
        }
        catch (Exception exception) when (exception is IOException
                                           or JsonException
                                           or FormatException
                                           or ArgumentException
                                           or NotSupportedException)
        {
            Debug.WriteLine($"Integridade de vídeos incorporada ignorada: {exception.Message}");
        }
        return entries;
    }

    private static IReadOnlyDictionary<string, string> LoadRetroPlatformDescriptions()
    {
        var entries = new Dictionary<string, string>(
            BuiltInRetroPlatformDescriptions,
            StringComparer.OrdinalIgnoreCase);
        try
        {
            using var stream = typeof(StoreWindow).Assembly.GetManifestResourceStream(
                "Turborama.PlatformDescriptions.json");
            if (stream is null || stream.Length <= 0 || stream.Length > MaximumRetroPlatformDescriptionsBytes)
                return entries;

            using var document = JsonDocument.Parse(stream, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 3
            });
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                return entries;

            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (property.Value.ValueKind != JsonValueKind.String) continue;
                var value = property.Value.GetString();
                if (!string.IsNullOrWhiteSpace(property.Name) && !string.IsNullOrWhiteSpace(value))
                    entries[property.Name] = value.Trim();
            }
        }
        catch (Exception exception) when (exception is IOException
                                           or JsonException
                                           or ArgumentException
                                           or NotSupportedException)
        {
            Debug.WriteLine($"Descrições incorporadas ignoradas: {exception.Message}");
        }

        return entries;
    }

    private static string BuildCatalogPackageDescription(CatalogItem item)
    {
        if (!string.IsNullOrWhiteSpace(item.Description)) return item.Description.Trim();
        var subtitle = string.IsNullOrWhiteSpace(item.Subtitle)
            ? "Pacote organizado para download pelo catálogo Turborama"
            : item.Subtitle.Trim();
        return $"{subtitle}. Este título pertence à coleção {item.Category} e mantém a capa própria do pacote. "
               + $"A versão apresentada é {item.Version}, com download, pausa, retomada e abertura da pasta preservados. "
               + "Use o botão abaixo da capa selecionada para iniciar ou continuar a operação.";
    }

    private static string FormatRetroPlatformDescription(string? description)
    {
        var normalized = string.Join(
            ' ',
            (description ?? string.Empty).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        foreach (var marker in new[] { " Italiano:", " English:", " Inglês:" })
        {
            var markerIndex = normalized.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (markerIndex > 0) normalized = normalized[..markerIndex].TrimEnd();
        }
        if (normalized.Length == 0)
            normalized = "Pacote Turborama organizado para download, retomada e instalação na coleção selecionada.";

        var allWords = normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var visibleWords = new List<string>();
        var visibleCharacters = 0;
        foreach (var word in allWords)
        {
            if (visibleWords.Count >= 50 || visibleCharacters + word.Length + 1 > 340) break;
            visibleWords.Add(word);
            visibleCharacters += word.Length + 1;
        }
        var truncated = visibleWords.Count < allWords.Length;
        while (visibleWords.Count < 5) visibleWords.Add("•");

        var lines = new List<string>(5);
        var consumed = 0;
        for (var lineIndex = 0; lineIndex < 5; lineIndex++)
        {
            var wordsRemaining = visibleWords.Count - consumed;
            var linesRemaining = 5 - lineIndex;
            var take = (int)Math.Ceiling(wordsRemaining / (double)linesRemaining);
            lines.Add(string.Join(' ', visibleWords.Skip(consumed).Take(take)));
            consumed += take;
        }
        if (truncated) lines[^1] = lines[^1].TrimEnd('.', ',', ';', ':') + "…";
        return string.Join(Environment.NewLine, lines);
    }

    private CatalogItem GetRetroCarouselItem(int offset) =>
        _retroCarouselItems[WrapRetroCarouselIndex(_retroCarouselIndex + offset)];

    private int WrapRetroCarouselIndex(int index)
    {
        if (_retroCarouselItems.Count == 0) return 0;
        var wrapped = index % _retroCarouselItems.Count;
        return wrapped < 0 ? wrapped + _retroCarouselItems.Count : wrapped;
    }

    private RetroCarouselSlot GetRetroCarouselSlot(int offset)
    {
        var selectedHeight = UsesPortraitCarouselCovers ? 375 : 188;
        var compactHeight = UsesPortraitCarouselCovers ? 210 : 105;
        return offset switch
        {
            -1 => new RetroCarouselSlot(-270, 0, 250, selectedHeight, 1, 35),
            0 => new RetroCarouselSlot(30, 0, 250, selectedHeight, 1, 40),
            1 => new RetroCarouselSlot(320, 0, 140, compactHeight, .95, 25),
            2 => new RetroCarouselSlot(470, 0, 140, compactHeight, .90, 24),
            3 => new RetroCarouselSlot(620, 0, 140, compactHeight, .85, 23),
            4 => new RetroCarouselSlot(770, 0, 140, compactHeight, .80, 22),
            5 => new RetroCarouselSlot(920, 0, 140, compactHeight, .75, 21),
            6 => new RetroCarouselSlot(1070, 0, 140, compactHeight, 1, 20),
            _ => throw new ArgumentOutOfRangeException(nameof(offset))
        };
    }

    private void ApplyRetroCarouselSlot(ContentControl control, RetroCarouselSlot slot)
    {
        const double nativeWidth = 250;
        var nativeHeight = UsesPortraitCarouselCovers ? 375 : 188;
        Canvas.SetLeft(control, 0);
        Canvas.SetTop(control, 0);
        control.Width = nativeWidth;
        control.Height = nativeHeight;
        control.Opacity = slot.Opacity;
        Panel.SetZIndex(control, slot.ZIndex);
        var (scale, translate) = GetRetroCarouselTransforms(control);
        scale.ScaleX = slot.Width / nativeWidth;
        scale.ScaleY = slot.Height / nativeHeight;
        translate.X = slot.Left;
        translate.Y = slot.Top;
    }

    private static (ScaleTransform Scale, TranslateTransform Translate) GetRetroCarouselTransforms(
        ContentControl control)
    {
        if (control.RenderTransform is TransformGroup existing
            && existing.Children.Count == 2
            && existing.Children[0] is ScaleTransform existingScale
            && existing.Children[1] is TranslateTransform existingTranslate)
            return (existingScale, existingTranslate);

        var scale = new ScaleTransform(1, 1);
        var translate = new TranslateTransform();
        var group = new TransformGroup();
        group.Children.Add(scale);
        group.Children.Add(translate);
        control.RenderTransformOrigin = new Point(0, 0);
        control.RenderTransform = group;
        return (scale, translate);
    }

    private static void ClearRetroCarouselControlAnimations(ContentControl control)
    {
        control.BeginAnimation(Canvas.LeftProperty, null);
        control.BeginAnimation(Canvas.TopProperty, null);
        control.BeginAnimation(FrameworkElement.WidthProperty, null);
        control.BeginAnimation(FrameworkElement.HeightProperty, null);
        control.BeginAnimation(UIElement.OpacityProperty, null);
        var (scale, translate) = GetRetroCarouselTransforms(control);
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        translate.BeginAnimation(TranslateTransform.XProperty, null);
        translate.BeginAnimation(TranslateTransform.YProperty, null);
    }

    private void UpdateRetroCarouselSystemIcon(CatalogItem item)
    {
        var icon = FindNamed<Image>("RetroCarouselSystemIcon");
        var glyph = FindNamed<TextBlock>("RetroCarouselSystemGlyph");
        if (icon is null) return;

        var iconPath = Path.Combine(
            AppContext.BaseDirectory,
            "Assets",
            "Catalog",
            "SystemIcons",
            $"{item.Id}.png");
        if (!File.Exists(iconPath))
        {
            try
            {
                var resourceUri = new Uri(
                    $"pack://application:,,,/Assets/Catalog/SystemIcons/{item.Id}.png",
                    UriKind.Absolute);
                var resource = Application.GetResourceStream(resourceUri);
                if (resource is not null)
                {
                    using (resource.Stream)
                    {
                        var embeddedBitmap = new BitmapImage();
                        embeddedBitmap.BeginInit();
                        embeddedBitmap.CacheOption = BitmapCacheOption.OnLoad;
                        embeddedBitmap.DecodePixelWidth = 96;
                        embeddedBitmap.StreamSource = resource.Stream;
                        embeddedBitmap.EndInit();
                        embeddedBitmap.Freeze();
                        icon.Source = embeddedBitmap;
                    }
                    icon.Visibility = Visibility.Visible;
                    if (glyph is not null) glyph.Visibility = Visibility.Collapsed;
                    return;
                }
            }
            catch (Exception exception) when (exception is IOException
                                               or UnauthorizedAccessException
                                               or NotSupportedException
                                               or FileFormatException
                                               or ArgumentException)
            {
            }

            icon.Source = null;
            icon.Visibility = Visibility.Collapsed;
            if (glyph is not null) glyph.Visibility = Visibility.Visible;
            return;
        }

        try
        {
            using var stream = File.OpenRead(iconPath);
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.DecodePixelWidth = 96;
            bitmap.StreamSource = stream;
            bitmap.EndInit();
            bitmap.Freeze();
            icon.Source = bitmap;
            icon.Visibility = Visibility.Visible;
            if (glyph is not null) glyph.Visibility = Visibility.Collapsed;
        }
        catch (Exception exception) when (exception is IOException
                                           or UnauthorizedAccessException
                                           or NotSupportedException
                                           or FileFormatException
                                           or ArgumentException)
        {
            icon.Source = null;
            icon.Visibility = Visibility.Collapsed;
            if (glyph is not null) glyph.Visibility = Visibility.Visible;
        }
    }

    private void RetroCarouselPrevious_Click(object sender, RoutedEventArgs e) =>
        QueueRetroCarouselMove(-1);

    private void RetroCarouselNext_Click(object sender, RoutedEventArgs e) =>
        QueueRetroCarouselMove(1);

    private void RetroCarouselItem_Click(object sender, RoutedEventArgs e)
    {
        var source = sender as FrameworkElement;
        if (FindCarouselNavigationOffset(source) is { } navigationOffset)
        {
            QueueRetroCarouselClickMove(navigationOffset);
            return;
        }

        var target = source?.Tag as CatalogItem ?? source?.DataContext as CatalogItem;
        if (target is null || _retroCarouselItems.Count < 2) return;

        var targetIndex = _retroCarouselItems.FindIndex(item =>
            item.Id.Equals(target.Id, StringComparison.OrdinalIgnoreCase));
        if (targetIndex < 0 || targetIndex == _retroCarouselIndex) return;

        var forwardSteps = (targetIndex - _retroCarouselIndex + _retroCarouselItems.Count)
                           % _retroCarouselItems.Count;
        var backwardSteps = forwardSteps - _retroCarouselItems.Count;
        QueueRetroCarouselClickMove(Math.Abs(backwardSteps) < forwardSteps
            ? backwardSteps
            : forwardSteps);
    }

    private static int? FindCarouselNavigationOffset(DependencyObject? source)
    {
        for (var current = source; current is not null; current = VisualTreeHelper.GetParent(current))
        {
            if (current is ContentControl { Tag: int offset } && offset != 0)
                return offset;
        }

        return null;
    }

    private void RetroCarousel_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (!IsRetroCarouselVisible || _retroCarouselItems.Count < 2) return;
        QueueRetroCarouselMove(e.Delta < 0 ? 1 : -1);
        e.Handled = true;
    }

    private void QueueRetroCarouselMove(int steps)
    {
        if (!IsRetroCarouselVisible || _retroCarouselItems.Count < 2 || steps == 0) return;

        // A fresh wheel/key/arrow gesture supersedes any multi-card click journey.
        _remainingRetroCarouselClickSteps = 0;

        _pendingRetroCarouselSteps = Math.Clamp(_pendingRetroCarouselSteps + steps, -12, 12);
        if (!_isRetroCarouselAnimating)
            StartNextRetroCarouselAnimation();
    }

    private void QueueRetroCarouselClickMove(int steps)
    {
        if (!IsRetroCarouselVisible || _retroCarouselItems.Count < 2 || steps == 0) return;

        _pendingRetroCarouselSteps = Math.Clamp(steps, -12, 12);
        _remainingRetroCarouselClickSteps = steps - _pendingRetroCarouselSteps;
        if (!_isRetroCarouselAnimating)
            StartNextRetroCarouselAnimation();
    }

    private bool IsRetroCarouselVisible =>
        IsRetroCarouselMode
        && FindNamed<UIElement>("CatalogPage")?.IsVisible == true
        && FindNamed<UIElement>("RetroCarouselHost")?.IsVisible == true;

    private void CancelRetroCarouselAnimation()
    {
        _pendingRetroCarouselSteps = 0;
        _remainingRetroCarouselClickSteps = 0;
        _isRetroCarouselAnimating = false;

        if (TryGetRetroCarouselControls(out var controls))
        {
            foreach (var control in controls)
                ClearRetroCarouselControlAnimations(control);
        }

        if (FindNamed<ContentControl>("RetroCarouselActionBar") is { } actionBar)
        {
            actionBar.BeginAnimation(OpacityProperty, null);
            actionBar.Opacity = 1;
            SetRetroCarouselActionInteractive(actionBar, _retroCarouselItems.Count > 0);
        }

        if (FindNamed<Panel>("RetroCarouselTrack") is { } track)
            track.IsHitTestVisible = true;
    }

    private void StartNextRetroCarouselAnimation()
    {
        var lastVisibleOffset = GetRetroCarouselLastVisibleOffset();
        if (!IsRetroCarouselVisible
            || _pendingRetroCarouselSteps == 0
            || _retroCarouselItems.Count < 2
            || _retroCarouselControlsByOffset.Count != GetExpectedRetroCarouselControlCount())
        {
            _isRetroCarouselAnimating = false;
            return;
        }

        _isRetroCarouselAnimating = true;
        var direction = Math.Sign(_pendingRetroCarouselSteps);
        _pendingRetroCarouselSteps -= direction;
        var incomingKey = direction > 0 ? 6 : -1;
        var incomingItemOffset = direction > 0 ? lastVisibleOffset + 1 : -1;

        // The only rebound surface is already fully outside the clipped viewport.
        var sparePair = _retroCarouselControlsByOffset.First(
            pair => pair.Key < 0 || pair.Key > lastVisibleOffset);
        _retroCarouselControlsByOffset.Remove(sparePair.Key);
        var spare = sparePair.Value;
        ClearRetroCarouselControlAnimations(spare);
        spare.Content = GetRetroCarouselItem(incomingItemOffset);
        spare.Tag = incomingKey;
        spare.Visibility = Visibility.Visible;
        ApplyRetroCarouselSlot(spare, GetRetroCarouselSlot(incomingKey));
        SetRetroCarouselControlInteractive(spare, false);
        _retroCarouselControlsByOffset[incomingKey] = spare;

        var motion = _retroCarouselControlsByOffset
            .Select(pair =>
            {
                int targetOffset;
                if (ReferenceEquals(pair.Value, spare))
                {
                    targetOffset = direction > 0 ? lastVisibleOffset : 0;
                }
                else if (direction > 0)
                {
                    targetOffset = pair.Key - 1;
                }
                else
                {
                    targetOffset = pair.Key == lastVisibleOffset ? 6 : pair.Key + 1;
                }

                return (Control: pair.Value, TargetOffset: targetOffset);
            })
            .ToArray();

        if (FindNamed<Panel>("RetroCarouselTrack") is { } track)
            track.IsHitTestVisible = false;

        if (FindNamed<ContentControl>("RetroCarouselActionBar") is { } actionBar)
        {
            actionBar.BeginAnimation(OpacityProperty, null);
            actionBar.Opacity = 1;
            SetRetroCarouselActionInteractive(actionBar, false);
        }

        var keepMoving = _pendingRetroCarouselSteps != 0
                         || _remainingRetroCarouselClickSteps != 0;
        foreach (var entry in motion)
        {
            AnimateRetroCarouselSurface(
                entry.Control,
                GetRetroCarouselSlot(entry.TargetOffset),
                keepMoving,
                ReferenceEquals(entry.Control, spare)
                    ? () => CompleteRetroCarouselAnimation(direction, motion)
                    : null);
        }
    }

    private void AnimateRetroCarouselSurface(
        ContentControl control,
        RetroCarouselSlot target,
        bool keepMoving,
        Action? completed)
    {
        const double nativeWidth = 250;
        var nativeHeight = UsesPortraitCarouselCovers ? 375 : 188;
        var duration = TimeSpan.FromMilliseconds(keepMoving ? 115 : 235);
        var easing = new QuinticEase { EasingMode = EasingMode.EaseOut };
        var (scale, translate) = GetRetroCarouselTransforms(control);
        Panel.SetZIndex(control, target.ZIndex);
        control.Opacity = 1;

        DoubleAnimation CreateAnimation(double from, double to)
        {
            var animation = new DoubleAnimation(from, to, duration)
            {
                FillBehavior = FillBehavior.HoldEnd,
                EasingFunction = easing
            };
            Timeline.SetDesiredFrameRate(animation, 120);
            return animation;
        }

        var horizontal = CreateAnimation(translate.X, target.Left);
        if (completed is not null)
            horizontal.Completed += (_, _) => completed();

        translate.BeginAnimation(TranslateTransform.XProperty, horizontal);
        translate.BeginAnimation(TranslateTransform.YProperty, CreateAnimation(translate.Y, target.Top));
        scale.BeginAnimation(
            ScaleTransform.ScaleXProperty,
            CreateAnimation(scale.ScaleX, target.Width / nativeWidth));
        scale.BeginAnimation(
            ScaleTransform.ScaleYProperty,
            CreateAnimation(scale.ScaleY, target.Height / nativeHeight));
    }

    private void CompleteRetroCarouselAnimation(
        int direction,
        IReadOnlyList<(ContentControl Control, int TargetOffset)> motion)
    {
        var lastVisibleOffset = GetRetroCarouselLastVisibleOffset();
        foreach (var entry in motion)
        {
            ApplyRetroCarouselSlot(entry.Control, GetRetroCarouselSlot(entry.TargetOffset));
            ClearRetroCarouselControlAnimations(entry.Control);
            entry.Control.Tag = entry.TargetOffset;
            SetRetroCarouselControlInteractive(
                entry.Control,
                entry.TargetOffset >= 0 && entry.TargetOffset <= lastVisibleOffset);
        }

        _retroCarouselControlsByOffset.Clear();
        foreach (var entry in motion)
            _retroCarouselControlsByOffset[entry.TargetOffset] = entry.Control;

        _retroCarouselIndex = WrapRetroCarouselIndex(_retroCarouselIndex + direction);
        UpdateRetroCarouselSelection();

        if (_pendingRetroCarouselSteps == 0 && _remainingRetroCarouselClickSteps != 0)
        {
            var refill = Math.Clamp(_remainingRetroCarouselClickSteps, -3, 3);
            _pendingRetroCarouselSteps = refill;
            _remainingRetroCarouselClickSteps -= refill;
        }

        var hasMoreMotion = _pendingRetroCarouselSteps != 0;

        if (FindNamed<ContentControl>("RetroCarouselActionBar") is { } actionBar)
        {
            actionBar.BeginAnimation(OpacityProperty, null);
            actionBar.Opacity = 1;
            SetRetroCarouselActionInteractive(actionBar, !hasMoreMotion);
        }

        if (FindNamed<Panel>("RetroCarouselTrack") is { } track)
            track.IsHitTestVisible = !hasMoreMotion;

        _isRetroCarouselAnimating = false;
        if (hasMoreMotion)
            StartNextRetroCarouselAnimation();
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
        _gameLibraryFolderPath = string.Empty;
        RememberApprovedRoot(selectedFolder);
        UpdateFolderLabels();
        var installSaved = LocalDataPaths.WriteInstallFolder(selectedFolder);
        var libraryCreated = EnsureGameLibraryFolder() is not null;
        SetCatalogStatus(installSaved && libraryCreated
            ? $"Pasta atualizada. Os jogos serão organizados automaticamente em {CatalogArchiveExtractor.GameLibraryFolderName}."
            : "Pasta atualizada nesta sessão, mas não foi possível salvar ou preparar a biblioteca.");
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

    private void StoreWindow_Loaded(object sender, RoutedEventArgs e)
    {
        Loaded -= StoreWindow_Loaded;
        if (_catalogRepository is null) return;

        try
        {
            var restoredCount = 0;
            var restoredItems = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var restoreRoots = new List<string>();
            if (IsExistingGameLibraryFolder(_gameLibraryFolderPath))
                restoreRoots.Add(Path.GetFullPath(_gameLibraryFolderPath));
            if (!restoreRoots.Contains(_installFolderPath, StringComparer.OrdinalIgnoreCase))
                restoreRoots.Add(_installFolderPath);

            var gameLibraryChoiceAttempted = false;
            string? restoreGameLibrary = null;
            foreach (var restoreRoot in restoreRoots)
            {
                foreach (var savedDownload in _downloadService.DiscoverResumableDownloads(restoreRoot))
                {
                    var item = _catalogRepository.FindById(savedDownload.ItemId);
                    if (item is null || !restoredItems.Add(item.Id)) continue;

                    _downloadRootsByItem[item.Id] = restoreRoot;
                    RememberApprovedRoot(restoreRoot);
                    EnsureDownloadJob(item);
                    restoredCount++;

                    if (IsGameItem(item))
                    {
                        if (!gameLibraryChoiceAttempted)
                        {
                            restoreGameLibrary = EnsureGameLibraryFolder();
                            gameLibraryChoiceAttempted = true;
                        }

                        if (restoreGameLibrary is not null)
                            _extractionRootsByItem[item.Id] = restoreGameLibrary;
                    }

                    if (savedDownload.ArchiveReady && File.Exists(savedDownload.ArchiveFilePath))
                    {
                        if (item.Extract)
                        {
                            item.MarkArchiveReady(savedDownload.ArchiveFilePath);
                            if (IsGameItem(item) && restoreGameLibrary is null)
                            {
                                item.AwaitExtractionLocation(
                                    $"Localize a pasta '{CatalogArchiveExtractor.GameLibraryFolderName}' para continuar.");
                                continue;
                            }

                            var extractionRoot = IsGameItem(item)
                                ? restoreGameLibrary!
                                : restoreRoot;
                            _ = ExtractArchiveAsync(
                                item,
                                savedDownload.ArchiveFilePath,
                                extractionRoot,
                                restoreRoot);
                        }
                        else
                        {
                            item.CompleteDownload(savedDownload.ArchiveFilePath);
                        }
                        continue;
                    }

                    var restorePaused = savedDownload.IsPaused
                                        || IsGameItem(item) && restoreGameLibrary is null;
                    item.RestoreDownload(
                        savedDownload.BytesReceived,
                        savedDownload.TotalBytes,
                        restorePaused);
                    if (!restorePaused)
                    {
                        _ = RunDownloadAsync(item, restoreRoot);
                    }
                }
            }

            if (restoredCount > 0)
                SetCatalogStatus($"{restoredCount} download(s) anterior(es) foram restaurados sem perder o progresso.");
        }
        catch (Exception exception) when (exception is IOException
                                           or UnauthorizedAccessException
                                           or InvalidDataException
                                           or System.Text.Json.JsonException
                                           or ArgumentException)
        {
            SetCatalogStatus($"Não foi possível restaurar os downloads anteriores: {exception.Message}");
        }
    }

    private async Task RunDownloadAsync(CatalogItem item, string installationRoot)
    {
        if (_downloadService.IsActive(item.Id))
        {
            SetCatalogStatus($"{item.Title}: o download ainda está ativo; aguarde a operação atual.");
            return;
        }

        var isGameItem = IsGameItem(item);
        string? gameLibraryRoot = null;
        if (isGameItem)
        {
            gameLibraryRoot = EnsureGameLibraryFolder();
            if (gameLibraryRoot is null)
            {
                SetCatalogStatus(
                    $"{item.Title}: selecione a pasta '{CatalogArchiveExtractor.GameLibraryFolderName}' para iniciar ou continuar.");
                return;
            }

            var hasRememberedDownloadRoot = _downloadRootsByItem.ContainsKey(item.Id);
            if (!item.Extract
                && (!hasRememberedDownloadRoot || !Directory.Exists(installationRoot)))
                installationRoot = gameLibraryRoot;
            _extractionRootsByItem[item.Id] = gameLibraryRoot;
        }

        EnsureDownloadJob(item);
        _downloadRootsByItem[item.Id] = installationRoot;
        RememberApprovedRoot(installationRoot);
        SetCatalogStatus(item.CanResume
            ? $"{item.Title}: continuando do ponto salvo..."
            : $"{item.Title}: iniciando download verificado...");

        try
        {
            var result = await _downloadService.DownloadAsync(item, installationRoot);
            SetCatalogStatus(result.Message);
            if (!result.Succeeded) return;

            if (isGameItem)
            {
                gameLibraryRoot = EnsureGameLibraryFolder();
                if (gameLibraryRoot is null)
                {
                    if (item.Extract)
                        item.AwaitExtractionLocation(
                            $"Localize a pasta '{CatalogArchiveExtractor.GameLibraryFolderName}' para concluir.");
                    SetCatalogStatus(
                        $"{item.Title}: download preservado; localize '{CatalogArchiveExtractor.GameLibraryFolderName}' para concluir.");
                    return;
                }
                _extractionRootsByItem[item.Id] = gameLibraryRoot;
            }

            if (isGameItem && !item.Extract)
            {
                var placedPath = await EnsureDownloadedGameIsInsideLibraryAsync(
                    item,
                    result.LocalFilePath,
                    gameLibraryRoot!);
                if (placedPath is not null)
                {
                    item.CompleteDownload(placedPath);
                    RememberApprovedRoot(gameLibraryRoot!);
                    SetCatalogStatus(
                        $"{item.Title}: download concluído em {CatalogArchiveExtractor.GameLibraryFolderName}.");
                }
                return;
            }

            if (!item.Extract) return;

            await ExtractArchiveAsync(
                item,
                result.LocalFilePath,
                isGameItem ? gameLibraryRoot! : installationRoot,
                installationRoot);
        }
        catch (Exception exception) when (exception is IOException
                                           or UnauthorizedAccessException
                                           or InvalidDataException
                                           or HttpRequestException
                                           or ArgumentException)
        {
            SetCatalogStatus($"{item.Title}: {exception.Message} O progresso salvo foi preservado.");
        }
    }

    private async Task ExtractArchiveAsync(
        CatalogItem item,
        string archivePath,
        string destinationBase,
        string downloadRoot,
        bool offerAnotherDrive = true)
    {
        await _extractionQueue.WaitAsync();
        try
        {
            await ExtractArchiveCoreAsync(
                item,
                archivePath,
                destinationBase,
                downloadRoot,
                offerAnotherDrive);
        }
        finally
        {
            _extractionQueue.Release();
        }
    }

    private async Task ExtractArchiveCoreAsync(
        CatalogItem item,
        string archivePath,
        string destinationBase,
        string downloadRoot,
        bool offerAnotherDrive)
    {
        if (string.IsNullOrWhiteSpace(archivePath) || !File.Exists(archivePath))
        {
            item.FailExtraction("O pacote compactado não foi encontrado. Baixe-o novamente.");
            SetCatalogStatus($"{item.Title}: o pacote compactado não foi encontrado.");
            return;
        }

        _extractionRootsByItem[item.Id] = destinationBase;
        item.BeginExtraction();
        SetCatalogStatus($"{item.Title}: descompactando automaticamente...");

        var isGameItem = IsGameItem(item);
        var category = string.IsNullOrWhiteSpace(item.Category) ? item.CategoryId : item.Category;
        var result = await Task.Run(() => _archiveExtractor.ExtractAsync(
            archivePath,
            destinationBase,
            category,
            item.Title,
            baseDirectoryIsGameLibrary: isGameItem));

        if (result.Succeeded)
        {
            var completedLibraryRoot = isGameItem
                ? destinationBase
                : Path.Combine(destinationBase, CatalogArchiveExtractor.LibraryFolderName);
            RememberApprovedRoot(completedLibraryRoot);
            if (!_downloadService.MarkExtractionCompleted(item, downloadRoot, archivePath))
            {
                item.FailExtraction(
                    "O conteúdo foi extraído, mas o estado final não pôde ser salvo. " +
                    "O pacote foi preservado; clique em Tentar extração para finalizar.");
                SetCatalogStatus($"{item.Title}: {item.DownloadStatus}");
                return;
            }

            var archiveCleanupMessage = string.Empty;
            try
            {
                File.Delete(archivePath);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                archiveCleanupMessage = $" A extração terminou, mas o pacote compactado não pôde ser apagado: {exception.Message}";
            }

            try
            {
                File.Delete(Path.Combine(
                    result.DestinationPath,
                    CatalogArchiveExtractor.CompletionMarkerFileName));
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
            }

            item.CompleteExtraction(result.DestinationPath);
            var libraryName = isGameItem
                ? CatalogArchiveExtractor.GameLibraryFolderName
                : CatalogArchiveExtractor.LibraryFolderName;
            SetCatalogStatus($"{item.Title}: extração concluída em {libraryName}.{archiveCleanupMessage}");
            return;
        }

        if (result.NeedsAnotherDrive)
        {
            item.AwaitExtractionLocation(result.Message);
            SetCatalogStatus($"{item.Title}: {result.Message}");
            if (!offerAnotherDrive) return;

            var chooseAnotherDrive = MessageBox.Show(
                this,
                isGameItem
                    ? $"Não há espaço suficiente neste disco.\n\nDeseja localizar outra pasta '{CatalogArchiveExtractor.GameLibraryFolderName}' em outro HD? O pacote baixado será preservado."
                    : "Não há espaço suficiente neste disco.\n\nDeseja escolher outro HD? O Turborama criará automaticamente uma pasta TruboRoms na unidade escolhida. O pacote baixado será preservado.",
                "Escolher outro HD",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning,
                MessageBoxResult.Yes);
            if (chooseAnotherDrive != MessageBoxResult.Yes) return;

            if (isGameItem)
            {
                var alternativeGameLibrary = ChooseAndPersistGameLibraryFolder(
                    destinationBase,
                    $"Selecione outra pasta chamada exatamente '{CatalogArchiveExtractor.GameLibraryFolderName}'");
                if (alternativeGameLibrary is null) return;

                _extractionRootsByItem[item.Id] = alternativeGameLibrary;
                await ExtractArchiveCoreAsync(
                    item,
                    archivePath,
                    alternativeGameLibrary,
                    downloadRoot,
                    offerAnotherDrive: false);
                return;
            }

            var selectedFolder = ChooseFolder(
                "Escolha outro HD ou uma pasta-base para criar TruboRoms",
                destinationBase);
            if (selectedFolder is null) return;

            var alternativeBase = NormalizeExtractionBase(selectedFolder);
            try
            {
                var libraryRoot = Path.Combine(alternativeBase, CatalogArchiveExtractor.LibraryFolderName);
                Directory.CreateDirectory(libraryRoot);
                RememberApprovedRoot(libraryRoot);
                _extractionRootsByItem[item.Id] = alternativeBase;
            }
            catch (Exception exception) when (exception is IOException
                                               or UnauthorizedAccessException
                                               or ArgumentException)
            {
                item.FailExtraction($"Não foi possível criar TruboRoms no local escolhido: {exception.Message}");
                SetCatalogStatus(item.DownloadStatus);
                return;
            }

            await ExtractArchiveCoreAsync(
                item,
                archivePath,
                alternativeBase,
                downloadRoot,
                offerAnotherDrive: false);
            return;
        }

        item.FailExtraction(result.Message);
        SetCatalogStatus($"{item.Title}: {result.Message}");
    }

    private void PauseDownload(CatalogItem item)
    {
        if (_downloadService.Pause(item.Id))
            SetCatalogStatus($"{item.Title}: pausando sem apagar o progresso...");
        else
            SetCatalogStatus($"{item.Title}: não foi possível registrar a pausa; o download continuará ativo.");
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

        if (item.CanPause)
        {
            PauseDownload(item);
            return;
        }

        if (item.CanRetryExtraction)
        {
            var downloadRoot = GetDownloadRoot(item);
            var extractionBase = IsGameItem(item)
                ? EnsureGameLibraryFolder()
                : _extractionRootsByItem.GetValueOrDefault(item.Id, downloadRoot);
            if (extractionBase is null)
            {
                SetCatalogStatus(
                    $"{item.Title}: localize a pasta '{CatalogArchiveExtractor.GameLibraryFolderName}' para tentar novamente.");
                return;
            }
            await ExtractArchiveAsync(item, item.ArchiveFilePath, extractionBase, downloadRoot);
            return;
        }

        if (item.IsBusy)
        {
            SetCatalogStatus($"{item.Title}: aguarde a operação atual terminar.");
            return;
        }

        if (string.IsNullOrWhiteSpace(item.DownloadUrl))
        {
            SetCatalogStatus($"{item.Title}: nenhum endereço de download foi configurado. Nada foi iniciado.");
            return;
        }

        await RunDownloadAsync(item, GetDownloadRoot(item));
    }

    private void PauseDownloadJob_Click(object sender, RoutedEventArgs e)
    {
        var job = ResolveDownloadJob(sender as FrameworkElement);
        if (job is null)
        {
            SetCatalogStatus("Não foi possível identificar o download para pausar.");
            return;
        }

        PauseDownload(job.Item);
    }

    private async void ResumeDownloadJob_Click(object sender, RoutedEventArgs e)
    {
        var job = ResolveDownloadJob(sender as FrameworkElement);
        if (job is null)
        {
            SetCatalogStatus("Não foi possível identificar o download para continuar.");
            return;
        }

        await RunDownloadAsync(job.Item, GetDownloadRoot(job.Item));
    }

    private async void RetryExtractionJob_Click(object sender, RoutedEventArgs e)
    {
        var job = ResolveDownloadJob(sender as FrameworkElement);
        if (job is null || string.IsNullOrWhiteSpace(job.Item.ArchiveFilePath))
        {
            SetCatalogStatus("O pacote compactado não foi encontrado para nova tentativa.");
            return;
        }

        var downloadRoot = GetDownloadRoot(job.Item);
        var extractionBase = IsGameItem(job.Item)
            ? EnsureGameLibraryFolder()
            : _extractionRootsByItem.GetValueOrDefault(job.ItemId, downloadRoot);
        if (extractionBase is null)
        {
            SetCatalogStatus(
                $"{job.Title}: localize a pasta '{CatalogArchiveExtractor.GameLibraryFolderName}' para tentar novamente.");
            return;
        }
        await ExtractArchiveAsync(job.Item, job.Item.ArchiveFilePath, extractionBase, downloadRoot);
    }

    private void DiscardDownloadJob_Click(object sender, RoutedEventArgs e)
    {
        var job = ResolveDownloadJob(sender as FrameworkElement);
        if (job is null)
        {
            SetCatalogStatus("Não foi possível identificar o pacote para apagar.");
            return;
        }

        if (job.CanPause)
        {
            SetCatalogStatus($"Pause {job.Title} antes de apagar o pacote.");
            return;
        }

        if (_downloadService.IsActive(job.ItemId))
        {
            SetCatalogStatus($"{job.Title}: aguarde a pausa terminar antes de apagar o progresso.");
            return;
        }

        var confirmation = MessageBox.Show(
            this,
            $"Deseja cancelar definitivamente o download de '{job.Title}' e apagar todo o progresso salvo?\n\nEsta é a única ação que apaga o pacote parcial.",
            "Apagar download e progresso",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);
        if (confirmation != MessageBoxResult.Yes) return;

        if (_downloadService.Discard(job.Item, GetDownloadRoot(job.Item)))
        {
            _downloadRootsByItem.Remove(job.ItemId);
            _extractionRootsByItem.Remove(job.ItemId);
            SetCatalogStatus($"{job.Title}: download e progresso apagados.");
        }
        else
            SetCatalogStatus($"{job.Title}: não havia um pacote salvo para apagar.");
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
            if (job.State is not (CatalogDownloadState.Completed or CatalogDownloadState.Discarded)) continue;
            DownloadJobs.RemoveAt(index);
            _downloadJobsByItem.Remove(job.ItemId);
            _downloadRootsByItem.Remove(job.ItemId);
            _extractionRootsByItem.Remove(job.ItemId);
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

    private string GetDownloadRoot(CatalogItem item)
    {
        if (_downloadRootsByItem.TryGetValue(item.Id, out var root)) return root;
        return _installFolderPath;
    }

    private static bool IsGameItem(CatalogItem item) =>
        !item.CategoryId.Equals("system-tools", StringComparison.OrdinalIgnoreCase);

    private static bool IsExistingGameLibraryFolder(string? folder)
    {
        return !string.IsNullOrWhiteSpace(folder)
               && CatalogArchiveExtractor.IsGameLibraryRoot(folder);
    }

    private string? EnsureGameLibraryFolder()
    {
        if (IsExistingGameLibraryFolder(_gameLibraryFolderPath))
            return Path.GetFullPath(_gameLibraryFolderPath);

        try
        {
            // A pasta escolhida em "Alterar pasta" é a única decisão necessária.
            // A estrutura padrão é criada automaticamente e nenhum seletor
            // interrompe o início ou a retomada de um download.
            var automaticLibrary = Path.GetFullPath(Path.Combine(
                _installFolderPath,
                CatalogArchiveExtractor.GameLibraryFolderName));
            Directory.CreateDirectory(automaticLibrary);
            return PersistGameLibraryFolder(automaticLibrary)
                ? automaticLibrary
                : null;
        }
        catch (Exception exception) when (exception is IOException
                                           or UnauthorizedAccessException
                                           or ArgumentException
                                           or NotSupportedException)
        {
            SetCatalogStatus(
                $"Não foi possível criar '{CatalogArchiveExtractor.GameLibraryFolderName}' " +
                $"dentro da pasta de instalação: {exception.Message}");
            return null;
        }
    }

    private string? ChooseAndPersistGameLibraryFolder(string initialFolder, string title)
    {
        var initialDirectory = GetExistingFolderPickerStart(initialFolder);
        while (true)
        {
            var selectedFolder = ChooseFolder(title, initialDirectory);
            if (selectedFolder is null) return null;

            var candidate = ResolveSelectedGameLibraryFolder(selectedFolder);
            if (candidate is null)
            {
                MessageBox.Show(
                    this,
                    $"Selecione a própria pasta chamada exatamente '{CatalogArchiveExtractor.GameLibraryFolderName}'.\n\nSe ela ainda não existir, crie-a pelo seletor de pastas e então a selecione.",
                    "Pasta mestre incorreta",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                initialDirectory = selectedFolder;
                continue;
            }

            return PersistGameLibraryFolder(candidate) ? candidate : null;
        }
    }

    private bool PersistGameLibraryFolder(string candidate)
    {
        if (!LocalDataPaths.WriteGameLibraryFolder(candidate))
        {
            MessageBox.Show(
                this,
                "Não foi possível salvar a localização da pasta mestre. Nenhum download foi iniciado.",
                "Não foi possível salvar",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            return false;
        }

        _gameLibraryFolderPath = Path.GetFullPath(candidate);
        RememberApprovedRoot(_gameLibraryFolderPath);
        return true;
    }

    private static string GetExistingFolderPickerStart(string? configuredFolder)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(configuredFolder))
            {
                var canonical = Path.GetFullPath(configuredFolder);
                if (Directory.Exists(canonical)) return canonical;
                var parent = Directory.GetParent(canonical)?.FullName;
                if (parent is not null && Directory.Exists(parent)) return parent;
            }
        }
        catch (Exception exception) when (exception is IOException
                                           or UnauthorizedAccessException
                                           or ArgumentException
                                           or NotSupportedException)
        {
        }

        return Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
    }

    private static string? ResolveSelectedGameLibraryFolder(string selectedFolder)
    {
        try
        {
            var canonical = Path.TrimEndingDirectorySeparator(Path.GetFullPath(selectedFolder));
            if (CatalogArchiveExtractor.IsGameLibraryRoot(canonical))
                return canonical;

            // Ao escolher uma unidade ou pasta-base alternativa, o Turborama
            // cria sozinho TruboRoms\roms; não é necessário criá-la no diálogo.
            var child = Path.GetFullPath(Path.Combine(
                canonical,
                CatalogArchiveExtractor.GameLibraryFolderName));
            Directory.CreateDirectory(child);
            return child;
        }
        catch (Exception exception) when (exception is IOException
                                           or UnauthorizedAccessException
                                           or ArgumentException
                                           or NotSupportedException)
        {
            return null;
        }
    }

    private async Task<string?> EnsureDownloadedGameIsInsideLibraryAsync(
        CatalogItem item,
        string downloadedPath,
        string gameLibraryRoot)
    {
        try
        {
            var sourcePath = Path.GetFullPath(downloadedPath);
            var canonicalLibrary = Path.TrimEndingDirectorySeparator(Path.GetFullPath(gameLibraryRoot));
            if (!IsExistingGameLibraryFolder(canonicalLibrary))
                throw new DirectoryNotFoundException(
                    $"A pasta '{CatalogArchiveExtractor.GameLibraryFolderName}' foi movida.");

            var destinationPath = _downloadService.BuildSafeDestinationPath(
                canonicalLibrary,
                item,
                new Uri(item.DownloadUrl, UriKind.Absolute));
            if (sourcePath.Equals(destinationPath, StringComparison.OrdinalIgnoreCase)
                && File.Exists(sourcePath))
                return sourcePath;
            if (!File.Exists(sourcePath))
            {
                if (File.Exists(destinationPath)) return destinationPath;
                throw new FileNotFoundException("O arquivo concluído não foi encontrado.", sourcePath);
            }

            EnsureGameLibraryDestinationSafe(canonicalLibrary, destinationPath);
            if (File.Exists(destinationPath))
                throw new IOException(
                    "Já existe um arquivo para este jogo na pasta mestre. O novo pacote foi preservado sem sobrescrever nada.");

            var destinationDirectory = Path.GetDirectoryName(destinationPath)
                                       ?? throw new InvalidDataException("O destino do jogo é inválido.");
            Directory.CreateDirectory(destinationDirectory);
            EnsureGameLibraryDestinationSafe(canonicalLibrary, destinationPath);

            await Task.Run(() => MoveFilePreservingSourceOnFailure(sourcePath, destinationPath));
            return destinationPath;
        }
        catch (Exception exception) when (exception is IOException
                                           or UnauthorizedAccessException
                                           or InvalidDataException
                                           or UriFormatException
                                           or ArgumentException
                                           or NotSupportedException)
        {
            SetCatalogStatus(
                $"{item.Title}: o download foi preservado em '{downloadedPath}', mas não pôde ser colocado em {CatalogArchiveExtractor.GameLibraryFolderName}: {exception.Message}");
            return null;
        }
    }

    private static void MoveFilePreservingSourceOnFailure(string sourcePath, string destinationPath)
    {
        var sameVolume = string.Equals(
            Path.GetPathRoot(sourcePath),
            Path.GetPathRoot(destinationPath),
            StringComparison.OrdinalIgnoreCase);
        if (sameVolume)
        {
            File.Move(sourcePath, destinationPath);
            return;
        }

        try
        {
            File.Copy(sourcePath, destinationPath, overwrite: false);
            if (new FileInfo(sourcePath).Length != new FileInfo(destinationPath).Length)
                throw new IOException("A cópia para a pasta mestre ficou incompleta.");
            File.Delete(sourcePath);
        }
        catch
        {
            try
            {
                if (File.Exists(destinationPath)) File.Delete(destinationPath);
            }
            catch (Exception cleanupException) when (cleanupException is IOException
                                                     or UnauthorizedAccessException)
            {
            }
            throw;
        }
    }

    private static void EnsureGameLibraryDestinationSafe(string libraryRoot, string destinationPath)
    {
        if (!IsWithinRoot(destinationPath, libraryRoot))
            throw new InvalidDataException("O destino do jogo saiu da pasta mestre autorizada.");

        var current = Path.GetFullPath(libraryRoot);
        var relative = Path.GetRelativePath(current, Path.GetFullPath(destinationPath));
        foreach (var segment in relative.Split(
                     [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                     StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            try
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException(
                        $"O destino contém um atalho ou junção não autorizado: {current}");
            }
            catch (FileNotFoundException)
            {
            }
            catch (DirectoryNotFoundException)
            {
            }
        }
    }

    private void RememberApprovedRoot(string root)
    {
        if (string.IsNullOrWhiteSpace(root)) return;
        _approvedOpenRoots.Add(Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)));
    }

    private static bool IsWithinRoot(string candidatePath, string rootPath)
    {
        var candidate = Path.GetFullPath(candidatePath);
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(rootPath));
        var rootPrefix = Path.EndsInDirectorySeparator(root)
            ? root
            : root + Path.DirectorySeparatorChar;
        return candidate.Equals(root, StringComparison.OrdinalIgnoreCase)
               || candidate.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeExtractionBase(string selectedFolder)
    {
        var canonical = Path.TrimEndingDirectorySeparator(Path.GetFullPath(selectedFolder));
        if (!Path.GetFileName(canonical).Equals(
                CatalogArchiveExtractor.LibraryFolderName,
                StringComparison.OrdinalIgnoreCase))
            return canonical;

        return Directory.GetParent(canonical)?.FullName ?? canonical;
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
        if (string.IsNullOrWhiteSpace(localFilePath)
            || (!File.Exists(localFilePath) && !Directory.Exists(localFilePath)))
        {
            SetCatalogStatus("O arquivo ou a pasta baixada não foi encontrado.");
            return;
        }

        var canonicalPath = Path.GetFullPath(localFilePath);
        if (!_approvedOpenRoots.Any(root => IsWithinRoot(canonicalPath, root)))
        {
            SetCatalogStatus("O arquivo está fora da pasta autorizada e não será aberto.");
            return;
        }

        var containingDirectory = Directory.Exists(canonicalPath)
            ? canonicalPath
            : Path.GetDirectoryName(canonicalPath);
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

        if (!isCatalog)
        {
            CancelRetroCarouselAnimation();
            StopRetroSystemVideo(clearFallback: true);
            StopRetroUniversalVideo();
        }

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

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);
        if (e.Handled
            || !IsRetroCarouselVisible
            || Keyboard.FocusedElement is TextBox)
            return;

        if (e.Key == Key.Left)
        {
            QueueRetroCarouselMove(-1);
            e.Handled = true;
        }
        else if (e.Key == Key.Right)
        {
            QueueRetroCarouselMove(1);
            e.Handled = true;
        }
    }

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

    private void StoreWindow_StateChanged(object? sender, EventArgs e)
    {
        if (WindowState == WindowState.Minimized)
            PauseRetroSystemVideoForWindow();
        else
            ResumeRetroSystemVideoForWindow();
    }

    protected override void OnClosed(EventArgs e)
    {
        StateChanged -= StoreWindow_StateChanged;
        StopRetroSystemVideo(clearFallback: true);
        StopRetroUniversalVideo();
        foreach (var job in DownloadJobs) job.Dispose();
        _downloadService.Dispose();
        base.OnClosed(e);
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
