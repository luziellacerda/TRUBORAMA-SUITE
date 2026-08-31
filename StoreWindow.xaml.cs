using System.Buffers;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;
using TurboBoxManager.Catalog;
using TurboBoxManager.Licensing;

namespace TurboBoxManager;

[SuppressMessage(
    "Design",
    "CA1001:Types that own disposable fields should be disposable",
    Justification = "The WPF lifetime cancels all work in OnClosed; its linked CTS intentionally remains valid while in-flight operations unwind, and an active extraction may still release its semaphore after closure.")]
public partial class StoreWindow : Window, INotifyPropertyChanged
{
    private const int CatalogPageSize = 4;
    private const string RetroGamesCategoryId = "retro-games";
    private const int MaximumRetroSystemVideoManifestBytes = 256 * 1024;
    private const int MaximumRetroPlatformDescriptionsBytes = 512 * 1024;
    private const int MaximumQuarantinedVideoPlaybacks = 8;
    private const uint NativeGenericRead = 0x80000000;
    private const uint NativeFileReadAttributes = 0x00000080;
    private const uint NativeFileShareRead = 0x00000001;
    private const uint NativeOpenExisting = 3;
    private const uint NativeFileFlagBackupSemantics = 0x02000000;
    private const uint NativeFileFlagOpenReparsePoint = 0x00200000;
    private const uint NativeFileFlagSequentialScan = 0x08000000;
    private const int NativeFileAttributeTagInfoClass = 9;
    private const int NativeWmDpiChanged = 0x02E0;
    private const uint NativeMonitorDefaultToNearest = 0x00000002;
    private const uint NativeSwpNoZOrder = 0x0004;
    private const uint NativeSwpNoActivate = 0x0010;
    private static readonly Lazy<IReadOnlyDictionary<string, string>> RetroSystemVideoMap =
        new(LoadRetroSystemVideoMap);
    private static readonly Lazy<IReadOnlyDictionary<string, RetroSystemVideoIntegrity>> RetroSystemVideoIntegrityMap =
        new(LoadRetroSystemVideoIntegrityMap);
    private static readonly Lazy<IReadOnlyDictionary<string, RetroSystemVideoIntegrity>> BackgroundVideoIntegrityMap =
        new(LoadBackgroundVideoIntegrityMap);
    private static readonly Lazy<IReadOnlyDictionary<string, string>> RetroPlatformDescriptions =
        new(LoadRetroPlatformDescriptions);
    private static readonly HashSet<string> SupportedMusicExtensions = new(
        [".mp3", ".wav", ".wma", ".m4a", ".aac"],
        StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, Color> EmulatorCoverAccentMap =
        new Dictionary<string, Color>(StringComparer.OrdinalIgnoreCase)
        {
            ["f95f1d8257dc3d0ec31c199c8d30ba0e"] = Color.FromRgb(255, 51, 71),
            ["c430156afd3ca0b0f10579218c7aedc1"] = Color.FromRgb(56, 189, 248),
            ["d2326fb5c3bf0d258dc869a20f883612"] = Color.FromRgb(56, 189, 248),
            ["303aed3c0caf2e9e02d3f229cfeb18b6"] = Color.FromRgb(56, 189, 248),
            ["d43efb43df478b8e78759de456be6177"] = Color.FromRgb(192, 83, 255),
            ["cb4b3fa79fd5c10714e2a92e6777da08"] = Color.FromRgb(255, 51, 71),
            ["7468dcff66e5c9f90202455b19b629c6"] = Color.FromRgb(139, 92, 246),
            ["2c6872aec304fb6faa038b8cc65dee58"] = Color.FromRgb(239, 68, 68),
            ["83864e2a73f63bc39dbfbd84b20f0795"] = Color.FromRgb(168, 85, 247),
            ["f9f47bfde74241481308ebdfda080778"] = Color.FromRgb(34, 211, 238),
            ["f754c3e71638008f72dce88d1c5b9590"] = Color.FromRgb(14, 165, 233),
            ["492a56ae14eb149854ccdd23b6617f08"] = Color.FromRgb(132, 204, 22),
            ["aab30de07d8cf800b283271fd0365aca"] = Color.FromRgb(163, 230, 53),
            ["4c2d5717ad93c2b6277453f7ae39aa13"] = Color.FromRgb(56, 189, 248),
            ["40ff54b598e3f23a31cea45c856397e8"] = Color.FromRgb(14, 165, 233),
            ["2dccd65b139159b74c7327df00fcfda9"] = Color.FromRgb(34, 197, 94),
            ["962f9229c145b1e91e079f8c2601f0af"] = Color.FromRgb(34, 197, 94)
        };
    private static readonly object FailedVideoCloseQuarantineGate = new();
    private static readonly List<(MediaElement Player, TrustedVideoLease Lease)>
        FailedVideoCloseQuarantine = [];
    private static int _videoPlaybackDisabled;
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

    private readonly CatalogDownloadService _downloadService;
    private readonly CatalogLocalLibraryService _localLibraryService;
    private readonly AuthorizedStoreContext _authorization;
    private readonly SuiteLicensingRuntime _licensingRuntime;
    private readonly SuiteAuthorizationSubscription _authorizationSubscription;
    private readonly CancellationTokenSource _storeOperationCancellation;
    private int _revocationHandled;
    private int _storeReady;
    private readonly Dictionary<string, CatalogDownloadJob> _downloadJobsByItem =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _downloadRootsByItem =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _pendingLegacyDownloadRootsByItem =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _extractionRootsByItem =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _approvedOpenRoots = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, CatalogLocalGameInspection> _localGameInspectionCache =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _managedGameDeletionInProgress =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly List<CatalogItem> _managedGameSystemItems = [];
    private readonly List<CatalogLocalOrphanInspection> _managedGameSystemOrphans = [];
    private readonly MediaPlayer _musicPlayer = new();
    private readonly List<string> _musicTracks = [];
    private EmbeddedMusicTrackLease? _activeEmbeddedMusicTrackLease;
    private readonly CatalogArchiveExtractor _archiveExtractor = new();
    private readonly SemaphoreSlim _extractionQueue = new(1, 1);
    private CatalogRepository? _catalogRepository;
    private CatalogCategory? _selectedCategory;
    private int _currentCatalogPage = 1;
    private string _catalogSearchText = string.Empty;
    private string _installFolderPath = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
    private string _gameLibraryFolderPath = string.Empty;
    private string _temporaryFolderPath = string.Empty;
    private string _musicFolderPath = string.Empty;
    private string _managedGameSearchText = string.Empty;
    private LibrarySystemSummary? _selectedManagedGameSystem;
    private CancellationTokenSource? _localLibraryScanCancellation;
    private int _localLibraryScanVersion;
    private CancellationTokenSource? _musicPlaylistLoadCancellation;
    private int _musicPlaylistLoadVersion;
    private int _musicTrackIndex = -1;
    private string _openedMusicTrackPath = string.Empty;
    private bool _isMusicPlaying;
    private bool _isBuiltInMusicPlaylist;
    private int _consecutiveMusicFailures;
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
    private int _retroUniversalVideoRequestVersion;
    private int _activeRetroSystemVideoGeneration;
    private string _activeRetroSystemVideoItemId = string.Empty;
    private string _activeRetroSystemVideoPath = string.Empty;
    private string _pendingRetroSystemVideoItemId = string.Empty;
    private string _activeRetroUniversalVideoCategoryId = string.Empty;
    private string _pendingRetroUniversalVideoCategoryId = string.Empty;
    private MediaElement? _retroSystemVideoPlayer;
    private MediaElement? _retroUniversalVideoPlayer;
    private TrustedVideoLease? _retroSystemVideoLease;
    private TrustedVideoLease? _retroUniversalVideoLease;
    private CancellationTokenSource? _retroSystemVideoLoadCancellation;
    private CancellationTokenSource? _retroUniversalVideoLoadCancellation;
    private Task _retroSystemVideoLoadTask = Task.CompletedTask;
    private Task _retroUniversalVideoLoadTask = Task.CompletedTask;
    private bool _retroSystemVideoPausedForWindow;
    private bool _retroSystemVideoRestartOnResume;
    private HwndSource? _windowSource;
    private IntPtr _lastWorkAreaMonitor;
    private bool _workAreaClampScheduled;
    private bool _workAreaClampInProgress;

    private readonly record struct RetroCarouselSlot(
        double Left,
        double Top,
        double Width,
        double Height,
        double Opacity,
        int ZIndex);

    private sealed record DirectGamePlacement(
        string LocalFilePath,
        bool SourceStateCleanupPending);

    private readonly record struct RetroSystemVideoIntegrity(string Sha256, long Length);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeFileAttributeTagInfo
    {
        public uint FileAttributes;
        public uint ReparseTag;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeMonitorInfo
    {
        public uint Size;
        public NativeRect Monitor;
        public NativeRect Work;
        public uint Flags;
    }

    private sealed class TrustedVideoLeaseResources(
        FileStream stream,
        List<SafeFileHandle> directoryHandles) : IDisposable
    {
        public void Dispose()
        {
            try
            {
                stream.Dispose();
            }
            finally
            {
                for (var index = directoryHandles.Count - 1; index >= 0; index--)
                    directoryHandles[index].Dispose();
            }
        }
    }

    private sealed class TrustedVideoLease(
        string path,
        FileStream stream,
        List<SafeFileHandle> directoryHandles) : IDisposable
    {
        private TrustedVideoLeaseResources? _resources = new(stream, directoryHandles);

        public string Path { get; } = path;

        public bool IsActive => Volatile.Read(ref _resources) is not null;

        public void Dispose() => Interlocked.Exchange(ref _resources, null)?.Dispose();
    }

#pragma warning disable SYSLIB1054
    [DllImport(
        "kernel32.dll",
        EntryPoint = "CreateFileW",
        CharSet = CharSet.Unicode,
        ExactSpelling = true,
        SetLastError = true)]
    private static extern SafeFileHandle OpenNativeVideoPathHandle(
        string fileName,
        uint desiredAccess,
        uint shareMode,
        IntPtr securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        IntPtr templateFile);

    [DllImport(
        "kernel32.dll",
        EntryPoint = "GetFileInformationByHandleEx",
        ExactSpelling = true,
        SetLastError = true)]
    private static extern int GetNativeVideoFileInformation(
        SafeFileHandle file,
        int fileInformationClass,
        out NativeFileAttributeTagInfo fileInformation,
        uint bufferSize);

    [DllImport(
        "kernel32.dll",
        EntryPoint = "GetFinalPathNameByHandleW",
        ExactSpelling = true,
        SetLastError = true)]
    private static extern uint GetFinalNativeVideoPath(
        SafeFileHandle file,
        IntPtr pathBuffer,
        uint pathBufferLength,
        uint flags);

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern IntPtr MonitorFromWindow(IntPtr windowHandle, uint flags);

    [DllImport(
        "user32.dll",
        EntryPoint = "GetMonitorInfoW",
        ExactSpelling = true,
        SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNativeMonitorInfo(
        IntPtr monitorHandle,
        ref NativeMonitorInfo monitorInfo);

    [DllImport("user32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr windowHandle, out NativeRect windowRect);

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern uint GetDpiForWindow(IntPtr windowHandle);

    [DllImport("user32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(
        IntPtr windowHandle,
        IntPtr insertAfter,
        int x,
        int y,
        int width,
        int height,
        uint flags);
#pragma warning restore SYSLIB1054

    public ObservableCollection<CatalogCategory> CatalogCategories { get; } = [];
    public ObservableCollection<CatalogCategory> FeaturedCategories { get; } = [];
    public ObservableCollection<CatalogItem> CatalogItems { get; } = [];
    public ObservableCollection<LibrarySystemSummary> LibrarySystems { get; } = [];
    public ObservableCollection<LibrarySystemSummary> ManagedGameSystems { get; } = [];
    public ObservableCollection<CatalogLocalGameEntry> ManagedGames { get; } = [];
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

    public LibrarySystemSummary? SelectedManagedGameSystem
    {
        get => _selectedManagedGameSystem;
        set
        {
            if (ReferenceEquals(_selectedManagedGameSystem, value)) return;
            _selectedManagedGameSystem = value;
            OnPropertyChanged();
            BeginManagedGameSystemRefresh();
        }
    }

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

    internal StoreWindow(
        AuthorizedStoreContext authorization,
        SuiteLicensingRuntime licensingRuntime)
        : this(authorization, licensingRuntime, SuiteAuthorizedCatalog.Empty)
    {
    }

    internal StoreWindow(
        AuthorizedStoreContext authorization,
        SuiteLicensingRuntime licensingRuntime,
        SuiteAuthorizedCatalog authorizedCatalog)
    {
        _authorization = authorization ?? throw new ArgumentNullException(nameof(authorization));
        _licensingRuntime = licensingRuntime
                            ?? throw new ArgumentNullException(nameof(licensingRuntime));
        ArgumentNullException.ThrowIfNull(authorizedCatalog);
        _downloadService = _licensingRuntime.CreateCatalogDownloadService(
            _authorization,
            authorizedCatalog,
            new CatalogDownloadOptions
            {
                MaximumFileSizeBytes = 512L * 1024L * 1024L * 1024L
            });
        _localLibraryService = new CatalogLocalLibraryService(_downloadService);
        _authorizationSubscription = _licensingRuntime.AttachAuthorizationConsumer(
            _authorization,
            LicensingRuntime_AuthorizationRevoked);
        _storeOperationCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            _authorizationSubscription.AuthorizationCancellationToken);

        try
        {
            ThrowIfOperationUnauthorized();
            InitializeComponent();
            DataContext = this;
            LicenseConsumerText.Text = FormatMaskedLicenseId(_authorization.LicenseId);
            LicenseConsumerStatusText.Text = "Licença ativa";
            _installFolderPath = LocalDataPaths.ReadInstallFolder() ?? _installFolderPath;
            _gameLibraryFolderPath = LocalDataPaths.ReadGameLibraryFolder() ?? string.Empty;
            _musicFolderPath = LocalDataPaths.ReadMusicFolder() ?? string.Empty;
            _musicPlayer.Volume = .35;
            _musicPlayer.MediaOpened += MusicPlayer_MediaOpened;
            _musicPlayer.MediaEnded += MusicPlayer_MediaEnded;
            _musicPlayer.MediaFailed += MusicPlayer_MediaFailed;
            RememberApprovedRoot(_installFolderPath);
            if (IsExistingGameLibraryFolder(_gameLibraryFolderPath))
                RememberApprovedRoot(_gameLibraryFolderPath);
            InitializeCatalog(authorizedCatalog);
            ThrowIfOperationUnauthorized();
            UpdateFolderLabels();
            Loaded += StoreWindow_Loaded;
            StateChanged += StoreWindow_StateChanged;
            SessionStatusText.Text = "SESSÃO ATIVA";
            SessionStatusText.Foreground = Brushes.LawnGreen;
            SessionStatusBadge.Background = new SolidColorBrush(Color.FromRgb(19, 32, 14));
            SessionStatusBadge.BorderBrush = new SolidColorBrush(Color.FromRgb(49, 72, 42));
            ThrowIfOperationUnauthorized();
            Volatile.Write(ref _storeReady, 1);
            ThrowIfOperationUnauthorized();
            ShowPage("Home");
        }
        catch
        {
            Volatile.Write(ref _storeReady, 0);
            CancelStoreOperations();
            _storeOperationCancellation.Dispose();
            _authorizationSubscription.Dispose();
            throw;
        }
    }

    private void InitializeCatalog(SuiteAuthorizedCatalog authorizedCatalog)
    {
        try
        {
            var catalogDirectory = Path.Combine(AppContext.BaseDirectory, "Assets", "Catalog");
            var publicManifestPath = Path.Combine(catalogDirectory, "catalog.json");
            _catalogRepository = CatalogRepository.Load(
                publicManifestPath,
                authorizedCatalog.Descriptors,
                authorizedCatalog.MaintenanceItems,
                authorizedCatalog.RequiresCompleteCoverage);

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
            ManagedGameSystems.Clear();
            ManagedGames.Clear();
            LibraryTotalItemCount = 0;
            CatalogItems.Clear();
            PageNumbers.Clear();
            HasCatalogItems = false;
            SetCatalogStatus($"Catálogo indisponível: {exception.Message}");
        }
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
        ManagedGameSystems.Clear();
        LibraryTotalItemCount = 0;
        if (_catalogRepository is null) return;

        foreach (var category in CatalogCategories)
        {
            var result = _catalogRepository.Query(
                category.Id,
                searchText: null,
                requestedPage: 1,
                pageSize: Math.Max(1, _catalogRepository.ItemCount));
            var cover = result.Items.Count > 0
                ? result.Items[0].ImageSource
                : string.Empty;
            var summary = new LibrarySystemSummary
            {
                Category = category,
                CoverImageSource = cover,
                ItemCount = result.TotalItems
            };
            LibrarySystems.Add(summary);
            if (!category.Id.Equals("system-tools", StringComparison.OrdinalIgnoreCase))
                ManagedGameSystems.Add(summary);
            LibraryTotalItemCount += result.TotalItems;
        }
    }

    private void BeginManagedGameSystemRefresh()
    {
        if (_catalogRepository is null || SelectedManagedGameSystem is not { } selectedSystem)
            return;

        CancelManagedGameScan();
        _managedGameSystemItems.Clear();
        _managedGameSystemOrphans.Clear();
        _localGameInspectionCache.Clear();
        var result = _catalogRepository.Query(
            selectedSystem.CategoryId,
            searchText: null,
            requestedPage: 1,
            pageSize: Math.Max(1, _catalogRepository.ItemCount));
        var systemItems = result.Items.Where(IsGameItem).ToArray();
        _managedGameSystemItems.AddRange(systemItems);
        RenderManagedGames();
        _ = InspectManagedGameSystemAsync(selectedSystem, systemItems);
    }

    private void ManagedGameSearch_TextChanged(object sender, TextChangedEventArgs e)
    {
        _managedGameSearchText = sender is TextBox textBox ? textBox.Text.Trim() : string.Empty;
        RenderManagedGames();
    }

    private void RefreshManagedGames_Click(object sender, RoutedEventArgs e) =>
        BeginManagedGameSystemRefresh();

    private void RenderManagedGames()
    {
        var localItems = _managedGameSystemItems.Where(item =>
            _localGameInspectionCache.TryGetValue(item.Id, out var inspection)
            && IsVisibleLocalGame(inspection));
        var filteredItems = string.IsNullOrWhiteSpace(_managedGameSearchText)
            ? localItems
            : localItems.Where(item =>
                    item.Title.Contains(_managedGameSearchText, StringComparison.CurrentCultureIgnoreCase)
                    || item.Subtitle.Contains(_managedGameSearchText, StringComparison.CurrentCultureIgnoreCase)
                    || item.Keywords.Contains(_managedGameSearchText, StringComparison.CurrentCultureIgnoreCase));

        ManagedGames.Clear();
        foreach (var item in filteredItems)
        {
            var entry = new CatalogLocalGameEntry(item);
            if (_localGameInspectionCache.TryGetValue(item.Id, out var inspection))
                entry.Apply(inspection);
            ManagedGames.Add(entry);
        }

        var filteredOrphans = string.IsNullOrWhiteSpace(_managedGameSearchText)
            ? _managedGameSystemOrphans
            : _managedGameSystemOrphans.Where(orphan =>
                    orphan.Name.Contains(
                        _managedGameSearchText,
                        StringComparison.CurrentCultureIgnoreCase)
                    || orphan.LocalPath.Contains(
                        _managedGameSearchText,
                        StringComparison.CurrentCultureIgnoreCase))
                .ToList();
        var categoryId = SelectedManagedGameSystem?.CategoryId ?? string.Empty;
        foreach (var orphan in filteredOrphans)
            if (IsPhysicalLocalPath(orphan.LocalPath))
                ManagedGames.Add(new CatalogLocalGameEntry(categoryId, orphan));

        UpdateManagedGameSummary();
    }

    private async Task InspectManagedGameSystemAsync(
        LibrarySystemSummary selectedSystem,
        CatalogItem[] systemItems)
    {
        var gameLibraryRoot = EnsureGameLibraryFolder();
        if (gameLibraryRoot is null)
        {
            foreach (var item in systemItems)
            {
                _localGameInspectionCache[item.Id] = new CatalogLocalGameInspection(
                    CatalogLocalGameStatus.Unavailable,
                    string.Empty,
                    $"Selecione a pasta '{CatalogArchiveExtractor.GameLibraryFolderName}' para analisar este jogo.");
            }
            _managedGameSystemOrphans.Clear();
            RenderManagedGames();
            SetManagedGameStatus("Selecione a pasta de ROMs para analisar os jogos locais.");
            return;
        }

        var version = ++_localLibraryScanVersion;
        var scanCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            _storeOperationCancellation.Token);
        _localLibraryScanCancellation = scanCancellation;
        SetManagedGameStatus(
            $"Analisando {selectedSystem.DisplayName} em {CatalogArchiveExtractor.GameLibraryFolderName}...");

        try
        {
            var inspection = await _localLibraryService.InspectSystemAsync(
                gameLibraryRoot,
                selectedSystem.DisplayName,
                systemItems,
                scanCancellation.Token);
            if (scanCancellation.IsCancellationRequested
                || version != _localLibraryScanVersion)
                return;

            for (var index = 0; index < systemItems.Length; index++)
                _localGameInspectionCache[systemItems[index].Id] = inspection.CatalogItems[index];
            _managedGameSystemOrphans.Clear();
            _managedGameSystemOrphans.AddRange(inspection.Orphans);
            RenderManagedGames();
            SetManagedGameStatus(
                $"Análise concluída: {GetManagedDownloadedCount()} instalado(s), " +
                $"{GetManagedIncompleteCount()} incompleto(s), " +
                $"{_managedGameSystemOrphans.Count} não reconhecido(s). " +
                inspection.CategoryDetail);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception) when (exception is IOException
                                           or UnauthorizedAccessException
                                           or InvalidDataException
                                           or ArgumentException
                                           or NotSupportedException)
        {
            SetManagedGameStatus($"Não foi possível analisar a pasta local: {exception.Message}");
        }
        finally
        {
            if (ReferenceEquals(_localLibraryScanCancellation, scanCancellation))
                _localLibraryScanCancellation = null;
            scanCancellation.Dispose();
        }
    }

    private async void DeleteManagedGame_Click(object sender, RoutedEventArgs e)
    {
        if (!TryEnsureAuthorized("excluir o jogo local")) return;
        var entry = (sender as FrameworkElement)?.DataContext as CatalogLocalGameEntry;
        if (entry is null || !entry.CanDelete)
        {
            SetManagedGameStatus("O jogo selecionado não possui conteúdo local removível.");
            return;
        }
        if (entry.Item is { } activeCatalogItem
            && (_downloadService.IsActive(entry.ItemId) || activeCatalogItem.IsBusy))
        {
            SetManagedGameStatus($"{entry.Title}: aguarde o download ou a extração terminar.");
            return;
        }
        if (!IsExistingGameLibraryFolder(_gameLibraryFolderPath))
        {
            SetManagedGameStatus("A pasta de ROMs configurada não está disponível.");
            return;
        }

        var deletionExplanation = entry.IsOrphan
            ? "Este item não é reconhecido pelo catálogo e será removido da pasta do sistema."
            : "O jogo sairá de Jogos locais e continuará disponível na Biblioteca para ser instalado novamente.";
        var localPath = string.IsNullOrWhiteSpace(entry.LocalPath)
            ? "(caminho local indisponível)"
            : entry.LocalPath;
        var confirmation = MessageBox.Show(
            this,
            $"Excluir permanentemente os arquivos locais de:\n\n{entry.Title}\n\n" +
            $"Caminho local exato:\n{localPath}\n\n" +
            deletionExplanation,
            "Excluir jogo local",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);
        if (confirmation != MessageBoxResult.Yes) return;
        if (!_managedGameDeletionInProgress.Add(entry.ItemId))
        {
            SetManagedGameStatus($"{entry.Title}: a exclusão já está em andamento.");
            return;
        }

        try
        {
            CancelManagedGameScan();
            var gameLibraryRoot = Path.GetFullPath(_gameLibraryFolderPath);
            SetManagedGameStatus($"Excluindo {entry.Title} com validação de caminho...");
            if (entry.Orphan is { } orphan)
            {
                var categoryName = SelectedManagedGameSystem?.DisplayName
                                   ?? throw new InvalidOperationException(
                                       "Nenhum sistema está selecionado para a exclusão.");
                var currentItems = _managedGameSystemItems.ToArray();
                var deleted = await CatalogLocalLibraryService.DeleteOrphanAsync(
                    gameLibraryRoot,
                    categoryName,
                    orphan,
                    currentItems,
                    _storeOperationCancellation.Token);
                ThrowIfOperationUnauthorized();
                SetManagedGameStatus(deleted
                    ? $"{entry.Title}: item não reconhecido excluído com segurança."
                    : $"{entry.Title}: o item já não existia na pasta do sistema.");
            }
            else
            {
                var catalogItemToDelete = entry.Item
                                          ?? throw new InvalidOperationException(
                                              "O item do catálogo não está mais disponível.");
                var downloadRoot = GetDownloadRoot(catalogItemToDelete);
                var deleted = await _localLibraryService.DeleteAsync(
                    gameLibraryRoot,
                    catalogItemToDelete,
                    _storeOperationCancellation.Token);
                ThrowIfOperationUnauthorized();
                var packageStateRemoved = true;
                var cleanupRoots = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                {
                    Path.GetFullPath(gameLibraryRoot),
                    Path.GetFullPath(downloadRoot),
                    Path.GetFullPath(_installFolderPath)
                };
                if (_pendingLegacyDownloadRootsByItem.TryGetValue(
                        entry.ItemId,
                        out var pendingLegacyRoot))
                    cleanupRoots.Add(Path.GetFullPath(pendingLegacyRoot));
                if (catalogItemToDelete.HasAuthorizedArtifact)
                    foreach (var cleanupRoot in cleanupRoots)
                        packageStateRemoved = _downloadService.Discard(
                                                  catalogItemToDelete,
                                                  cleanupRoot)
                                              && packageStateRemoved;

                _extractionRootsByItem.Remove(entry.ItemId);
                if (packageStateRemoved)
                {
                    _downloadRootsByItem.Remove(entry.ItemId);
                    _pendingLegacyDownloadRootsByItem.Remove(entry.ItemId);
                }
                NotifyDownloadCollectionChanged();
                if (!packageStateRemoved)
                {
                    SetManagedGameStatus(deleted
                        ? $"{entry.Title}: instalação excluída; o pacote ou progresso permaneceu para uma nova tentativa de limpeza."
                        : $"{entry.Title}: instalação já ausente; o pacote ou progresso não pôde ser limpo agora.");
                }
                else
                {
                    SetManagedGameStatus(deleted
                        ? $"{entry.Title}: jogo, pacote e progresso locais excluídos."
                        : $"{entry.Title}: pacote e progresso removidos; a instalação já não existia.");
                }
            }
        }
        catch (OperationCanceledException)
        {
            SetManagedGameStatus("A exclusão foi cancelada porque a sessão terminou.");
        }
        catch (SuiteAuthorizationException)
        {
            SetManagedGameStatus("A exclusão foi interrompida porque a sessão terminou.");
        }
        catch (Exception exception) when (exception is IOException
                                           or UnauthorizedAccessException
                                           or InvalidDataException
                                           or ArgumentException
                                           or NotSupportedException)
        {
            SetManagedGameStatus($"A exclusão segura foi interrompida: {exception.Message}");
        }
        finally
        {
            _managedGameDeletionInProgress.Remove(entry.ItemId);
            if (Volatile.Read(ref _storeReady) != 0)
                BeginManagedGameSystemRefresh();
        }
    }

    private void ChooseManagedGameFolder_Click(object sender, RoutedEventArgs e)
    {
        if (!TryEnsureAuthorized("alterar a pasta de ROMs")) return;
        if (_managedGameDeletionInProgress.Count > 0)
        {
            SetManagedGameStatus("Aguarde a exclusão local terminar antes de trocar a pasta.");
            return;
        }
        var initialFolder = IsExistingGameLibraryFolder(_gameLibraryFolderPath)
            ? _gameLibraryFolderPath
            : _installFolderPath;
        var selected = ChooseAndPersistGameLibraryFolder(
            initialFolder,
            $"Selecione a pasta '{CatalogArchiveExtractor.GameLibraryFolderName}'");
        if (selected is null) return;
        _gameLibraryFolderPath = selected;
        BeginManagedGameSystemRefresh();
    }

    private void OpenManagedGameFolder_Click(object sender, RoutedEventArgs e)
    {
        if (!TryEnsureAuthorized("abrir a pasta de ROMs")) return;
        if (!IsExistingGameLibraryFolder(_gameLibraryFolderPath))
        {
            SetManagedGameStatus("A pasta de ROMs configurada não está disponível.");
            return;
        }

        try
        {
            Process.Start(CreateExplorerStartInfo(_gameLibraryFolderPath));
            SetManagedGameStatus("Pasta de ROMs aberta.");
        }
        catch (Exception exception) when (exception is InvalidOperationException or Win32Exception)
        {
            SetManagedGameStatus($"Não foi possível abrir a pasta: {exception.Message}");
        }
    }

    private void UpdateManagedGameSummary()
    {
        SetText("ManagedGameSystemTitle", SelectedManagedGameSystem?.DisplayName ?? "Jogos locais");
        SetText(
            "ManagedGameDownloadedCount",
            GetManagedDownloadedCount().ToString(System.Globalization.CultureInfo.InvariantCulture));
        SetText(
            "ManagedGameVisibleCount",
            ManagedGames.Count.ToString(System.Globalization.CultureInfo.InvariantCulture));
        SetVisibility("ManagedGamesEmptyPanel", ManagedGames.Count == 0);
        if (FindNamed<ListBox>("ManagedGamesList") is { } list)
            list.Visibility = ManagedGames.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    private int GetManagedDownloadedCount() => _localGameInspectionCache.Values.Count(inspection =>
        inspection.Status == CatalogLocalGameStatus.Downloaded);

    private int GetManagedIncompleteCount() => _localGameInspectionCache.Values.Count(inspection =>
        inspection.Status == CatalogLocalGameStatus.Incomplete);

    internal static bool IsVisibleLocalGame(CatalogLocalGameInspection inspection) =>
        (inspection.Status is CatalogLocalGameStatus.Downloaded
            or CatalogLocalGameStatus.Incomplete
            or CatalogLocalGameStatus.Unsafe)
        && IsPhysicalLocalPath(inspection.ExpectedPath);

    private static bool IsPhysicalLocalPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        try
        {
            _ = File.GetAttributes(path);
            return true;
        }
        catch (FileNotFoundException)
        {
            return false;
        }
        catch (DirectoryNotFoundException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return true;
        }
        catch (IOException)
        {
            return true;
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException)
        {
            return false;
        }
    }

    private void SetManagedGameStatus(string message) =>
        SetText("ManagedGameManagerStatus", message);

    private void CancelManagedGameScan()
    {
        ++_localLibraryScanVersion;
        var cancellation = _localLibraryScanCancellation;
        _localLibraryScanCancellation = null;
        if (cancellation is null) return;
        try { cancellation.Cancel(); }
        catch (ObjectDisposedException) { }
    }

    private void RefreshManagedGamesIfVisible(string categoryId)
    {
        if (FindNamed<UIElement>("GameManagerPage")?.IsVisible == true
            && SelectedManagedGameSystem?.CategoryId.Equals(
                categoryId,
                StringComparison.OrdinalIgnoreCase) == true)
            BeginManagedGameSystemRefresh();
    }

    private async Task InitializeMusicPlayerAsync()
    {
        await LoadBuiltInMusicPlaylistAsync();
    }

    private async Task LoadBuiltInMusicPlaylistAsync()
    {
        CancelMusicPlaylistLoad();
        var version = ++_musicPlaylistLoadVersion;
        var loadCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            _storeOperationCancellation.Token);
        _musicPlaylistLoadCancellation = loadCancellation;
        UpdateMusicPlayerUi("Preparando músicas internas...", isPlaying: false);
        try
        {
            var tracks = await Task.Run(
                () => EmbeddedMusicLibrary.PreparePlaylist(loadCancellation.Token),
                loadCancellation.Token);
            if (loadCancellation.IsCancellationRequested
                || version != _musicPlaylistLoadVersion)
                return;
            if (tracks.Count == 0)
                throw new InvalidDataException("A playlist interna está vazia.");
            ActivateMusicPlaylist(
                tracks,
                startPlayback: true,
                isBuiltInPlaylist: true);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception) when (exception is IOException
                                           or UnauthorizedAccessException
                                           or InvalidDataException
                                           or CryptographicException
                                           or ArgumentException
                                           or NotSupportedException)
        {
            if (version != _musicPlaylistLoadVersion) return;
            _musicPlayer.Stop();
            _musicPlayer.Close();
            DisposeActiveEmbeddedMusicTrackLease();
            _musicTracks.Clear();
            _musicTrackIndex = -1;
            _openedMusicTrackPath = string.Empty;
            _isMusicPlaying = false;
            UpdateMusicPlayerUi(
                $"Músicas internas indisponíveis: {exception.Message}",
                isPlaying: false);
        }
        finally
        {
            if (ReferenceEquals(_musicPlaylistLoadCancellation, loadCancellation))
                _musicPlaylistLoadCancellation = null;
            loadCancellation.Dispose();
        }
    }

    private async void ChooseMusicFolder_Click(object sender, RoutedEventArgs e)
    {
        var initialFolder = Directory.Exists(_musicFolderPath)
            ? _musicFolderPath
            : Environment.GetFolderPath(Environment.SpecialFolder.MyMusic);
        var selected = ChooseFolder("Escolha a pasta de músicas", initialFolder);
        if (selected is null) return;
        await LoadMusicPlaylistAsync(
            selected,
            startPlayback: true,
            persistAfterValidation: true);
    }

    private async Task LoadMusicPlaylistAsync(
        string folder,
        bool startPlayback,
        bool persistAfterValidation)
    {
        CancelMusicPlaylistLoad();
        var version = ++_musicPlaylistLoadVersion;
        var loadCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            _storeOperationCancellation.Token);
        _musicPlaylistLoadCancellation = loadCancellation;
        UpdateMusicPlayerUi("Analisando a pasta de músicas...", _isMusicPlaying);
        try
        {
            var tracks = await Task.Run(
                () => DiscoverMusicTracks(folder, loadCancellation.Token),
                loadCancellation.Token);
            if (loadCancellation.IsCancellationRequested
                || version != _musicPlaylistLoadVersion)
                return;
            if (tracks.Count == 0)
                throw new InvalidDataException("Nenhuma música compatível foi encontrada.");
            if (persistAfterValidation && !LocalDataPaths.WriteMusicFolder(folder))
                throw new IOException("Não foi possível salvar a pasta de músicas escolhida.");

            _musicFolderPath = Path.GetFullPath(folder);
            ActivateMusicPlaylist(
                tracks,
                startPlayback,
                isBuiltInPlaylist: false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception) when (exception is IOException
                                           or UnauthorizedAccessException
                                           or InvalidDataException
                                           or ArgumentException
                                           or NotSupportedException)
        {
            if (version != _musicPlaylistLoadVersion) return;
            _musicPlayer.Stop();
            _musicPlayer.Close();
            DisposeActiveEmbeddedMusicTrackLease();
            _musicTracks.Clear();
            _musicTrackIndex = -1;
            _openedMusicTrackPath = string.Empty;
            _isMusicPlaying = false;
            UpdateMusicPlayerUi($"Pasta de músicas indisponível: {exception.Message}", false);
        }
        finally
        {
            if (ReferenceEquals(_musicPlaylistLoadCancellation, loadCancellation))
                _musicPlaylistLoadCancellation = null;
            loadCancellation.Dispose();
        }
    }

    private void ActivateMusicPlaylist(
        IReadOnlyCollection<string> tracks,
        bool startPlayback,
        bool isBuiltInPlaylist)
    {
        if (tracks.Count == 0)
            throw new InvalidDataException("A playlist de músicas está vazia.");
        _musicPlayer.Stop();
        _musicPlayer.Close();
        DisposeActiveEmbeddedMusicTrackLease();
        _musicTracks.Clear();
        _musicTracks.AddRange(tracks);
        _isBuiltInMusicPlaylist = isBuiltInPlaylist;
        _musicTrackIndex = 0;
        _openedMusicTrackPath = string.Empty;
        _isMusicPlaying = false;
        _consecutiveMusicFailures = 0;
        if (startPlayback)
            PlayMusicAtIndex(_musicTrackIndex);
        else
            UpdateMusicPlayerUi(
                Path.GetFileNameWithoutExtension(_musicTracks[_musicTrackIndex]),
                isPlaying: false);
    }

    private static List<string> DiscoverMusicTracks(
        string folder,
        CancellationToken cancellationToken)
    {
        const int maximumEntries = 25_000;
        var root = PathIdentity.Canonicalize(folder);
        if (!Directory.Exists(root))
            throw new DirectoryNotFoundException("A pasta de músicas não foi encontrada.");

        var pending = new Stack<string>();
        var tracks = new List<string>();
        var visitedEntries = 0;
        pending.Push(root);
        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var current = pending.Pop();
            using var tree = PathIdentity.OpenDirectoryTree(current);
            foreach (var entry in Directory.EnumerateFileSystemEntries(current))
            {
                cancellationToken.ThrowIfCancellationRequested();
                visitedEntries++;
                if (visitedEntries > maximumEntries)
                    throw new InvalidDataException(
                        "A pasta de músicas excede o limite de 25.000 arquivos e pastas.");
                var attributes = File.GetAttributes(entry);
                if ((attributes & FileAttributes.ReparsePoint) != 0) continue;
                if ((attributes & FileAttributes.Directory) != 0)
                {
                    pending.Push(entry);
                    continue;
                }

                if (SupportedMusicExtensions.Contains(Path.GetExtension(entry)))
                    tracks.Add(PathIdentity.Canonicalize(entry));
            }
            tree.Revalidate();
        }

        tracks.Sort(StringComparer.CurrentCultureIgnoreCase);
        return tracks;
    }

    private void CancelMusicPlaylistLoad()
    {
        ++_musicPlaylistLoadVersion;
        var cancellation = _musicPlaylistLoadCancellation;
        _musicPlaylistLoadCancellation = null;
        if (cancellation is null) return;
        try { cancellation.Cancel(); }
        catch (ObjectDisposedException) { }
    }

    private void MusicPrevious_Click(object sender, RoutedEventArgs e) =>
        MoveMusicTrack(-1);

    private void MusicNext_Click(object sender, RoutedEventArgs e) =>
        MoveMusicTrack(1);

    private void MoveMusicTrack(int direction)
    {
        if (_musicTracks.Count == 0) return;
        var next = (_musicTrackIndex + direction) % _musicTracks.Count;
        if (next < 0) next += _musicTracks.Count;
        PlayMusicAtIndex(next);
    }

    private void MusicPlayPause_Click(object sender, RoutedEventArgs e)
    {
        if (_musicTracks.Count == 0)
        {
            ChooseMusicFolder_Click(sender, e);
            return;
        }

        if (_isMusicPlaying)
        {
            _musicPlayer.Pause();
            _isMusicPlaying = false;
        }
        else if (_musicTrackIndex >= 0)
        {
            var selectedPath = _musicTracks[_musicTrackIndex];
            if (!_openedMusicTrackPath.Equals(
                    selectedPath,
                    StringComparison.OrdinalIgnoreCase))
                PlayMusicAtIndex(_musicTrackIndex);
            else
            {
                _musicPlayer.Play();
                _isMusicPlaying = true;
            }
        }
        UpdateMusicPlayerUi(
            Path.GetFileNameWithoutExtension(_musicTracks[_musicTrackIndex]),
            _isMusicPlaying);
    }

    private void MusicVolume_ValueChanged(
        object sender,
        RoutedPropertyChangedEventArgs<double> e)
    {
        _musicPlayer.Volume = Math.Clamp(e.NewValue / 100d, 0d, 1d);
    }

    private void PlayMusicAtIndex(int index)
    {
        if (index < 0 || index >= _musicTracks.Count) return;
        _musicTrackIndex = index;
        var path = _musicTracks[index];
        try
        {
            _musicPlayer.Stop();
            _musicPlayer.Close();
            DisposeActiveEmbeddedMusicTrackLease();
            if (_isBuiltInMusicPlaylist)
            {
                _activeEmbeddedMusicTrackLease = EmbeddedMusicLibrary.OpenVerifiedTrackLease(
                    path,
                    _storeOperationCancellation.Token);
                _activeEmbeddedMusicTrackLease.Revalidate();
            }
            var attributes = File.GetAttributes(path);
            if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
                throw new InvalidDataException("A faixa não é um arquivo local comum.");
            _musicPlayer.Open(new Uri(path, UriKind.Absolute));
            _openedMusicTrackPath = path;
            _musicPlayer.Play();
            _isMusicPlaying = true;
            UpdateMusicPlayerUi(Path.GetFileNameWithoutExtension(path), true);
        }
        catch (Exception exception) when (exception is IOException
                                           or UnauthorizedAccessException
                                           or InvalidDataException
                                           or CryptographicException
                                           or UriFormatException
                                           or NotSupportedException)
        {
            DisposeActiveEmbeddedMusicTrackLease();
            _openedMusicTrackPath = string.Empty;
            _isMusicPlaying = false;
            UpdateMusicPlayerUi($"Faixa indisponível: {exception.Message}", false);
        }
    }

    private void MusicPlayer_MediaOpened(object? sender, EventArgs e)
    {
        _consecutiveMusicFailures = 0;
        if (_musicTrackIndex >= 0 && _musicTrackIndex < _musicTracks.Count)
            UpdateMusicPlayerUi(
                Path.GetFileNameWithoutExtension(_musicTracks[_musicTrackIndex]),
                _isMusicPlaying);
    }

    private void MusicPlayer_MediaEnded(object? sender, EventArgs e)
    {
        _consecutiveMusicFailures = 0;
        MoveMusicTrack(1);
    }

    private void MusicPlayer_MediaFailed(object? sender, ExceptionEventArgs e)
    {
        _consecutiveMusicFailures++;
        if (_musicTracks.Count == 0 || _consecutiveMusicFailures >= _musicTracks.Count)
        {
            _musicPlayer.Stop();
            _musicPlayer.Close();
            DisposeActiveEmbeddedMusicTrackLease();
            _isMusicPlaying = false;
            UpdateMusicPlayerUi("Nenhuma faixa pôde ser reproduzida", false);
            return;
        }
        MoveMusicTrack(1);
    }

    private void UpdateMusicPlayerUi(string trackLabel, bool isPlaying)
    {
        SetText("MusicTrackTitle", trackLabel);
        SetText("MusicPlayPauseGlyph", isPlaying ? "Ⅱ" : "▶");
        SetText("MusicPlaybackStatus", isPlaying ? "TOCANDO" : "PAUSADO");
    }

    private void DisposeActiveEmbeddedMusicTrackLease()
    {
        _activeEmbeddedMusicTrackLease?.Dispose();
        _activeEmbeddedMusicTrackLease = null;
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

        ApplyThemeAccent(accent);
    }

    private void ApplyThemeAccent(Color accent)
    {
        accent = SoftenThemeAccent(accent);
        var bright = Color.FromRgb(
            BlendThemeChannel(accent.R, 255, .22),
            BlendThemeChannel(accent.G, 255, .22),
            BlendThemeChannel(accent.B, 255, .22));
        var contrast = ChooseThemeContrastColor(accent);
        var brightContrast = ChooseThemeContrastColor(bright);

        Resources["CurrentSystemAccentColor"] = accent;
        SetCategoryThemeBrush("CurrentSystemAccentBrush", accent);
        SetCategoryThemeBrush("CurrentSystemBrightBrush", bright);
        SetCategoryThemeBrush("CurrentSystemContrastBrush", contrast);
        SetCategoryThemeBrush("CurrentSystemBrightContrastBrush", brightContrast);
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
        var sidebarBackground = new LinearGradientBrush
        {
            StartPoint = new Point(0, .5),
            EndPoint = new Point(1, .5)
        };
        sidebarBackground.GradientStops.Add(new GradientStop(
            Color.FromArgb(255, accent.R, accent.G, accent.B), 0));
        sidebarBackground.GradientStops.Add(new GradientStop(
            Color.FromArgb(255, accent.R, accent.G, accent.B), .05));
        sidebarBackground.GradientStops.Add(new GradientStop(
            Color.FromArgb(
                64,
                BlendThemeChannel(accent.R, 6, .55),
                BlendThemeChannel(accent.G, 9, .55),
                BlendThemeChannel(accent.B, 12, .55)),
            .16));
        sidebarBackground.GradientStops.Add(new GradientStop(
            Color.FromRgb(8, 10, 8), .32));
        sidebarBackground.GradientStops.Add(new GradientStop(
            Color.FromRgb(5, 7, 5), 1));
        sidebarBackground.Freeze();
        Resources["CurrentSystemSidebarBrush"] = sidebarBackground;

        var sidebarSelection = new LinearGradientBrush
        {
            StartPoint = new Point(0, .5),
            EndPoint = new Point(1, .5)
        };
        sidebarSelection.GradientStops.Add(new GradientStop(
            Color.FromArgb(255, accent.R, accent.G, accent.B), 0));
        sidebarSelection.GradientStops.Add(new GradientStop(
            Color.FromArgb(255, accent.R, accent.G, accent.B), .05));
        sidebarSelection.GradientStops.Add(new GradientStop(
            Color.FromArgb(110, accent.R, accent.G, accent.B), .14));
        sidebarSelection.GradientStops.Add(new GradientStop(
            Color.FromArgb(28, accent.R, accent.G, accent.B), .28));
        sidebarSelection.GradientStops.Add(new GradientStop(
            Color.FromArgb(0, 0, 0, 0), .42));
        sidebarSelection.Freeze();
        Resources["CurrentSystemSidebarSelectionBrush"] = sidebarSelection;

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

    internal static string FormatMaskedLicenseId(string licenseId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(licenseId);
        var normalized = licenseId.Trim();
        if (normalized.Length <= 4) return "Cliente ••••";
        return $"Cliente ••••{normalized[^4..]}";
    }

    private static Color SoftenThemeAccent(Color accent) => Color.FromRgb(
        BlendThemeChannel(accent.R, 222, .32),
        BlendThemeChannel(accent.G, 226, .32),
        BlendThemeChannel(accent.B, 224, .32));

    internal static Color ChooseThemeContrastColor(Color background)
    {
        var dark = Color.FromRgb(7, 12, 7);
        var light = Colors.White;
        return CalculateContrastRatio(dark, background)
               >= CalculateContrastRatio(light, background)
            ? dark
            : light;
    }

    private static double CalculateContrastRatio(Color foreground, Color background)
    {
        var foregroundLuminance = CalculateRelativeLuminance(foreground);
        var backgroundLuminance = CalculateRelativeLuminance(background);
        var lighter = Math.Max(foregroundLuminance, backgroundLuminance);
        var darker = Math.Min(foregroundLuminance, backgroundLuminance);
        return (lighter + .05) / (darker + .05);
    }

    private static double CalculateRelativeLuminance(Color color) =>
        .2126 * LinearizeColorChannel(color.R)
        + .7152 * LinearizeColorChannel(color.G)
        + .0722 * LinearizeColorChannel(color.B);

    private static double LinearizeColorChannel(byte channel)
    {
        var normalized = channel / 255d;
        return normalized <= .04045
            ? normalized / 12.92
            : Math.Pow((normalized + .055) / 1.055, 2.4);
    }

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
    private static bool UsesPortraitCarouselCovers => true;

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
        if (SelectedCategory?.Id.Equals("emulators", StringComparison.OrdinalIgnoreCase) == true)
        {
            if (EmulatorCoverAccentMap.TryGetValue(current.Id, out var emulatorAccent))
                ApplyThemeAccent(emulatorAccent);
            else
                ApplyCategoryTheme(SelectedCategory);
        }
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

        SwitchRetroSystemVideo(current);
    }

    private bool HasPendingRetroCarouselMotion =>
        _isRetroCarouselAnimating
        || _pendingRetroCarouselSteps != 0
        || _remainingRetroCarouselClickSteps != 0;

    private void StartRetroUniversalVideo()
    {
        if (IsVideoPlaybackDisabled)
        {
            StopRetroUniversalVideo();
            return;
        }
        if (!IsRetroCarouselVisible || WindowState == WindowState.Minimized)
            return;

        var categoryId = SelectedCategory?.Id ?? string.Empty;
        if (_retroUniversalVideoPlayer is { Source: not null } active
            && _retroUniversalVideoLease is { IsActive: true } activeLease
            && _activeRetroUniversalVideoCategoryId.Equals(
                categoryId,
                StringComparison.OrdinalIgnoreCase)
            && IsPlayerUsingLease(active, activeLease))
        {
            try { active.Play(); }
            catch (InvalidOperationException) { StopRetroUniversalVideo(); }
            return;
        }

        if (_pendingRetroUniversalVideoCategoryId.Equals(
                categoryId,
                StringComparison.OrdinalIgnoreCase)
            && !_retroUniversalVideoLoadTask.IsCompleted)
            return;

        StopRetroUniversalVideo();
        if (Dispatcher.HasShutdownStarted) return;

        var requestVersion = ++_retroUniversalVideoRequestVersion;
        var requestCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            _storeOperationCancellation.Token);
        _pendingRetroUniversalVideoCategoryId = categoryId;
        Volatile.Write(ref _retroUniversalVideoLoadCancellation, requestCancellation);
        _retroUniversalVideoLoadTask = LoadRetroUniversalVideoAsync(
            categoryId,
            requestVersion,
            requestCancellation);
    }

    private async Task LoadRetroUniversalVideoAsync(
        string categoryId,
        int requestVersion,
        CancellationTokenSource requestCancellation)
    {
        TrustedVideoLease? videoLease = null;
        var cancellationToken = requestCancellation.Token;
        try
        {
            videoLease = await Task.Run(
                    () => OpenRetroUniversalVideoLeaseCore(
                        categoryId,
                        cancellationToken),
                    cancellationToken)
                .ConfigureAwait(false);
            if (Dispatcher.HasShutdownStarted) return;

            var completion = Dispatcher.InvokeAsync(
                () => CompleteRetroUniversalVideoLoad(
                    categoryId,
                    requestVersion,
                    requestCancellation,
                    videoLease),
                DispatcherPriority.ContextIdle);
            if (await completion.Task.ConfigureAwait(false))
                videoLease = null;
        }
        catch (OperationCanceledException)
        {
        }
        catch (InvalidOperationException exception)
        {
            Debug.WriteLine($"Carregamento assíncrono do vídeo universal falhou: {exception.Message}");
        }
        finally
        {
            videoLease?.Dispose();
            if (ReferenceEquals(
                    Interlocked.CompareExchange(
                        ref _retroUniversalVideoLoadCancellation,
                        null,
                        requestCancellation),
                    requestCancellation))
            {
                requestCancellation.Dispose();
            }
        }
    }

    private bool CompleteRetroUniversalVideoLoad(
        string categoryId,
        int requestVersion,
        CancellationTokenSource requestCancellation,
        TrustedVideoLease? videoLease)
    {
        if (!ReferenceEquals(
                Interlocked.CompareExchange(
                    ref _retroUniversalVideoLoadCancellation,
                    null,
                    requestCancellation),
                requestCancellation))
            return false;

        var requestWasCanceled = requestCancellation.IsCancellationRequested;
        requestCancellation.Dispose();
        if (requestVersion == _retroUniversalVideoRequestVersion)
            _pendingRetroUniversalVideoCategoryId = string.Empty;
        if (requestWasCanceled
            || requestVersion != _retroUniversalVideoRequestVersion
            || videoLease is null
            || IsVideoPlaybackDisabled
            || !IsRetroCarouselVisible
            || WindowState == WindowState.Minimized
            || !(SelectedCategory?.Id ?? string.Empty).Equals(
                categoryId,
                StringComparison.OrdinalIgnoreCase)
            || FindNamed<Grid>("RetroUniversalVideoPlayerHost") is not { } host)
            return false;

        CloseRetroUniversalVideoCore();
        var player = CreateResponsiveBackgroundVideoPlayer(host);
        player.MediaEnded += RetroUniversalVideo_MediaEnded;
        player.MediaFailed += RetroUniversalVideo_MediaFailed;
        _retroUniversalVideoPlayer = player;
        _retroUniversalVideoLease = videoLease;
        _activeRetroUniversalVideoCategoryId = categoryId;
        host.Children.Add(player);
        try
        {
            player.Source = new Uri(videoLease.Path, UriKind.Absolute);
            player.Play();
        }
        catch (Exception exception) when (exception is InvalidOperationException
                                           or UriFormatException
                                           or NotSupportedException)
        {
            Debug.WriteLine($"Vídeo universal indisponível: {exception.Message}");
            StopRetroUniversalVideo();
            return false;
        }
        return true;
    }

    private void RetroUniversalVideo_MediaEnded(object? sender, RoutedEventArgs e)
    {
        if (sender is not MediaElement player || !ReferenceEquals(player, _retroUniversalVideoPlayer))
            return;
        if (_retroUniversalVideoLease is not { IsActive: true } activeLease
            || !IsPlayerUsingLease(player, activeLease))
        {
            StopRetroUniversalVideo();
            return;
        }
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
        CancelRetroUniversalVideoLoad();
        if (_retroUniversalVideoPlayer is not { Source: not null } player) return;
        try { player.Pause(); }
        catch (InvalidOperationException) { }
    }

    private void ResumeRetroUniversalVideo()
    {
        if (IsVideoPlaybackDisabled)
        {
            StopRetroUniversalVideo();
            return;
        }
        if (!IsRetroCarouselVisible || WindowState == WindowState.Minimized) return;
        if (_retroUniversalVideoPlayer is { Source: not null } player
            && _retroUniversalVideoLease is { IsActive: true } activeLease
            && IsPlayerUsingLease(player, activeLease))
        {
            try { player.Play(); return; }
            catch (InvalidOperationException) { StopRetroUniversalVideo(); }
        }
        else
        {
            StopRetroUniversalVideo();
        }
        StartRetroUniversalVideo();
    }

    private void StopRetroUniversalVideo()
    {
        CancelRetroUniversalVideoLoad();
        CloseRetroUniversalVideoCore();
    }

    private void CancelRetroUniversalVideoLoad()
    {
        ++_retroUniversalVideoRequestVersion;
        _pendingRetroUniversalVideoCategoryId = string.Empty;
        CancelAndDisposeVideoLoad(
            Interlocked.Exchange(ref _retroUniversalVideoLoadCancellation, null));
    }

    private void CloseRetroUniversalVideoCore()
    {
        var player = _retroUniversalVideoPlayer;
        var lease = _retroUniversalVideoLease;
        _retroUniversalVideoPlayer = null;
        _retroUniversalVideoLease = null;
        _activeRetroUniversalVideoCategoryId = string.Empty;
        var playerClosed = player is null;
        if (player is not null)
        {
            player.MediaEnded -= RetroUniversalVideo_MediaEnded;
            player.MediaFailed -= RetroUniversalVideo_MediaFailed;
            try { player.Stop(); }
            catch (InvalidOperationException) { }
            try
            {
                player.Close();
                playerClosed = true;
            }
            catch (InvalidOperationException) { }
            try
            {
                player.Source = null;
                playerClosed = true;
            }
            catch (InvalidOperationException) { }
            if (FindNamed<Grid>("RetroUniversalVideoPlayerHost") is { } host)
                host.Children.Remove(player);
        }

        // Keep the same verified handle alive until MediaElement has been
        // stopped and closed, so the path cannot be replaced in between.
        ReleaseOrRetainVideoLease(player, lease, playerClosed);
    }

    private void SwitchRetroSystemVideo(CatalogItem item)
    {
        if (IsVideoPlaybackDisabled)
        {
            CancelRetroSystemVideoLoad();
            CloseRetroSystemVideoCore(clearFallback: false);
            return;
        }
        if (!IsRetroCarouselVisible || WindowState == WindowState.Minimized)
        {
            CancelRetroSystemVideoLoad();
            CloseRetroSystemVideoCore(clearFallback: false);
            if (WindowState == WindowState.Minimized && IsRetroCarouselMode)
                _retroSystemVideoPausedForWindow = true;
            return;
        }

        _retroSystemVideoPausedForWindow = false;
        if (_retroSystemVideoPlayer is { Source: not null } currentPlayer
            && _retroSystemVideoLease is { IsActive: true } activeLease
            && _activeRetroSystemVideoItemId.Equals(item.Id, StringComparison.OrdinalIgnoreCase)
            && IsPlayerUsingLease(currentPlayer, activeLease)
            && IsActiveRetroSystemVideo(currentPlayer))
        {
            try
            {
                currentPlayer.Play();
                return;
            }
            catch (InvalidOperationException exception)
            {
                Debug.WriteLine($"Não foi possível continuar o vídeo de sistema: {exception.Message}");
                CloseRetroSystemVideoCore(clearFallback: false);
            }
        }

        if (_pendingRetroSystemVideoItemId.Equals(item.Id, StringComparison.OrdinalIgnoreCase)
            && !_retroSystemVideoLoadTask.IsCompleted)
            return;

        CancelRetroSystemVideoLoad();
        CloseRetroSystemVideoCore(clearFallback: false);
        if (Dispatcher.HasShutdownStarted) return;

        var requestVersion = ++_retroSystemVideoRequestVersion;
        var requestCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            _storeOperationCancellation.Token);
        _pendingRetroSystemVideoItemId = item.Id;
        Volatile.Write(ref _retroSystemVideoLoadCancellation, requestCancellation);
        _retroSystemVideoLoadTask = LoadRetroSystemVideoAsync(
            item,
            requestVersion,
            requestCancellation);
    }

    private async Task LoadRetroSystemVideoAsync(
        CatalogItem item,
        int requestVersion,
        CancellationTokenSource requestCancellation)
    {
        TrustedVideoLease? videoLease = null;
        var cancellationToken = requestCancellation.Token;
        try
        {
            videoLease = await Task.Run(
                    () => OpenRetroSystemVideoLeaseCore(
                        item.Id,
                        cancellationToken),
                    cancellationToken)
                .ConfigureAwait(false);
            if (Dispatcher.HasShutdownStarted) return;

            var completion = Dispatcher.InvokeAsync(
                () => CompleteRetroSystemVideoLoad(
                    item,
                    requestVersion,
                    requestCancellation,
                    videoLease),
                DispatcherPriority.ContextIdle);
            if (await completion.Task.ConfigureAwait(false))
                videoLease = null;
        }
        catch (OperationCanceledException)
        {
        }
        catch (InvalidOperationException exception)
        {
            Debug.WriteLine($"Carregamento assíncrono do vídeo de sistema falhou: {exception.Message}");
        }
        finally
        {
            videoLease?.Dispose();
            if (ReferenceEquals(
                    Interlocked.CompareExchange(
                        ref _retroSystemVideoLoadCancellation,
                        null,
                        requestCancellation),
                    requestCancellation))
            {
                requestCancellation.Dispose();
            }
        }
    }

    private bool CompleteRetroSystemVideoLoad(
        CatalogItem item,
        int requestVersion,
        CancellationTokenSource requestCancellation,
        TrustedVideoLease? videoLease)
    {
        if (!ReferenceEquals(
                Interlocked.CompareExchange(
                    ref _retroSystemVideoLoadCancellation,
                    null,
                    requestCancellation),
                requestCancellation))
            return false;

        var requestWasCanceled = requestCancellation.IsCancellationRequested;
        requestCancellation.Dispose();
        if (requestVersion == _retroSystemVideoRequestVersion)
            _pendingRetroSystemVideoItemId = string.Empty;
        if (requestWasCanceled
            || requestVersion != _retroSystemVideoRequestVersion
            || videoLease is null
            || IsVideoPlaybackDisabled
            || !IsRetroCarouselVisible
            || WindowState == WindowState.Minimized
            || _retroCarouselItems.Count == 0
            || !_retroCarouselItems[WrapRetroCarouselIndex(_retroCarouselIndex)].Id.Equals(
                item.Id,
                StringComparison.OrdinalIgnoreCase))
            return false;

        OpenRetroSystemVideo(item, videoLease, requestVersion);
        return ReferenceEquals(_retroSystemVideoLease, videoLease);
    }

    private void OpenRetroSystemVideo(
        CatalogItem item,
        TrustedVideoLease videoLease,
        int generation)
    {
        if (IsVideoPlaybackDisabled
            || FindNamed<Grid>("RetroSystemVideoPlayerHost") is not { } playerHost)
        {
            videoLease.Dispose();
            return;
        }

        CloseRetroSystemVideoCore(clearFallback: false);
        var player = CreateResponsiveBackgroundVideoPlayer(playerHost);
        player.Opacity = 0;
        player.Tag = generation;
        player.MediaOpened += RetroSystemVideo_MediaOpened;
        player.MediaEnded += RetroSystemVideo_MediaEnded;
        player.MediaFailed += RetroSystemVideo_MediaFailed;

        _retroSystemVideoPlayer = player;
        _retroSystemVideoLease = videoLease;
        _activeRetroSystemVideoGeneration = generation;
        _activeRetroSystemVideoItemId = item.Id;
        _activeRetroSystemVideoPath = videoLease.Path;
        playerHost.Children.Add(player);

        try
        {
            player.Source = new Uri(videoLease.Path, UriKind.Absolute);
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

    private static MediaElement CreateResponsiveBackgroundVideoPlayer(FrameworkElement host)
    {
        var player = new MediaElement
        {
            LoadedBehavior = MediaState.Manual,
            UnloadedBehavior = MediaState.Manual,
            IsMuted = true,
            Volume = 0,
            Stretch = Stretch.UniformToFill,
            StretchDirection = StretchDirection.Both,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            Focusable = false,
            IsHitTestVisible = false,
            SnapsToDevicePixels = true,
            UseLayoutRounding = true
        };

        // Keep the player box tied to the viewport and use a centered proportional
        // cover. Different source ratios may be cropped at the edges, but the
        // background always fills the complete area without stretching or bars.
        player.SetBinding(
            WidthProperty,
            new Binding(nameof(ActualWidth))
            {
                Source = host,
                Mode = BindingMode.OneWay
            });
        player.SetBinding(
            HeightProperty,
            new Binding(nameof(ActualHeight))
            {
                Source = host,
                Mode = BindingMode.OneWay
            });
        return player;
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
            || _retroSystemVideoLease is not { IsActive: true } activeLease
            || player.Tag is not int generation
            || generation != _activeRetroSystemVideoGeneration
            || player.Source is null
            || string.IsNullOrEmpty(_activeRetroSystemVideoItemId)
            || string.IsNullOrEmpty(_activeRetroSystemVideoPath))
            return false;

        return _activeRetroSystemVideoPath.Equals(activeLease.Path, StringComparison.OrdinalIgnoreCase)
               && IsPlayerUsingLease(player, activeLease);
    }

    private static bool IsPlayerUsingLease(MediaElement player, TrustedVideoLease lease)
    {
        try
        {
            return lease.IsActive
                   && player.Source is { IsFile: true } source
                   && Path.GetFullPath(source.LocalPath).Equals(
                       lease.Path,
                       StringComparison.OrdinalIgnoreCase);
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

        CancelRetroSystemVideoLoad();
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
        if (IsVideoPlaybackDisabled)
        {
            CancelRetroSystemVideoLoad();
            CloseRetroSystemVideoCore(clearFallback: false);
            return;
        }
        if (!_retroSystemVideoPausedForWindow) return;
        _retroSystemVideoPausedForWindow = false;
        if (!IsRetroCarouselVisible || _retroCarouselItems.Count == 0) return;

        var current = _retroCarouselItems[_retroCarouselIndex];
        if (_retroSystemVideoPlayer is { Source: not null } player
            && _retroSystemVideoLease is { IsActive: true } activeLease
            && _activeRetroSystemVideoItemId.Equals(current.Id, StringComparison.OrdinalIgnoreCase)
            && IsPlayerUsingLease(player, activeLease)
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

        SwitchRetroSystemVideo(current);
    }

    private void StopRetroSystemVideo(bool clearFallback)
    {
        CancelRetroSystemVideoLoad();
        _retroSystemVideoPausedForWindow = false;
        CloseRetroSystemVideoCore(clearFallback);
    }

    private void CancelRetroSystemVideoLoad()
    {
        ++_retroSystemVideoRequestVersion;
        _pendingRetroSystemVideoItemId = string.Empty;
        CancelAndDisposeVideoLoad(
            Interlocked.Exchange(ref _retroSystemVideoLoadCancellation, null));
    }

    private static void CancelAndDisposeVideoLoad(CancellationTokenSource? cancellation)
    {
        if (cancellation is null) return;
        try
        {
            cancellation.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
        finally
        {
            cancellation.Dispose();
        }
    }

    private void CloseRetroSystemVideoCore(bool clearFallback)
    {
        var player = _retroSystemVideoPlayer;
        var lease = _retroSystemVideoLease;
        _retroSystemVideoPlayer = null;
        _retroSystemVideoLease = null;
        _activeRetroSystemVideoGeneration = 0;
        _activeRetroSystemVideoItemId = string.Empty;
        _activeRetroSystemVideoPath = string.Empty;
        _pendingRetroSystemVideoItemId = string.Empty;
        _retroSystemVideoRestartOnResume = false;
        var playerClosed = player is null;

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
                playerClosed = true;
            }
            catch (InvalidOperationException exception)
            {
                Debug.WriteLine($"Não foi possível liberar o vídeo de sistema: {exception.Message}");
            }
            try
            {
                player.Source = null;
                playerClosed = true;
            }
            catch (InvalidOperationException exception)
            {
                Debug.WriteLine($"Não foi possível limpar a origem do vídeo de sistema: {exception.Message}");
            }

            if (FindNamed<Grid>("RetroSystemVideoPlayerHost") is { } playerHost)
                playerHost.Children.Remove(player);
        }

        // Do not release either identity lock until its player has closed.
        ReleaseOrRetainVideoLease(player, lease, playerClosed);

        if (clearFallback && FindNamed<Image>("RetroSystemVideoFallback") is { } fallback)
            fallback.DataContext = null;
    }

    private static bool IsVideoPlaybackDisabled =>
        Volatile.Read(ref _videoPlaybackDisabled) != 0;

    private static void ReleaseOrRetainVideoLease(
        MediaElement? player,
        TrustedVideoLease? lease,
        bool playerClosed)
    {
        if (lease is null) return;
        if (playerClosed || player is null)
        {
            lease.Dispose();
            return;
        }

        // Fail closed for the process lifetime. Keeping the player together
        // with its complete path lease prevents GC ordering from releasing the
        // identity locks while the native media graph can still reference it.
        Volatile.Write(ref _videoPlaybackDisabled, 1);
        lock (FailedVideoCloseQuarantineGate)
        {
            if (FailedVideoCloseQuarantine.Count >= MaximumQuarantinedVideoPlaybacks)
            {
                Environment.FailFast(
                    "A quarantine de vídeos excedeu o limite seguro depois de falhas repetidas ao fechar MediaElement.");
            }
            FailedVideoCloseQuarantine.Add((player, lease));
            Debug.WriteLine(
                $"MediaElement retido em quarantine; reprodução desabilitada para o processo " +
                $"({FailedVideoCloseQuarantine.Count}/{MaximumQuarantinedVideoPlaybacks}).");
        }
    }

    private static TrustedVideoLease? OpenRetroSystemVideoLease(CatalogItem item) =>
        OpenRetroSystemVideoLeaseCore(item.Id, CancellationToken.None);

    private static TrustedVideoLease? OpenRetroSystemVideoLeaseCore(
        string itemId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var root = GetRetroSystemVideoRoot();
        if (root is null) return null;
        cancellationToken.ThrowIfCancellationRequested();
        return RetroSystemVideoMap.Value.TryGetValue(itemId, out var mappedFile)
            ? OpenRetroSystemVideoCandidate(root, mappedFile, cancellationToken)
            : OpenRetroSystemVideoCandidate(root, $"{itemId}.mp4", cancellationToken);
    }

    private static TrustedVideoLease? OpenRetroUniversalVideoLease(string? categoryId) =>
        OpenRetroUniversalVideoLeaseCore(categoryId, CancellationToken.None);

    private static TrustedVideoLease? OpenRetroUniversalVideoLeaseCore(
        string? categoryId,
        CancellationToken cancellationToken)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var fileName = ResolveRetroUniversalVideoFileName(categoryId);
            var root = Path.GetFullPath(Path.Combine(
                AppContext.BaseDirectory,
                "Assets",
                "BackgroundVideos"));
            var lease = OpenBackgroundVideoCandidate(root, fileName, cancellationToken);
            if (lease is null
                && !fileName.Equals("Turborama-background.mp4", StringComparison.OrdinalIgnoreCase))
            {
                lease = OpenBackgroundVideoCandidate(
                    root,
                    "Turborama-background.mp4",
                    cancellationToken);
            }
            return lease;
        }
        catch (Exception exception) when (exception is IOException
                                           or UnauthorizedAccessException
                                           or ArgumentException
                                           or NotSupportedException)
        {
            return null;
        }
    }

    private static string ResolveRetroUniversalVideoFileName(string? categoryId) =>
        categoryId?.ToLowerInvariant() switch
        {
            "system-tools" => "Turborama-background-system-tools.mp4",
            "playstation-1" => "Turborama-background-playstation.mp4",
            "playstation-2" or "playstation-2-br"
                => "Turborama-background-ps2.mp4",
            "playstation-4" => "Turborama-background-ps4.mp4",
            "playstation-5" => "Turborama-background-ps5.mp4",
            "psp" => "Turborama-background-psp.mp4",
            "ps-vita" => "Turborama-background-ps-vita.mp4",
            "sega-saturn" => "Turborama-background-sega-saturn.mp4",
            "xbox" or "xbox-360" or "xbox-one" or "xbox-series"
                => "Turborama-background-xbox-one-x.mp4",
            "nintendo-3ds" or "gamecube"
                => "Turborama-background-nintendo-generic.mp4",
            "nintendo-switch" => "Turborama-background-nintendo-switch.mp4",
            "nintendo-wii" or "nintendo-wii-u"
                => "Turborama-background-nintendo-wii.mp4",
            "windows" => "Turborama-background-windows.mp4",
            "retro-games" => "Turborama-background-retro.mp4",
            _ => "Turborama-background.mp4"
        };

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

    private static TrustedVideoLease? OpenBackgroundVideoCandidate(
        string root,
        string fileName,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!Directory.Exists(root)
            || !Path.GetFileName(fileName).Equals(fileName, StringComparison.Ordinal)
            || !fileName.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase)
            || HasReparsePointInRetroSystemVideoPath(root))
            return null;

        var candidate = Path.GetFullPath(Path.Combine(root, fileName));
        var prefix = Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar;
        if (!candidate.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            return null;

        TrustedVideoLease? lease = OpenTrustedBackgroundVideoLeaseCore(
            candidate,
            fileName,
            cancellationToken);
        if (lease is null) return null;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (HasReparsePointInRetroSystemVideoPath(root, candidate)) return null;
            var acceptedLease = lease;
            lease = null;
            return acceptedLease;
        }
        finally
        {
            lease?.Dispose();
        }
    }

    private static TrustedVideoLease? OpenRetroSystemVideoCandidate(
        string videoRoot,
        string? relativePath,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
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
            if (!candidate.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase))
                return null;

            TrustedVideoLease? lease = OpenTrustedRetroSystemVideoLeaseCore(
                candidate,
                fileName,
                cancellationToken);
            if (lease is null) return null;
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (HasReparsePointInRetroSystemVideoPath(videoRoot, candidate)) return null;
                var acceptedLease = lease;
                lease = null;
                return acceptedLease;
            }
            finally
            {
                lease?.Dispose();
            }
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
        return HasReparsePointInVideoPath(videoRoot)
               || leafPath is not null && HasReparsePointInVideoPath(leafPath);
    }

    private static bool HasReparsePointInVideoPath(string path)
    {
        var candidate = Path.GetFullPath(path);
        var volumeRoot = Path.GetPathRoot(candidate);
        if (string.IsNullOrEmpty(volumeRoot)) return true;

        var current = volumeRoot;
        if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
            return true;
        foreach (var segment in Path.GetRelativePath(volumeRoot, candidate).Split(
                     [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                     StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                return true;
        }
        return false;
    }

    private static TrustedVideoLease? OpenTrustedRetroSystemVideoLease(
        string path,
        string fileName) =>
        OpenTrustedRetroSystemVideoLeaseCore(path, fileName, CancellationToken.None);

    private static TrustedVideoLease? OpenTrustedRetroSystemVideoLeaseCore(
        string path,
        string fileName,
        CancellationToken cancellationToken) =>
        OpenTrustedVideoLease(
            path,
            fileName,
            RetroSystemVideoIntegrityMap.Value,
            cancellationToken);

    private static TrustedVideoLease? OpenTrustedBackgroundVideoLease(
        string path,
        string fileName) =>
        OpenTrustedBackgroundVideoLeaseCore(path, fileName, CancellationToken.None);

    private static TrustedVideoLease? OpenTrustedBackgroundVideoLeaseCore(
        string path,
        string fileName,
        CancellationToken cancellationToken) =>
        OpenTrustedVideoLease(
            path,
            fileName,
            BackgroundVideoIntegrityMap.Value,
            cancellationToken);

    private static TrustedVideoLease? OpenTrustedVideoLease(
        string path,
        string fileName,
        IReadOnlyDictionary<string, RetroSystemVideoIntegrity> integrityMap,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!integrityMap.TryGetValue(fileName, out var expected))
            return null;

        List<SafeFileHandle>? directoryHandles = null;
        SafeFileHandle? leafHandle = null;
        FileStream? stream = null;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var expectedHash = Convert.FromHexString(expected.Sha256);
            var canonicalPath = Path.GetFullPath(path);
            directoryHandles = OpenTrustedVideoDirectoryHandles(canonicalPath);
            cancellationToken.ThrowIfCancellationRequested();
            leafHandle = OpenTrustedVideoLeafHandle(canonicalPath);
            stream = new FileStream(
                leafHandle,
                FileAccess.Read,
                128 * 1024,
                isAsync: false);
            leafHandle = null;
            if (stream.Length != expected.Length || stream.Length < 12)
                return null;

            Span<byte> header = stackalloc byte[12];
            stream.ReadExactly(header);
            if (header[4] != (byte)'f'
                || header[5] != (byte)'t'
                || header[6] != (byte)'y'
                || header[7] != (byte)'p')
                return null;

            stream.Position = 0;
            var actualHash = ComputeVideoSha256(stream, cancellationToken);
            if (stream.Length != expected.Length
                || actualHash.Length != expectedHash.Length
                || !CryptographicOperations.FixedTimeEquals(actualHash, expectedHash))
                return null;

            var lease = new TrustedVideoLease(canonicalPath, stream, directoryHandles);
            stream = null;
            directoryHandles = null;
            return lease;
        }
        catch (Exception exception) when (exception is IOException
                                           or UnauthorizedAccessException
                                           or FormatException
                                           or ArgumentException
                                           or NotSupportedException
                                           or Win32Exception
                                           or OverflowException)
        {
            return null;
        }
        finally
        {
            stream?.Dispose();
            leafHandle?.Dispose();
            DisposeVideoPathHandles(directoryHandles);
        }
    }

    private static byte[] ComputeVideoSha256(
        Stream stream,
        CancellationToken cancellationToken)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = ArrayPool<byte>.Shared.Rent(128 * 1024);
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
            ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
        }
    }

    private static List<SafeFileHandle> OpenTrustedVideoDirectoryHandles(string canonicalPath)
    {
        var directory = Path.GetDirectoryName(canonicalPath)
                        ?? throw new IOException("O vídeo não possui um diretório válido.");
        var volumeRoot = Path.GetPathRoot(directory);
        if (string.IsNullOrEmpty(volumeRoot))
            throw new IOException("O vídeo não possui uma raiz de volume válida.");

        var paths = new List<string> { volumeRoot };
        var relativeDirectory = Path.GetRelativePath(volumeRoot, directory);
        if (!relativeDirectory.Equals(".", StringComparison.Ordinal))
        {
            var current = volumeRoot;
            foreach (var segment in relativeDirectory.Split(
                         [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                         StringSplitOptions.RemoveEmptyEntries))
            {
                current = Path.Combine(current, segment);
                paths.Add(current);
            }
        }

        var handles = new List<SafeFileHandle>(paths.Count);
        try
        {
            foreach (var directoryPath in paths)
            {
                var handle = OpenNativeVideoHandle(
                    directoryPath,
                    NativeFileReadAttributes,
                    NativeFileFlagBackupSemantics | NativeFileFlagOpenReparsePoint);
                try
                {
                    ValidateNativeVideoPathHandle(handle, directoryPath, expectDirectory: true);
                    handles.Add(handle);
                }
                catch
                {
                    handle.Dispose();
                    throw;
                }
            }
            return handles;
        }
        catch
        {
            DisposeVideoPathHandles(handles);
            throw;
        }
    }

    private static SafeFileHandle OpenTrustedVideoLeafHandle(string canonicalPath)
    {
        var handle = OpenNativeVideoHandle(
            canonicalPath,
            NativeGenericRead,
            NativeFileFlagOpenReparsePoint | NativeFileFlagSequentialScan);
        try
        {
            ValidateNativeVideoPathHandle(handle, canonicalPath, expectDirectory: false);
            return handle;
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    private static SafeFileHandle OpenNativeVideoHandle(
        string path,
        uint desiredAccess,
        uint flagsAndAttributes)
    {
        var handle = OpenNativeVideoPathHandle(
            ToExtendedVideoPath(path),
            desiredAccess,
            NativeFileShareRead,
            IntPtr.Zero,
            NativeOpenExisting,
            flagsAndAttributes,
            IntPtr.Zero);
        if (!handle.IsInvalid) return handle;

        var error = Marshal.GetLastPInvokeError();
        handle.Dispose();
        throw new Win32Exception(error, $"Não foi possível reservar o caminho de vídeo '{path}'.");
    }

    private static void ValidateNativeVideoPathHandle(
        SafeFileHandle handle,
        string expectedPath,
        bool expectDirectory)
    {
        if (GetNativeVideoFileInformation(
                handle,
                NativeFileAttributeTagInfoClass,
                out var information,
                (uint)Marshal.SizeOf<NativeFileAttributeTagInfo>()) == 0)
        {
            throw new Win32Exception(
                Marshal.GetLastPInvokeError(),
                $"Não foi possível validar os atributos do caminho de vídeo '{expectedPath}'.");
        }

        var attributes = (FileAttributes)information.FileAttributes;
        if ((attributes & FileAttributes.ReparsePoint) != 0 || information.ReparseTag != 0)
            throw new IOException($"O caminho de vídeo '{expectedPath}' contém um reparse point.");
        if (((attributes & FileAttributes.Directory) != 0) != expectDirectory)
            throw new IOException($"O tipo do caminho de vídeo '{expectedPath}' mudou durante a validação.");

        var finalPath = GetFinalVideoPath(handle);
        if (!VideoPathsEqual(finalPath, expectedPath))
        {
            throw new IOException(
                $"O caminho final do vídeo mudou durante a validação: '{expectedPath}'.");
        }
    }

    private static string GetFinalVideoPath(SafeFileHandle handle)
    {
        uint capacity = 512;
        while (capacity <= short.MaxValue)
        {
            var buffer = Marshal.AllocHGlobal(checked((int)capacity * sizeof(char)));
            try
            {
                var length = GetFinalNativeVideoPath(handle, buffer, capacity, flags: 0);
                if (length == 0)
                    throw new Win32Exception(Marshal.GetLastPInvokeError());
                if (length < capacity)
                {
                    var path = Marshal.PtrToStringUni(buffer, checked((int)length));
                    return NormalizeFinalVideoPath(
                        path ?? throw new IOException("O handle de vídeo não retornou um caminho final."));
                }
                capacity = checked(length + 1);
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
        throw new IOException("O caminho final do vídeo excede o limite suportado.");
    }

    private static string NormalizeFinalVideoPath(string path)
    {
        const string uncPrefix = @"\\?\UNC\";
        const string extendedPrefix = @"\\?\";
        if (path.StartsWith(uncPrefix, StringComparison.OrdinalIgnoreCase))
            path = @"\\" + path[uncPrefix.Length..];
        else if (path.StartsWith(extendedPrefix, StringComparison.OrdinalIgnoreCase))
            path = path[extendedPrefix.Length..];
        return Path.GetFullPath(path);
    }

    private static string ToExtendedVideoPath(string path)
    {
        var canonicalPath = Path.GetFullPath(path);
        if (canonicalPath.StartsWith(@"\\?\", StringComparison.Ordinal))
            return canonicalPath;
        return canonicalPath.StartsWith(@"\\", StringComparison.Ordinal)
            ? @"\\?\UNC\" + canonicalPath[2..]
            : @"\\?\" + canonicalPath;
    }

    private static bool VideoPathsEqual(string first, string second) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(first)).Equals(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(second)),
            StringComparison.OrdinalIgnoreCase);

    private static void DisposeVideoPathHandles(List<SafeFileHandle>? handles)
    {
        if (handles is null) return;
        for (var index = handles.Count - 1; index >= 0; index--)
            handles[index].Dispose();
    }

    private static Dictionary<string, string> LoadRetroSystemVideoMap()
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

    private static Dictionary<string, RetroSystemVideoIntegrity> LoadRetroSystemVideoIntegrityMap()
        => LoadVideoIntegrityMap("Turborama.SystemVideoIntegrity.json", "videos de sistema");

    private static Dictionary<string, RetroSystemVideoIntegrity> LoadBackgroundVideoIntegrityMap()
        => LoadVideoIntegrityMap("Turborama.BackgroundVideoIntegrity.json", "videos de fundo");

    private static Dictionary<string, RetroSystemVideoIntegrity> LoadVideoIntegrityMap(
        string resourceName,
        string label)
    {
        var entries = new Dictionary<string, RetroSystemVideoIntegrity>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var stream = typeof(StoreWindow).Assembly.GetManifestResourceStream(resourceName);
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
            Debug.WriteLine($"Integridade de {label} incorporada ignorada: {exception.Message}");
        }
        return entries;
    }

    private static Dictionary<string, string> LoadRetroPlatformDescriptions()
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

    private static RetroCarouselSlot GetRetroCarouselSlot(int offset)
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

    private static void ApplyRetroCarouselSlot(ContentControl control, RetroCarouselSlot slot)
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

    private static void AnimateRetroCarouselSurface(
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
        if (!TryEnsureAuthorized("alterar a pasta de instalação")) return;
        var selectedFolder = ChooseFolder("Escolha a pasta de instalação", _installFolderPath);
        if (selectedFolder is null) return;
        if (!TryEnsureAuthorized("alterar a pasta de instalação")) return;

        _installFolderPath = selectedFolder;
        _gameLibraryFolderPath = string.Empty;
        RememberApprovedRoot(selectedFolder);
        UpdateFolderLabels();
        if (!TryEnsureAuthorized("salvar a pasta de instalação")) return;
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
        if (!TryEnsureAuthorized("abrir a pasta de instalação")) return;
        if (!Directory.Exists(_installFolderPath))
        {
            SetCatalogStatus("A pasta de instalação não existe. Escolha uma pasta válida.");
            return;
        }

        try
        {
            if (!TryEnsureAuthorized("abrir a pasta de instalação")) return;
            Process.Start(CreateExplorerStartInfo(_installFolderPath));
            SetCatalogStatus("Pasta de instalação aberta.");
        }
        catch (Exception exception) when (exception is InvalidOperationException or Win32Exception)
        {
            SetCatalogStatus($"Não foi possível abrir a pasta: {exception.Message}");
        }
    }

    internal static ProcessStartInfo CreateExplorerStartInfo(string directoryPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directoryPath);

        var canonicalDirectory = Path.GetFullPath(directoryPath);
        var windowsDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        if (string.IsNullOrWhiteSpace(windowsDirectory)
            || !Path.IsPathFullyQualified(windowsDirectory))
        {
            throw new InvalidOperationException(
                "O diretório do Windows não pôde ser determinado com segurança.");
        }

        var explorerPath = Path.GetFullPath(Path.Combine(windowsDirectory, "explorer.exe"));
        var startInfo = new ProcessStartInfo(explorerPath)
        {
            UseShellExecute = false
        };
        startInfo.ArgumentList.Add(canonicalDirectory);
        return startInfo;
    }

    private void Support_Click(object sender, RoutedEventArgs e)
    {
        SetCatalogStatus("O canal de suporte ainda não foi configurado nesta versão.");
    }

    private async void StoreWindow_Loaded(object sender, RoutedEventArgs e)
    {
        Loaded -= StoreWindow_Loaded;
        _ = InitializeMusicPlayerAsync();
        if (_catalogRepository is null) return;

        try
        {
            ThrowIfOperationUnauthorized();
            var restoredCount = 0;
            var restoredItems = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var restoreRoots = new List<string>();
            if (IsExistingGameLibraryFolder(_gameLibraryFolderPath))
                restoreRoots.Add(Path.GetFullPath(_gameLibraryFolderPath));
            if (!restoreRoots.Contains(_installFolderPath, StringComparer.OrdinalIgnoreCase))
                restoreRoots.Add(_installFolderPath);

            var gameLibraryChoiceAttempted = false;
            string? restoreGameLibrary = null;
            var authorizedItems = _catalogRepository.Items
                .Where(item => item.HasAuthorizedArtifact)
                .ToArray();
            foreach (var restoreRoot in restoreRoots)
            {
                ThrowIfOperationUnauthorized();
                var savedDownloads = await Task.Run(
                    () => _downloadService.DiscoverResumableDownloads(
                        restoreRoot,
                        authorizedItems,
                        _storeOperationCancellation.Token),
                    _storeOperationCancellation.Token);
                foreach (var savedDownload in savedDownloads)
                {
                    ThrowIfOperationUnauthorized();
                    var item = _catalogRepository.FindById(savedDownload.ItemId);
                    if (item is null
                        || !item.HasAuthorizedArtifact
                        || !restoredItems.Add(item.Id)) continue;
                    var artifact = RequireAuthorizedArtifact(item);

                    ThrowIfOperationUnauthorized();
                    _downloadRootsByItem[item.Id] = restoreRoot;
                    RememberApprovedRoot(restoreRoot);
                    EnsureDownloadJob(item);
                    restoredCount++;

                    if (IsGameItem(item))
                    {
                        if (!gameLibraryChoiceAttempted)
                        {
                            ThrowIfOperationUnauthorized();
                            restoreGameLibrary = EnsureGameLibraryFolder();
                            ThrowIfOperationUnauthorized();
                            gameLibraryChoiceAttempted = true;
                        }

                        if (restoreGameLibrary is not null)
                        {
                            ThrowIfOperationUnauthorized();
                            _extractionRootsByItem[item.Id] = restoreGameLibrary;
                        }
                    }

                    ThrowIfOperationUnauthorized();
                    if (savedDownload.ArchiveReady && File.Exists(savedDownload.ArchiveFilePath))
                    {
                        if (artifact.ExtractPolicy == CatalogExtractPolicy.ExtractArchive)
                        {
                            ThrowIfOperationUnauthorized();
                            item.MarkArchiveReady(savedDownload.ArchiveFilePath);
                            if (IsGameItem(item) && restoreGameLibrary is null)
                            {
                                ThrowIfOperationUnauthorized();
                                item.AwaitExtractionLocation(
                                    $"Localize a pasta '{CatalogArchiveExtractor.GameLibraryFolderName}' para continuar.");
                                continue;
                            }

                            var extractionRoot = IsGameItem(item)
                                ? restoreGameLibrary!
                                : restoreRoot;
                            ThrowIfOperationUnauthorized();
                            _ = ExtractArchiveAsync(
                                item,
                                savedDownload.ArchiveFilePath,
                                extractionRoot,
                                restoreRoot);
                        }
                        else
                        {
                            ThrowIfOperationUnauthorized();
                            item.CompleteDownload(savedDownload.ArchiveFilePath);
                        }
                        continue;
                    }

                    var restorePaused = savedDownload.IsPaused
                                        || IsGameItem(item) && restoreGameLibrary is null;
                    ThrowIfOperationUnauthorized();
                    item.RestoreDownload(
                        savedDownload.BytesReceived,
                        savedDownload.TotalBytes,
                        restorePaused);
                    if (!restorePaused)
                    {
                        ThrowIfOperationUnauthorized();
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
        catch (OperationCanceledException)
        {
            SetCatalogStatus("A restauração foi cancelada porque a sessão terminou.");
        }
        catch (SuiteAuthorizationException)
        {
            SetCatalogStatus("A restauração foi bloqueada porque a sessão não está autorizada.");
        }
    }

    private async Task RunDownloadAsync(CatalogItem item, string installationRoot)
    {
        try
        {
            ThrowIfOperationUnauthorized();
            var artifact = RequireAuthorizedArtifact(item);
            var shouldExtract = artifact.ExtractPolicy == CatalogExtractPolicy.ExtractArchive;
            if (_downloadService.IsActive(item.Id))
            {
                SetCatalogStatus($"{item.Title}: o download ainda está ativo; aguarde a operação atual.");
                return;
            }

            var isGameItem = IsGameItem(item);
            string? gameLibraryRoot = null;
            if (isGameItem)
            {
                ThrowIfOperationUnauthorized();
                gameLibraryRoot = EnsureGameLibraryFolder();
                ThrowIfOperationUnauthorized();
                if (gameLibraryRoot is null)
                {
                    SetCatalogStatus(
                        $"{item.Title}: selecione a pasta '{CatalogArchiveExtractor.GameLibraryFolderName}' para iniciar ou continuar.");
                    return;
                }

                var hasRememberedDownloadRoot = _downloadRootsByItem.ContainsKey(item.Id);
                if (!shouldExtract
                    && (!hasRememberedDownloadRoot || !Directory.Exists(installationRoot)))
                    installationRoot = gameLibraryRoot;
                ThrowIfOperationUnauthorized();
                _extractionRootsByItem[item.Id] = gameLibraryRoot;
            }

            ThrowIfOperationUnauthorized();
            EnsureDownloadJob(item);
            _downloadRootsByItem[item.Id] = installationRoot;
            RememberApprovedRoot(installationRoot);
            SetCatalogStatus(item.CanResume
                ? $"{item.Title}: continuando do ponto salvo..."
                : $"{item.Title}: iniciando download verificado...");

            ThrowIfOperationUnauthorized();
            var result = await _downloadService.DownloadAsync(
                item,
                installationRoot,
                _storeOperationCancellation.Token);
            ThrowIfOperationUnauthorized();
            SetCatalogStatus(result.Message);
            if (!result.Succeeded) return;

            if (isGameItem)
            {
                ThrowIfOperationUnauthorized();
                gameLibraryRoot = EnsureGameLibraryFolder();
                ThrowIfOperationUnauthorized();
                if (gameLibraryRoot is null)
                {
                    if (shouldExtract)
                        item.AwaitExtractionLocation(
                            $"Localize a pasta '{CatalogArchiveExtractor.GameLibraryFolderName}' para concluir.");
                    SetCatalogStatus(
                        $"{item.Title}: download preservado; localize '{CatalogArchiveExtractor.GameLibraryFolderName}' para concluir.");
                    return;
                }
                ThrowIfOperationUnauthorized();
                _extractionRootsByItem[item.Id] = gameLibraryRoot;
            }

            if (isGameItem && !shouldExtract)
            {
                ThrowIfOperationUnauthorized();
                var placement = await EnsureDownloadedGameIsInsideLibraryAsync(
                    item,
                    result.LocalFilePath,
                    gameLibraryRoot!,
                    installationRoot);
                ThrowIfOperationUnauthorized();
                if (placement is not null)
                {
                    ThrowIfOperationUnauthorized();
                    _downloadRootsByItem[item.Id] = gameLibraryRoot!;
                    if (placement.SourceStateCleanupPending
                        && !installationRoot.Equals(
                            gameLibraryRoot,
                            StringComparison.OrdinalIgnoreCase))
                        _pendingLegacyDownloadRootsByItem[item.Id] = installationRoot;
                    else
                        _pendingLegacyDownloadRootsByItem.Remove(item.Id);
                    item.CompleteDownload(placement.LocalFilePath);
                    RefreshManagedGamesIfVisible(item.CategoryId);
                    RememberApprovedRoot(gameLibraryRoot!);
                    SetCatalogStatus(placement.SourceStateCleanupPending
                        ? $"{item.Title}: jogo concluído em {CatalogArchiveExtractor.GameLibraryFolderName}; o estado antigo ficou pendente para limpeza, sem afetar o arquivo instalado."
                        : $"{item.Title}: download concluído em {CatalogArchiveExtractor.GameLibraryFolderName}.");
                }
                return;
            }

            if (!shouldExtract) return;

            ThrowIfOperationUnauthorized();
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
        catch (OperationCanceledException)
        {
            SetCatalogStatus($"{item.Title}: operação cancelada porque a sessão terminou.");
        }
        catch (SuiteAuthorizationException)
        {
            SetCatalogStatus($"{item.Title}: a autorização terminou; nada novo foi iniciado.");
        }
    }

    private async Task ExtractArchiveAsync(
        CatalogItem item,
        string archivePath,
        string destinationBase,
        string downloadRoot,
        bool offerAnotherDrive = true)
    {
        var enteredQueue = false;
        try
        {
            ThrowIfOperationUnauthorized();
            await _extractionQueue.WaitAsync(_storeOperationCancellation.Token);
            enteredQueue = true;
            ThrowIfOperationUnauthorized();
            await ExtractArchiveCoreAsync(
                item,
                archivePath,
                destinationBase,
                downloadRoot,
                offerAnotherDrive);
        }
        catch (OperationCanceledException)
        {
            if (File.Exists(archivePath))
                item.FailExtraction("Extração cancelada; o pacote verificado foi preservado.");
            SetCatalogStatus($"{item.Title}: extração cancelada porque a sessão terminou.");
        }
        catch (SuiteAuthorizationException)
        {
            if (File.Exists(archivePath))
                item.FailExtraction("Autorização encerrada; o pacote verificado foi preservado.");
            SetCatalogStatus($"{item.Title}: a autorização terminou antes da extração.");
        }
        finally
        {
            if (enteredQueue) _extractionQueue.Release();
        }
    }

    private async Task ExtractArchiveCoreAsync(
        CatalogItem item,
        string archivePath,
        string destinationBase,
        string downloadRoot,
        bool offerAnotherDrive)
    {
        ThrowIfOperationUnauthorized();
        var artifact = RequireAuthorizedArtifact(item);
        if (artifact.ExtractPolicy != CatalogExtractPolicy.ExtractArchive)
            throw new InvalidDataException(
                "A política autorizada deste artefato não permite extração.");
        if (string.IsNullOrWhiteSpace(archivePath) || !File.Exists(archivePath))
        {
            item.FailExtraction("O pacote compactado não foi encontrado. Baixe-o novamente.");
            SetCatalogStatus($"{item.Title}: o pacote compactado não foi encontrado.");
            return;
        }

        ThrowIfOperationUnauthorized();
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
            artifact,
            baseDirectoryIsGameLibrary: isGameItem,
            itemId: item.Id,
            cancellationToken: _storeOperationCancellation.Token));

        ThrowIfOperationUnauthorized();
        if (result.Succeeded)
        {
            var completedLibraryRoot = isGameItem
                ? destinationBase
                : Path.Combine(destinationBase, CatalogArchiveExtractor.LibraryFolderName);
            ThrowIfOperationUnauthorized();
            RememberApprovedRoot(completedLibraryRoot);
            if (!await _downloadService.MarkExtractionCompletedAsync(
                    item,
                    downloadRoot,
                    archivePath,
                    _storeOperationCancellation.Token))
            {
                item.FailExtraction(
                    "O conteúdo foi extraído, mas o estado final não pôde ser salvo. " +
                    "O pacote foi preservado; clique em Tentar extração para finalizar.");
                SetCatalogStatus($"{item.Title}: {item.DownloadStatus}");
                return;
            }

            ThrowIfOperationUnauthorized();
            var archiveCleanupMessage = string.Empty;
            try
            {
                ThrowIfOperationUnauthorized();
                _ = CatalogExtractionCompletionCleanup.DeleteArchivePreservingRecoveryMarker(
                    archivePath,
                    downloadRoot,
                    result.DestinationPath);
            }
            catch (Exception exception) when (exception is IOException
                                               or UnauthorizedAccessException
                                               or InvalidDataException)
            {
                archiveCleanupMessage = $" A extração terminou, mas o pacote compactado não pôde ser apagado: {exception.Message}";
            }

            ThrowIfOperationUnauthorized();
            item.CompleteExtraction(result.DestinationPath);
            RefreshManagedGamesIfVisible(item.CategoryId);
            var libraryName = isGameItem
                ? CatalogArchiveExtractor.GameLibraryFolderName
                : CatalogArchiveExtractor.LibraryFolderName;
            SetCatalogStatus($"{item.Title}: extração concluída em {libraryName}.{archiveCleanupMessage}");
            return;
        }

        if (result.NeedsAnotherDrive)
        {
            ThrowIfOperationUnauthorized();
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
                ThrowIfOperationUnauthorized();
                var alternativeGameLibrary = ChooseAndPersistGameLibraryFolder(
                    destinationBase,
                    $"Selecione outra pasta chamada exatamente '{CatalogArchiveExtractor.GameLibraryFolderName}'");
                ThrowIfOperationUnauthorized();
                if (alternativeGameLibrary is null) return;

                ThrowIfOperationUnauthorized();
                _extractionRootsByItem[item.Id] = alternativeGameLibrary;
                await ExtractArchiveCoreAsync(
                    item,
                    archivePath,
                    alternativeGameLibrary,
                    downloadRoot,
                    offerAnotherDrive: false);
                return;
            }

            ThrowIfOperationUnauthorized();
            var selectedFolder = ChooseFolder(
                "Escolha outro HD ou uma pasta-base para criar TruboRoms",
                destinationBase);
            ThrowIfOperationUnauthorized();
            if (selectedFolder is null) return;

            var alternativeBase = NormalizeExtractionBase(selectedFolder);
            try
            {
                ThrowIfOperationUnauthorized();
                var libraryRoot = Path.Combine(alternativeBase, CatalogArchiveExtractor.LibraryFolderName);
                Directory.CreateDirectory(libraryRoot);
                ThrowIfOperationUnauthorized();
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
        if (!TryEnsureAuthorized("usar o catálogo")) return;
        var source = sender as FrameworkElement;
        var item = source?.Tag as CatalogItem ?? source?.DataContext as CatalogItem;
        if (item is null && source?.Tag is string itemId)
            item = _catalogRepository?.FindById(itemId);

        if (item is null)
        {
            SetCatalogStatus("Não foi possível identificar o pacote selecionado. Nenhum download foi iniciado.");
            return;
        }
        if (_managedGameDeletionInProgress.Contains(item.Id))
        {
            SetCatalogStatus($"{item.Title}: aguarde a exclusão local terminar.");
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
            if (!TryEnsureAuthorized("tentar a extração")) return;
            var extractionBase = IsGameItem(item)
                ? EnsureGameLibraryFolder()
                : _extractionRootsByItem.GetValueOrDefault(item.Id, downloadRoot);
            if (!TryEnsureAuthorized("tentar a extração")) return;
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

        if (!item.HasAuthorizedArtifact)
        {
            SetCatalogStatus(item.IsMaintenance
                ? $"{item.Title}: conteúdo temporariamente em manutenção."
                : item.HasExtractPolicyConflict
                ? $"{item.Title}: a política visual diverge do manifesto autorizado. Nada foi iniciado."
                : $"{item.Title}: conteúdo indisponível para esta sessão. Nada foi iniciado.");
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
        if (!TryEnsureAuthorized("continuar o download")) return;
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
        if (!TryEnsureAuthorized("tentar a extração")) return;
        var job = ResolveDownloadJob(sender as FrameworkElement);
        if (job is null || string.IsNullOrWhiteSpace(job.Item.ArchiveFilePath))
        {
            SetCatalogStatus("O pacote compactado não foi encontrado para nova tentativa.");
            return;
        }

        var downloadRoot = GetDownloadRoot(job.Item);
        if (!TryEnsureAuthorized("tentar a extração")) return;
        var extractionBase = IsGameItem(job.Item)
            ? EnsureGameLibraryFolder()
            : _extractionRootsByItem.GetValueOrDefault(job.ItemId, downloadRoot);
        if (!TryEnsureAuthorized("tentar a extração")) return;
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
        if (!TryEnsureAuthorized("apagar o download e o progresso")) return;
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
        if (!TryEnsureAuthorized("apagar o download e o progresso")) return;

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
        if (!TryEnsureAuthorized("abrir a pasta do download")) return;
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
        if (!TryEnsureAuthorized("preparar a biblioteca de jogos")) return null;
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
            if (!TryEnsureAuthorized("preparar a biblioteca de jogos")) return null;
            Directory.CreateDirectory(automaticLibrary);
            if (!TryEnsureAuthorized("preparar a biblioteca de jogos")) return null;
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
        if (!TryEnsureAuthorized("alterar a biblioteca de jogos")) return null;
        var initialDirectory = GetExistingFolderPickerStart(initialFolder);
        while (true)
        {
            var selectedFolder = ChooseFolder(title, initialDirectory);
            if (selectedFolder is null) return null;
            if (!TryEnsureAuthorized("alterar a biblioteca de jogos")) return null;

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
        if (!TryEnsureAuthorized("salvar a biblioteca de jogos")) return false;
        try
        {
            Directory.CreateDirectory(candidate);
        }
        catch (Exception exception) when (exception is IOException
                                           or UnauthorizedAccessException
                                           or ArgumentException
                                           or NotSupportedException)
        {
            SetCatalogStatus($"Não foi possível preparar a pasta mestre: {exception.Message}");
            return false;
        }
        if (!TryEnsureAuthorized("salvar a biblioteca de jogos")) return false;
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

            // Ao escolher uma unidade ou pasta-base alternativa, a criação de
            // TruboRoms\roms ocorre depois, no ponto central protegido por autorização.
            var child = Path.GetFullPath(Path.Combine(
                canonical,
                CatalogArchiveExtractor.GameLibraryFolderName));
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

    private async Task<DirectGamePlacement?> EnsureDownloadedGameIsInsideLibraryAsync(
        CatalogItem item,
        string downloadedPath,
        string gameLibraryRoot,
        string downloadRoot)
    {
        string? publishedDestination = null;
        try
        {
            var sourcePath = Path.GetFullPath(downloadedPath);
            var canonicalLibrary = Path.TrimEndingDirectorySeparator(Path.GetFullPath(gameLibraryRoot));
            if (!IsExistingGameLibraryFolder(canonicalLibrary))
                throw new DirectoryNotFoundException(
                    $"A pasta '{CatalogArchiveExtractor.GameLibraryFolderName}' foi movida.");

            var destinationPath = _downloadService.BuildSafeDestinationPath(
                canonicalLibrary,
                item);
            if (sourcePath.Equals(destinationPath, StringComparison.OrdinalIgnoreCase)
                && File.Exists(sourcePath))
                return new DirectGamePlacement(sourcePath, false);
            if (!File.Exists(sourcePath))
            {
                if (File.Exists(destinationPath))
                    return new DirectGamePlacement(destinationPath, false);
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

            var relocation = _downloadService.PrepareCompletedDirectDownloadRelocation(
                downloadRoot,
                canonicalLibrary,
                item,
                sourcePath,
                destinationPath,
                _storeOperationCancellation.Token);
            ThrowIfOperationUnauthorized();
            try
            {
                await MoveFilePreservingSourceOnFailureAsync(
                    sourcePath,
                    destinationPath,
                    item.Artifact
                    ?? throw new InvalidDataException("O artefato do jogo não possui identidade autorizada."),
                    _storeOperationCancellation.Token);
                publishedDestination = destinationPath;
            }
            catch
            {
                _downloadService.CancelCompletedDirectDownloadRelocation(relocation);
                throw;
            }
            var sourceStateRemoved = _downloadService
                .CompleteCompletedDirectDownloadRelocation(
                    relocation,
                    CancellationToken.None);
            return new DirectGamePlacement(
                destinationPath,
                SourceStateCleanupPending: !sourceStateRemoved);
        }
        catch (Exception exception) when (exception is IOException
                                           or UnauthorizedAccessException
                                           or InvalidDataException
                                           or ArgumentException
                                           or NotSupportedException)
        {
            if (publishedDestination is not null)
            {
                SetCatalogStatus(
                    $"{item.Title}: o arquivo foi instalado em '{publishedDestination}', mas a limpeza do estado antigo ficou pendente: {exception.Message}");
                return new DirectGamePlacement(
                    publishedDestination,
                    SourceStateCleanupPending: true);
            }
            SetCatalogStatus(
                $"{item.Title}: o download foi preservado em '{downloadedPath}', mas não pôde ser colocado em {CatalogArchiveExtractor.GameLibraryFolderName}: {exception.Message}");
            return null;
        }
    }

    internal static async Task MoveFilePreservingSourceOnFailureAsync(
        string sourcePath,
        string destinationPath,
        CatalogArtifactDescriptor artifact,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(artifact);
        var canonicalSource = PathIdentity.Canonicalize(sourcePath);
        var canonicalDestination = PathIdentity.Canonicalize(destinationPath);
        var sourceParent = Path.GetDirectoryName(canonicalSource)
                           ?? throw new InvalidDataException("A origem não possui diretório-pai.");
        var destinationParent = Path.GetDirectoryName(canonicalDestination)
                                ?? throw new InvalidDataException("O destino não possui diretório-pai.");
        using var sourceTree = PathIdentity.OpenDirectoryTree(sourceParent);
        using var destinationTree = PathIdentity.OpenDirectoryTree(
            destinationParent,
            createIfMissing: true);
        await using var source = sourceTree.OpenFile(
            canonicalSource,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            128 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan,
            deleteAccess: true);
        var sourceIdentity = PathIdentity.CaptureFileIdentity(
            source.SafeFileHandle,
            canonicalSource);
        if (source.Length != artifact.ContentLength)
            throw new InvalidDataException("O tamanho da origem diverge do descritor autorizado.");
        if (!await VerifyOpenArtifactAsync(source, artifact, cancellationToken))
            throw new InvalidDataException("O SHA-256 da origem diverge do descritor autorizado.");
        sourceTree.Revalidate();
        destinationTree.Revalidate();

        var destinationParentIdentity = PathIdentity.CaptureDirectoryIdentity(
            destinationTree.AnchorHandle,
            destinationParent);
        if (sourceIdentity.VolumeSerialNumber == destinationParentIdentity.VolumeSerialNumber)
        {
            _ = PathIdentity.RenameByHandle(
                source.SafeFileHandle,
                sourceIdentity,
                destinationTree.AnchorHandle,
                destinationParent,
                Path.GetFileName(canonicalDestination),
                replaceIfExists: false);
            return;
        }

        var temporaryPath = canonicalDestination + ".copy-" + Guid.NewGuid().ToString("N");
        FileStream? destination = null;
        PathIdentity.HandleIdentity? destinationIdentity = null;
        try
        {
            destination = destinationTree.OpenFile(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.ReadWrite,
                FileShare.Read,
                128 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan,
                deleteAccess: true);
            destinationIdentity = PathIdentity.CaptureFileIdentity(
                destination.SafeFileHandle,
                temporaryPath);
            source.Position = 0;
            destination.Position = 0;
            using (var sourceHasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
            {
                var buffer = GC.AllocateUninitializedArray<byte>(128 * 1024);
                try
                {
                    long copied = 0;
                    while (true)
                    {
                        var read = await source.ReadAsync(buffer, cancellationToken);
                        if (read == 0) break;
                        copied = checked(copied + read);
                        if (copied > artifact.ContentLength)
                            throw new InvalidDataException("A origem cresceu durante a cópia.");
                        sourceHasher.AppendData(buffer, 0, read);
                        await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                    }
                    if (copied != artifact.ContentLength
                        || !CryptographicOperations.FixedTimeEquals(
                            sourceHasher.GetHashAndReset(),
                            Convert.FromHexString(artifact.Sha256)))
                        throw new InvalidDataException("A origem mudou durante a cópia autenticada.");
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(buffer);
                }
            }
            await destination.FlushAsync(cancellationToken);
            destination.Flush(flushToDisk: true);
            cancellationToken.ThrowIfCancellationRequested();
            if (!await VerifyOpenArtifactAsync(destination, artifact, cancellationToken))
                throw new InvalidDataException("A cópia para a pasta mestre não preservou o SHA-256.");
            _ = PathIdentity.RevalidateFile(
                source.SafeFileHandle,
                canonicalSource,
                sourceIdentity);
            _ = PathIdentity.RevalidateFile(
                destination.SafeFileHandle,
                temporaryPath,
                destinationIdentity.Value);
            sourceTree.Revalidate();
            destinationTree.Revalidate();

            destinationIdentity = PathIdentity.RenameByHandle(
                destination.SafeFileHandle,
                destinationIdentity.Value,
                destinationTree.AnchorHandle,
                destinationParent,
                Path.GetFileName(canonicalDestination),
                replaceIfExists: false);
            cancellationToken.ThrowIfCancellationRequested();
            PathIdentity.DeleteByHandle(
                source.SafeFileHandle,
                canonicalSource,
                sourceIdentity);
        }
        catch
        {
            if (destination is not null && destinationIdentity is { } exactDestination)
            {
                // RenameByHandle can complete the kernel rename and then fail a
                // post-validation query. In that narrow case the caller has not
                // received the updated FinalPath yet. Probe only the two names
                // owned by this transaction and delete solely when the same
                // volume/file ID is still held by our original handle.
                foreach (var cleanupPath in new[] { canonicalDestination, temporaryPath })
                {
                    try
                    {
                        var currentIdentity = PathIdentity.CaptureFileIdentity(
                            destination.SafeFileHandle,
                            cleanupPath);
                        if (!currentIdentity.SameObject(exactDestination)) continue;
                        PathIdentity.DeleteByHandle(
                            destination.SafeFileHandle,
                            cleanupPath,
                            currentIdentity);
                        break;
                    }
                    catch (Exception cleanupException) when (cleanupException is IOException
                                                             or UnauthorizedAccessException
                                                             or InvalidDataException)
                    {
                        // Never fall back to deleting a pathname with unknown identity.
                    }
                }
            }
            throw;
        }
        finally
        {
            if (destination is not null) await destination.DisposeAsync();
        }
    }

    private static async Task<bool> VerifyOpenArtifactAsync(
        FileStream stream,
        CatalogArtifactDescriptor artifact,
        CancellationToken cancellationToken)
    {
        if (stream.Length != artifact.ContentLength) return false;
        stream.Position = 0;
        var hash = await SHA256.HashDataAsync(stream, cancellationToken);
        stream.Position = 0;
        return CryptographicOperations.FixedTimeEquals(
            hash,
            Convert.FromHexString(artifact.Sha256));
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
        if (!TryEnsureAuthorized("abrir a pasta do download")) return;
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
            if (!TryEnsureAuthorized("abrir a pasta do download")) return;
            Process.Start(CreateExplorerStartInfo(containingDirectory));
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
        var isGameManager = normalizedPage.Equals("GameManager", StringComparison.OrdinalIgnoreCase);
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
        SetVisibility("GameManagerPage", isGameManager);
        SetVisibility("DownloadsPage", isDownloads);
        SetVisibility("CatalogPage", isCatalog);

        if (isCatalog && SelectedCategory is not null)
        {
            RefreshCatalog();
            return;
        }

        if (isGameManager)
        {
            if (SelectedManagedGameSystem is null)
                SelectedManagedGameSystem = ManagedGameSystems.FirstOrDefault();
            else
                BeginManagedGameSystemRefresh();
        }

        var (title, subtitle) = isLibrary
            ? ("Minha biblioteca", "Acompanhe sua coleção Turborama")
            : isGameManager
                ? ("Jogos locais", "Analise e gerencie a pasta de ROMs")
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

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _windowSource = PresentationSource.FromVisual(this) as HwndSource;
        _windowSource?.AddHook(WindowMessageHook);
        ClampWindowToCurrentMonitor();
        ScheduleWorkAreaClamp();
    }

    protected override void OnLocationChanged(EventArgs e)
    {
        base.OnLocationChanged(e);
        if (_workAreaClampInProgress
            || WindowState != WindowState.Normal
            || _windowSource is null)
            return;

        var monitor = MonitorFromWindow(
            _windowSource.Handle,
            NativeMonitorDefaultToNearest);
        if (monitor != IntPtr.Zero && monitor != _lastWorkAreaMonitor)
            ScheduleWorkAreaClamp();
    }

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        ScheduleWorkAreaClamp();
    }

    private IntPtr WindowMessageHook(
        IntPtr windowHandle,
        int message,
        IntPtr wParam,
        IntPtr lParam,
        ref bool handled)
    {
        if (message == NativeWmDpiChanged)
            ScheduleWorkAreaClamp();
        return IntPtr.Zero;
    }

    private void ScheduleWorkAreaClamp()
    {
        if (_workAreaClampScheduled || Dispatcher.HasShutdownStarted) return;
        _workAreaClampScheduled = true;
        _ = Dispatcher.BeginInvoke(() =>
        {
            _workAreaClampScheduled = false;
            ClampWindowToCurrentMonitor();
        }, DispatcherPriority.Loaded);
    }

    private void ClampWindowToCurrentMonitor()
    {
        if (_workAreaClampInProgress
            || WindowState != WindowState.Normal
            || _windowSource is null)
            return;

        var windowHandle = _windowSource.Handle;
        if (windowHandle == IntPtr.Zero) return;

        var monitor = MonitorFromWindow(windowHandle, NativeMonitorDefaultToNearest);
        if (monitor == IntPtr.Zero) return;

        var monitorInfo = new NativeMonitorInfo
        {
            Size = checked((uint)Marshal.SizeOf<NativeMonitorInfo>())
        };
        if (!GetNativeMonitorInfo(monitor, ref monitorInfo)
            || !GetWindowRect(windowHandle, out var windowRect))
            return;

        _lastWorkAreaMonitor = monitor;
        var dpiScale = GetWindowDpiScale(windowHandle);
        var currentBounds = new Rect(
            windowRect.Left,
            windowRect.Top,
            windowRect.Right - windowRect.Left,
            windowRect.Bottom - windowRect.Top);
        var workArea = new Rect(
            monitorInfo.Work.Left,
            monitorInfo.Work.Top,
            monitorInfo.Work.Right - monitorInfo.Work.Left,
            monitorInfo.Work.Bottom - monitorInfo.Work.Top);
        if (currentBounds.Width <= 0
            || currentBounds.Height <= 0
            || workArea.Width <= 0
            || workArea.Height <= 0)
            return;

        var clampedBounds = ClampWindowBoundsToWorkArea(
            currentBounds,
            workArea,
            new Size(MinWidth * dpiScale, MinHeight * dpiScale));
        var x = checked((int)Math.Round(clampedBounds.X));
        var y = checked((int)Math.Round(clampedBounds.Y));
        var width = checked((int)Math.Round(clampedBounds.Width));
        var height = checked((int)Math.Round(clampedBounds.Height));
        if (x == windowRect.Left
            && y == windowRect.Top
            && width == windowRect.Right - windowRect.Left
            && height == windowRect.Bottom - windowRect.Top)
            return;

        _workAreaClampInProgress = true;
        try
        {
            _ = SetWindowPos(
                windowHandle,
                IntPtr.Zero,
                x,
                y,
                width,
                height,
                NativeSwpNoZOrder | NativeSwpNoActivate);
        }
        finally
        {
            _workAreaClampInProgress = false;
        }
    }

    private double GetWindowDpiScale(IntPtr windowHandle)
    {
        try
        {
            var dpi = GetDpiForWindow(windowHandle);
            if (dpi != 0) return dpi / 96d;
        }
        catch (EntryPointNotFoundException)
        {
            // GetDpiForWindow is unavailable on pre-Windows 10 systems. WPF's
            // visual DPI remains the correct fallback for the current source.
        }

        var visualDpi = VisualTreeHelper.GetDpi(this).DpiScaleX;
        return double.IsFinite(visualDpi) && visualDpi > 0 ? visualDpi : 1d;
    }

    internal static Rect ClampWindowBoundsToWorkArea(
        Rect windowBounds,
        Rect workArea,
        Size minimumSize)
    {
        if (windowBounds.IsEmpty
            || workArea.IsEmpty
            || windowBounds.Width <= 0
            || windowBounds.Height <= 0
            || workArea.Width <= 0
            || workArea.Height <= 0
            || minimumSize.IsEmpty
            || minimumSize.Width < 0
            || minimumSize.Height < 0
            || !double.IsFinite(windowBounds.X)
            || !double.IsFinite(windowBounds.Y)
            || !double.IsFinite(windowBounds.Width)
            || !double.IsFinite(windowBounds.Height)
            || !double.IsFinite(workArea.X)
            || !double.IsFinite(workArea.Y)
            || !double.IsFinite(workArea.Width)
            || !double.IsFinite(workArea.Height)
            || !double.IsFinite(minimumSize.Width)
            || !double.IsFinite(minimumSize.Height))
        {
            throw new ArgumentOutOfRangeException(
                nameof(windowBounds),
                "Os limites da janela e da work area precisam ser finitos e positivos.");
        }

        var minimumWidth = Math.Min(minimumSize.Width, workArea.Width);
        var minimumHeight = Math.Min(minimumSize.Height, workArea.Height);
        var width = Math.Min(Math.Max(windowBounds.Width, minimumWidth), workArea.Width);
        var height = Math.Min(Math.Max(windowBounds.Height, minimumHeight), workArea.Height);
        var x = Math.Clamp(windowBounds.X, workArea.Left, workArea.Right - width);
        var y = Math.Clamp(windowBounds.Y, workArea.Top, workArea.Bottom - height);
        return new Rect(x, y, width, height);
    }

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
        {
            ResumeRetroSystemVideoForWindow();
            if (WindowState == WindowState.Normal)
                ScheduleWorkAreaClamp();
        }
    }

    private void LicensingRuntime_AuthorizationRevoked(
        object? sender,
        SuiteAuthorizationRevokedEventArgs e)
    {
        if (Interlocked.Exchange(ref _revocationHandled, 1) != 0) return;
        if (Volatile.Read(ref _storeReady) == 0) return;
        _ = Dispatcher.InvokeAsync(() =>
        {
            if (Volatile.Read(ref _storeReady) == 0) return;
            SessionStatusText.Text = "SESSÃO ENCERRADA";
            SessionStatusText.Foreground = Brushes.OrangeRed;
            SessionStatusBadge.Background = new SolidColorBrush(Color.FromRgb(40, 15, 15));
            SessionStatusBadge.BorderBrush = new SolidColorBrush(Color.FromRgb(98, 38, 38));
            LicenseConsumerText.Text = "Cliente desconectado";
            LicenseConsumerStatusText.Text = "Licença sem sessão";
            LicenseConsumerStatusText.Foreground = Brushes.OrangeRed;
            IsEnabled = false;
            SetCatalogStatus("A autorização expirou ou foi revogada. Entre novamente.");
            var login = new PremiumLoginWindow();
            login.Show();
            Close();
        }, DispatcherPriority.Send);
    }

    protected override void OnClosed(EventArgs e)
    {
        Volatile.Write(ref _storeReady, 0);
        // Mark downloads as application shutdown before canceling the shared
        // Store token. Otherwise the cancellation catch can persist an
        // explicit user pause and prevent automatic continuation next launch.
        _downloadService.Dispose();
        CancelStoreOperations();
        CancelManagedGameScan();
        CancelMusicPlaylistLoad();
        StateChanged -= StoreWindow_StateChanged;
        _windowSource?.RemoveHook(WindowMessageHook);
        _windowSource = null;
        _authorizationSubscription.Dispose();
        StopRetroSystemVideo(clearFallback: true);
        StopRetroUniversalVideo();
        _musicPlayer.MediaOpened -= MusicPlayer_MediaOpened;
        _musicPlayer.MediaEnded -= MusicPlayer_MediaEnded;
        _musicPlayer.MediaFailed -= MusicPlayer_MediaFailed;
        _musicPlayer.Stop();
        _musicPlayer.Close();
        DisposeActiveEmbeddedMusicTrackLease();
        foreach (var job in DownloadJobs) job.Dispose();
        _ = DisposeLicensingRuntimeAsync(_licensingRuntime);
        base.OnClosed(e);
    }

    private static async Task DisposeLicensingRuntimeAsync(SuiteLicensingRuntime runtime)
    {
        try { await runtime.DisposeAsync(); }
        catch { }
    }

    private static CatalogArtifactDescriptor RequireAuthorizedArtifact(CatalogItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        var artifact = item.Artifact
                       ?? throw new SuiteAuthorizationException("ARTIFACT_NOT_AUTHORIZED");
        if (item.HasExtractPolicyConflict)
            throw new InvalidDataException(
                "A política de extração do catálogo visual diverge do manifesto autorizado.");
        return artifact;
    }

    private void ThrowIfOperationUnauthorized()
    {
        _storeOperationCancellation.Token.ThrowIfCancellationRequested();
        _authorization.ThrowIfUnauthorized();
    }

    private bool TryEnsureAuthorized(string action)
    {
        try
        {
            ThrowIfOperationUnauthorized();
            return true;
        }
        catch (Exception exception) when (exception is OperationCanceledException
                                           or SuiteAuthorizationException)
        {
            SetCatalogStatus($"Não foi possível {action}: a sessão terminou.");
            return false;
        }
    }

    private void CancelStoreOperations()
    {
        try
        {
            _storeOperationCancellation.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
