using System.Buffers.Binary;
using System.ComponentModel;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32.SafeHandles;
using TurboBoxManager.Catalog;
using TurboBoxManager.Licensing;

namespace TurboBoxManager.CatalogVerifier;

internal static class WpfTemplateVerifier
{
    private const uint NativeGenericWrite = 0x40000000;
    private const uint NativeOpenExisting = 3;
    private const uint NativeFileFlagBackupSemantics = 0x02000000;
    private const uint NativeFileFlagOpenReparsePoint = 0x00200000;
    private const uint NativeFsctlSetReparsePoint = 0x000900A4;
    private const uint NativeIoReparseTagMountPoint = 0xA0000003;

#pragma warning disable SYSLIB1054
    [DllImport(
        "kernel32.dll",
        EntryPoint = "CreateFileW",
        CharSet = CharSet.Unicode,
        ExactSpelling = true,
        SetLastError = true,
        BestFitMapping = false,
        ThrowOnUnmappableChar = true)]
    private static extern SafeFileHandle OpenNativeReparseHandle(
        string fileName,
        uint desiredAccess,
        uint shareMode,
        IntPtr securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        IntPtr templateFile);

    [DllImport(
        "kernel32.dll",
        EntryPoint = "DeviceIoControl",
        ExactSpelling = true,
        SetLastError = true)]
    private static extern int SetNativeReparsePoint(
        SafeFileHandle device,
        uint controlCode,
        IntPtr inputBuffer,
        uint inputBufferSize,
        IntPtr outputBuffer,
        uint outputBufferSize,
        out uint bytesReturned,
        IntPtr overlapped);

    [DllImport(
        "kernel32.dll",
        EntryPoint = "CreateHardLinkW",
        CharSet = CharSet.Unicode,
        ExactSpelling = true,
        SetLastError = true,
        BestFitMapping = false,
        ThrowOnUnmappableChar = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateNativeHardLink(
        string fileName,
        string existingFileName,
        IntPtr securityAttributes);
#pragma warning restore SYSLIB1054

    public static void Run(string categoryId)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            Application? application = null;
            StoreWindow? window = null;
            try
            {
                VerifyExplorerStartInfo();
                VerifyLicenseMasking();
                VerifyWindowBoundsClamp();
                VerifyPathIdentityLeaseAdversaries();
                VerifyArtifactPolicyDivergenceFailsClosed();
                VerifyBackgroundVideoManifestAndRouting();
                VerifyEmbeddedMusicResources();
                VerifyLocalGamesOnlyShowsPhysicalContent();
                application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                window = CreateAuthorizedWindowForRendering();
                window.ShowActivated = false;
                window.ShowInTaskbar = false;
                window.Left = -12000;
                window.Top = -12000;
                window.Width = 1360;
                window.Height = 728;
                window.Opacity = 0;

                var category = window.CatalogCategories.Single(item =>
                    item.Id.Equals(categoryId, StringComparison.OrdinalIgnoreCase));
                var openCatalog = typeof(StoreWindow).GetMethod(
                                      "OpenCatalog",
                                      BindingFlags.Instance | BindingFlags.NonPublic)
                                  ?? throw new MissingMethodException(
                                      nameof(StoreWindow),
                                  "OpenCatalog");
                if (window.LibrarySystems.Count != 22 || window.LibraryTotalItemCount != 902)
                    throw new InvalidDataException("A Biblioteca precisa contabilizar 22 sistemas e 902 jogos.");
                if (window.ManagedGameSystems.Count != 21
                    || window.FindName("GameManagerPage") is not Grid
                    || window.FindName("ManagedGamesList") is not ListBox
                    || window.FindName("GlobalMusicPlayer") is not Border
                    || window.FindName("GlobalMusicTrackTitle") is not TextBlock
                    || window.FindName("GlobalMusicPlayPauseGlyph") is not TextBlock
                    || window.FindName("MusicPreviousButton") is not Button
                    || window.FindName("MusicPlayPauseButton") is not Button
                    || window.FindName("MusicNextButton") is not Button
                    || window.FindName("MusicStopButton") is not Button
                    || window.FindName("MusicFolderButton") is not Button
                    || window.FindName("GlobalMusicVolumeSlider") is not Slider)
                    throw new InvalidDataException(
                        "Jogos locais precisa ser uma página separada e o player completo deve permanecer visível globalmente.");
                if (window.Resources["CatalogHudButtonStyle"] is not Style
                    {
                        TargetType: { } hudButtonTarget
                    } hudButtonStyle
                    || hudButtonTarget != typeof(Button)
                    || window.Resources["CatalogHudSearchBoxStyle"] is not Style
                    {
                        TargetType: { } hudSearchTarget
                    } hudSearchStyle
                    || hudSearchTarget != typeof(TextBox)
                     || window.FindName("CatalogPage") is not Grid catalogPage
                    || window.FindName("TitleCurrentPlatform") is not TextBlock titleCurrentPlatform
                    || window.FindName("CatalogMetalFrameOverlay") is not Grid catalogMetalFrameOverlay
                    || window.FindName("CatalogMetalOuterFrame") is not Border catalogMetalOuterFrame
                    || window.FindName("CatalogMetalInnerFrame") is not Border catalogMetalInnerFrame
                    || window.FindName("CatalogHudHeader") is not Border catalogHudHeader
                    || window.FindName("CatalogHudInnerFrame") is not Border catalogHudInnerFrame
                    || window.FindName("CatalogHudOpenFolderButton") is not Button hudOpenFolder
                    || window.FindName("CatalogHudSupportButton") is not Button hudSupport
                    || window.FindName("CatalogBottomActions") is not StackPanel catalogBottomActions
                    || window.FindName("CatalogHudChooseInstallButton") is not Button hudChooseInstall
                    || window.FindName("CatalogHudChooseTempButton") is not Button hudChooseTemp
                    || window.FindName("CatalogHudResetTempButton") is not Button hudResetTemp
                    || window.FindName("CatalogHudSearchPanel") is not Border hudSearchPanel
                    || window.FindName("CatalogHudActionStatusPanel") is not Border hudActionStatusPanel
                    || window.FindName("CatalogSearchBox") is not TextBox hudSearchBox
                    || window.FindName("InstallFolderPath") is not TextBlock installPath
                    || window.FindName("TempFolderPath") is not TextBlock tempPath
                    || window.FindName("RetroCarouselInfoPanel") is not Grid retroCarouselInfoPanel
                    || window.FindName("RetroCarouselInfoOuterFrame") is not Border retroCarouselInfoOuterFrame
                    || window.FindName("RetroCarouselInfoInnerFrame") is not Border retroCarouselInfoInnerFrame
                    || window.FindName("RetroCarouselFooter") is not Border retroCarouselFooter
                    || window.FindName("RetroCarouselFooterInnerFrame") is not Border retroCarouselFooterInnerFrame
                    || !ReferenceEquals(hudOpenFolder.Style, hudButtonStyle)
                    || !ReferenceEquals(hudSupport.Style, hudButtonStyle)
                    || !ReferenceEquals(hudChooseInstall.Style, hudButtonStyle)
                    || !ReferenceEquals(hudChooseTemp.Style, hudButtonStyle)
                    || !ReferenceEquals(hudResetTemp.Style, hudButtonStyle)
                    || !ReferenceEquals(hudSearchBox.Style, hudSearchStyle)
                    || catalogHudHeader.Background is not SolidColorBrush
                    || hudSearchPanel.Background is not SolidColorBrush
                    || hudActionStatusPanel.Background is not SolidColorBrush
                    || catalogMetalOuterFrame.Background is not SolidColorBrush
                    || catalogMetalInnerFrame.Background is not SolidColorBrush
                    || catalogHudInnerFrame.Background is not SolidColorBrush
                    || retroCarouselInfoOuterFrame.Background is not SolidColorBrush
                    || retroCarouselInfoInnerFrame.Background is not SolidColorBrush
                    || retroCarouselFooter.Background is not SolidColorBrush
                    || catalogHudHeader.CornerRadius != new CornerRadius(0)
                    || hudSearchPanel.CornerRadius != new CornerRadius(0)
                    || hudActionStatusPanel.CornerRadius != new CornerRadius(0)
                    || catalogPage.RowDefinitions.Count != 2
                    || Math.Abs(catalogHudHeader.Height - 50) > double.Epsilon
                    || Grid.GetRow(catalogHudHeader) != 0
                    || Grid.GetRow(hudSearchPanel) != 0
                    || Grid.GetColumn(catalogBottomActions) != 1
                    || catalogMetalFrameOverlay.IsHitTestVisible
                    || catalogMetalFrameOverlay.Focusable
                    || Panel.GetZIndex(catalogMetalFrameOverlay) != 90
                    || catalogMetalOuterFrame.BorderThickness != new Thickness(2)
                    || catalogMetalInnerFrame.BorderThickness != new Thickness(1)
                    || catalogHudHeader.BorderThickness != new Thickness(2)
                    || catalogHudInnerFrame.BorderThickness != new Thickness(1)
                    || retroCarouselInfoOuterFrame.BorderThickness != new Thickness(2)
                    || retroCarouselInfoInnerFrame.BorderThickness != new Thickness(1)
                    || retroCarouselFooter.BorderThickness != new Thickness(2)
                    || retroCarouselFooterInnerFrame.BorderThickness != new Thickness(1)
                    || titleCurrentPlatform.TextTrimming != TextTrimming.CharacterEllipsis
                    || installPath.Visibility != Visibility.Visible
                    || tempPath.Visibility != Visibility.Visible)
                    throw new InvalidDataException(
                        "O painel precisa manter molduras metálicas no estilo das capas, topo HUD sólido e ações inferiores separadas.");
                VerifyCatalogHudStyles(hudButtonStyle, hudSearchStyle);
                if (window.FindName("SidebarHost") is not Border sidebarHost
                    || window.FindName("HomeNavButton") is not Button homeNavButton
                    || window.FindName("LibraryNavButton") is not Button libraryNavButton
                    || window.FindName("GameManagerNavButton") is not Button gameManagerNavButton
                    || window.FindName("DownloadsNavButton") is not Button downloadsNavButton
                    || window.FindName("SidebarLedLayer") is not Grid sidebarLedLayer
                    || sidebarLedLayer.IsHitTestVisible
                    || sidebarLedLayer.Focusable
                    || sidebarLedLayer.Children.Count != 1
                    || FindVisualDescendants<Control>(sidebarLedLayer).Any()
                    || window.FindName("SidebarLedBar") is not System.Windows.Shapes.Rectangle sidebarLed
                    || Math.Abs(sidebarLed.Width - 2) > double.Epsilon
                    || sidebarLed.HorizontalAlignment != HorizontalAlignment.Left
                    || sidebarLed.VerticalAlignment != VerticalAlignment.Stretch
                    || sidebarLed.CacheMode is not BitmapCache
                    || sidebarLed.Fill is not SolidColorBrush ledCore
                    || ledCore.Color != Color.FromRgb(183, 255, 70)
                    || sidebarLed.Effect is not System.Windows.Media.Effects.DropShadowEffect ledGlow
                    || ledGlow.Color != Color.FromRgb(157, 255, 0)
                    || Math.Abs(ledGlow.BlurRadius - 18) > double.Epsilon
                    || Math.Abs(ledGlow.ShadowDepth - 5) > double.Epsilon
                    || Math.Abs(ledGlow.Direction) > double.Epsilon
                    || Math.Abs(ledGlow.Opacity - .92) > double.Epsilon
                    || window.FindName("SidebarCoverFrame") is not null
                    || window.FindName("SidebarMetalTopRail") is not null
                    || window.FindName("SidebarTopLedBar") is not null
                    || window.FindName("SidebarFrameLeftEnergySegments") is not null)
                    throw new InvalidDataException(
                        "O menu lateral original precisa manter um LED fino com núcleo brilhante e luz projetada para dentro, sem moldura tecnológica.");
                VerifySidebarLedPulse(sidebarLedLayer, sidebarLed, ledGlow);
                if (window.FindName("LicenseConsumerText") is not TextBlock licenseConsumer
                    || licenseConsumer.Text != "Cliente ••••FIER"
                    || licenseConsumer.Text.Contains("TR-WPF-VERIFIER", StringComparison.Ordinal)
                    || window.FindName("LicenseConsumerStatusText") is not TextBlock licenseStatus
                    || licenseStatus.Text != "Licença ativa")
                    throw new InvalidDataException(
                        "O cartão da licença precisa mostrar somente o identificador mascarado e o estado autenticado.");

                var ps3 = window.CatalogCategories.Single(item =>
                    item.Id.Equals("playstation-3", StringComparison.OrdinalIgnoreCase));
                openCatalog.Invoke(window, [ps3]);
                if (titleCurrentPlatform.Text != ps3.DisplayName)
                    throw new InvalidDataException(
                        "A barra superior não apresentou o nome da plataforma selecionada.");
                if (sidebarHost.Background is not SolidColorBrush renderedSidebar
                    || renderedSidebar.Color != Color.FromRgb(5, 7, 5))
                    throw new InvalidDataException(
                        "O menu lateral original precisa permanecer sólido, opaco e praticamente preto.");
                if (window.Resources["CurrentSystemSidebarSelectionBrush"] is not SolidColorBrush selection
                    || selection.Color != Color.FromArgb(28, 81, 184, 223))
                    throw new InvalidDataException(
                        "A seleção lateral precisa usar somente uma cor sólida e discreta.");
                if (window.Resources["TechScrollThumbStyle"] is not Style scrollThumbStyle
                    || scrollThumbStyle.TargetType != typeof(Thumb))
                    throw new InvalidDataException(
                        "A barra de rolagem tecnológica precisa manter o estilo global padrão.");
                if (window.Resources["CurrentSystemVideoOverlayBrush"] is not LinearGradientBrush overlay
                    || overlay.StartPoint != new Point(0, .5)
                    || overlay.EndPoint != new Point(1, .5)
                    || overlay.GradientStops.Count != 3
                    || overlay.GradientStops[0].Color != Color.FromArgb(255, 0, 0, 0)
                    || overlay.GradientStops[0].Offset != 0
                    || overlay.GradientStops[1].Color != Color.FromArgb(255, 0, 0, 0)
                    || overlay.GradientStops[1].Offset != .18
                    || overlay.GradientStops[2].Color != Color.FromArgb(0, 0, 0, 0)
                    || overlay.GradientStops[2].Offset != .28
                    || window.Resources.Values.OfType<GradientBrush>().Count() != 1)
                    throw new InvalidDataException(
                        "Somente o vídeo pode manter degradê: preto até 18% e transparente aos 28%.");

                var requestedThemeColors = new Dictionary<string, Color>(StringComparer.OrdinalIgnoreCase)
                {
                    ["playstation-1"] = Color.FromRgb(227, 229, 231),
                    ["playstation-2"] = Color.FromRgb(111, 161, 239),
                    ["playstation-2-br"] = Color.FromRgb(111, 161, 239),
                    ["sega-saturn"] = Color.FromRgb(96, 140, 231)
                };
                foreach (var requestedTheme in requestedThemeColors)
                {
                    var requestedCategory = window.CatalogCategories.Single(item =>
                        item.Id.Equals(requestedTheme.Key, StringComparison.OrdinalIgnoreCase));
                    openCatalog.Invoke(window, [requestedCategory]);
                    if (window.Resources["CurrentSystemAccentColor"] is not Color actualAccent
                        || actualAccent != requestedTheme.Value)
                        throw new InvalidDataException(
                            $"A paleta de {requestedTheme.Key} não corresponde à capa aprovada.");
                }

                openCatalog.Invoke(window, [category]);
                window.Show();
                window.Dispatcher.Invoke(
                    () => window.UpdateLayout(),
                    DispatcherPriority.ApplicationIdle);
                var categoryTitles = FindVisualDescendants<TextBlock>(window)
                    .Where(item => item.Name == "CategoryTitle")
                    .ToArray();
                if (Math.Abs(sidebarHost.ActualWidth - 252) > .5
                    || sidebarHost.ActualHeight <= 0
                    || window.Resources["CurrentSystemAccentBrush"] is not SolidColorBrush activeAccent
                    || window.Resources["GlobalAccentBrush"] is not SolidColorBrush globalAccent
                    || globalAccent.Color != Color.FromRgb(157, 255, 0)
                    || window.Resources["GlobalBrightBrush"] is not SolidColorBrush globalBright
                    || globalBright.Color != Color.FromRgb(183, 255, 70)
                    || sidebarLed.Fill is not SolidColorBrush ledAccent
                    || ledAccent.Color != globalBright.Color
                    || !sidebarLed.HasAnimatedProperties
                    || !ledGlow.HasAnimatedProperties
                    || homeNavButton.Foreground is not SolidColorBrush homeNavForeground
                    || libraryNavButton.Foreground is not SolidColorBrush libraryNavForeground
                    || gameManagerNavButton.Foreground is not SolidColorBrush gameManagerNavForeground
                    || downloadsNavButton.Foreground is not SolidColorBrush downloadsNavForeground
                    || homeNavForeground.Color != Colors.White
                    || libraryNavForeground.Color != Colors.White
                    || gameManagerNavForeground.Color != Colors.White
                    || downloadsNavForeground.Color != Colors.White
                    || categoryTitles.Length == 0
                    || categoryTitles.Any(item =>
                        item.Foreground is not SolidColorBrush titleBrush
                        || titleBrush.Color != Colors.White))
                    throw new InvalidDataException(
                        "O menu global precisa permanecer no verde padrão e todos os nomes laterais precisam ser brancos.");
                VerifyBuiltInMusicAutoplay(window);
                VerifyAsyncThumbnailBinding(window);
                VerifyResponsiveBackgroundVideo(window);
                VerifyVideoLeaseLifecycle(window);
                VerifyRevocationCancelsStoreOperations(window);
                if (window.IsVisible) window.Close();
                window = null;
                application.Shutdown();
                application = null;
            }
            catch (TargetInvocationException exception) when (exception.InnerException is not null)
            {
                failure = exception.InnerException;
            }
            catch (Exception exception)
            {
                failure = exception;
            }
            finally
            {
                try { window?.Close(); } catch { }
                try { application?.Shutdown(); } catch { }
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        if (!thread.Join(TimeSpan.FromSeconds(60)))
            throw new TimeoutException("O catálogo WPF não concluiu a renderização de teste.");
        if (failure is not null)
            throw new InvalidOperationException(
                "O catálogo WPF falhou ao criar os templates reais.",
                failure);
    }

    private static void VerifySidebarLedPulse(
        Grid sidebarLedLayer,
        System.Windows.Shapes.Rectangle sidebarLed,
        System.Windows.Media.Effects.DropShadowEffect ledGlow)
    {
        if (sidebarLedLayer.Triggers.Count != 2
            || !ReferenceEquals(sidebarLed.Effect, ledGlow))
            throw new InvalidDataException(
                "O LED lateral precisa manter exatamente os ciclos de início e encerramento do pulso.");

        var loadedTriggers = sidebarLedLayer.Triggers
            .OfType<EventTrigger>()
            .Where(trigger => trigger.RoutedEvent == FrameworkElement.LoadedEvent)
            .ToArray();
        var unloadedTriggers = sidebarLedLayer.Triggers
            .OfType<EventTrigger>()
            .Where(trigger => trigger.RoutedEvent == FrameworkElement.UnloadedEvent)
            .ToArray();
        if (loadedTriggers.Length != 1
            || loadedTriggers[0].Actions.Count != 1
            || loadedTriggers[0].Actions[0] is not BeginStoryboard
            {
                Name: "SidebarLedPulse",
                Storyboard: { } pulseStoryboard
            }
            || unloadedTriggers.Length != 1
            || unloadedTriggers[0].Actions.Count != 1
            || unloadedTriggers[0].Actions[0] is not RemoveStoryboard
            {
                BeginStoryboardName: "SidebarLedPulse"
            }
            || !pulseStoryboard.AutoReverse
            || !pulseStoryboard.RepeatBehavior.Equals(RepeatBehavior.Forever)
            || pulseStoryboard.Children.Count != 3)
            throw new InvalidDataException(
                "O LED lateral precisa iniciar e encerrar um único pulso suave, infinito e autorreversível.");

        var animations = pulseStoryboard.Children
            .OfType<DoubleAnimation>()
            .ToArray();
        if (animations.Length != 3)
            throw new InvalidDataException(
                "O pulso do LED precisa animar núcleo, intensidade do halo e alcance da luz.");

        DoubleAnimation RequireAnimation(string targetName, string targetProperty)
        {
            var matches = animations.Where(animation =>
                    Storyboard.GetTargetName(animation).Equals(
                        targetName,
                        StringComparison.Ordinal)
                    && Storyboard.GetTargetProperty(animation)?.Path.Equals(
                        targetProperty,
                        StringComparison.Ordinal) == true)
                .ToArray();
            if (matches.Length != 1)
                throw new InvalidDataException(
                    $"A animação {targetName}.{targetProperty} precisa existir exatamente uma vez.");
            var animation = matches[0];
            if (!animation.Duration.HasTimeSpan
                || animation.Duration.TimeSpan != TimeSpan.FromSeconds(1)
                || animation.EasingFunction is not SineEase
                {
                    EasingMode: EasingMode.EaseInOut
                })
                throw new InvalidDataException(
                    $"A animação {targetName}.{targetProperty} precisa pulsar suavemente em um segundo.");
            return animation;
        }

        var coreOpacity = RequireAnimation("SidebarLedBar", "Opacity");
        var glowOpacity = RequireAnimation("SidebarLedGlow", "Opacity");
        var glowRadius = RequireAnimation("SidebarLedGlow", "BlurRadius");
        if (coreOpacity.From != .35
            || coreOpacity.To != 1
            || glowOpacity.From != .20
            || glowOpacity.To != .98
            || glowRadius.From != 6
            || glowRadius.To != 24)
            throw new InvalidDataException(
                "A amplitude do pulso precisa tornar visíveis o brilho e a luz refletida no menu.");
    }

    private static void VerifyCatalogHudStyles(Style buttonStyle, Style searchStyle)
    {
        var buttonTemplate = buttonStyle.Setters
                                 .OfType<Setter>()
                                 .SingleOrDefault(setter => setter.Property == Button.TemplateProperty)
                                 ?.Value as ControlTemplate
                             ?? throw new InvalidDataException(
                                 "O botão HUD precisa possuir template próprio.");
        var searchTemplate = searchStyle.Setters
                                 .OfType<Setter>()
                                 .SingleOrDefault(setter => setter.Property == TextBox.TemplateProperty)
                                 ?.Value as ControlTemplate
                             ?? throw new InvalidDataException(
                                 "A pesquisa HUD precisa possuir template próprio.");
        if (!buttonTemplate.Triggers.OfType<Trigger>().Any(trigger =>
                trigger.Property == UIElement.IsKeyboardFocusedProperty
                && trigger.Value is true)
            || !searchTemplate.Triggers.OfType<Trigger>().Any(trigger =>
                trigger.Property == UIElement.IsKeyboardFocusedProperty
                && trigger.Value is true))
            throw new InvalidDataException(
                "Botões e pesquisa do HUD precisam indicar foco de teclado explicitamente.");
    }

    private static void VerifyExplorerStartInfo()
    {
        var directory = Path.GetFullPath(Path.Combine(
            Path.GetTempPath(),
            "Turborama Explorer argument with spaces"));
        var startInfo = StoreWindow.CreateExplorerStartInfo(directory);
        var expectedExplorer = Path.GetFullPath(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            "explorer.exe"));
        if (!Path.IsPathFullyQualified(startInfo.FileName)
            || !string.Equals(
                startInfo.FileName,
                expectedExplorer,
                StringComparison.OrdinalIgnoreCase)
            || startInfo.UseShellExecute
            || startInfo.ArgumentList.Count != 1
            || !string.Equals(
                startInfo.ArgumentList[0],
                directory,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                "O Explorer precisa usar o executável absoluto do Windows, " +
                "UseShellExecute=false e exatamente um argumento canônico.");
        }
    }

    private static void VerifyWindowBoundsClamp()
    {
        var workArea = new Rect(100, 50, 900, 480);
        var oversized = StoreWindow.ClampWindowBoundsToWorkArea(
            new Rect(-500, -400, 1080, 680),
            workArea,
            new Size(900, 480));
        AssertRectEquals(workArea, oversized, "janela maior que a work area");

        var partiallyOutside = StoreWindow.ClampWindowBoundsToWorkArea(
            new Rect(850, 450, 400, 300),
            workArea,
            new Size(300, 200));
        AssertRectEquals(
            new Rect(600, 230, 400, 300),
            partiallyOutside,
            "janela parcialmente fora da work area");

        var minimumEnforced = StoreWindow.ClampWindowBoundsToWorkArea(
            new Rect(120, 70, 200, 100),
            workArea,
            new Size(900, 480));
        AssertRectEquals(workArea, minimumEnforced, "tamanho mínimo lógico");

        var smallWorkArea = StoreWindow.ClampWindowBoundsToWorkArea(
            new Rect(0, 0, 900, 480),
            new Rect(20, 30, 800, 420),
            new Size(900, 480));
        AssertRectEquals(
            new Rect(20, 30, 800, 420),
            smallWorkArea,
            "work area menor que o tamanho mínimo");
    }

    private static void AssertRectEquals(Rect expected, Rect actual, string scenario)
    {
        const double tolerance = 0.001;
        if (Math.Abs(expected.X - actual.X) > tolerance
            || Math.Abs(expected.Y - actual.Y) > tolerance
            || Math.Abs(expected.Width - actual.Width) > tolerance
            || Math.Abs(expected.Height - actual.Height) > tolerance)
        {
            throw new InvalidDataException(
                $"O clamp falhou em '{scenario}': esperado={expected}, atual={actual}.");
        }
    }

    private static void VerifyPathIdentityLeaseAdversaries()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "Turborama-PathIdentity-" + Guid.NewGuid().ToString("N"));
        var external = root + "-external";
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(external);
        try
        {
            var levelA = Path.Combine(root, "a");
            var levelB = Path.Combine(levelA, "b");
            var levelC = Path.Combine(levelB, "c");
            Directory.CreateDirectory(levelC);
            using (var lease = TurboBoxManager.PathIdentity.OpenDirectoryTree(levelC))
            {
                foreach (var ancestor in new[] { levelC, levelB, levelA, root })
                {
                    var moved = ancestor + "-moved";
                    try
                    {
                        Directory.Move(ancestor, moved);
                        Directory.Move(moved, ancestor);
                        throw new InvalidDataException(
                            $"O lease permitiu renomear o ancestral ativo '{ancestor}'.");
                    }
                    catch (Exception exception) when (exception is IOException
                                                       or UnauthorizedAccessException)
                    {
                    }
                }
                lease.Revalidate();
            }

            var movedRoot = root + "-moved";
            Directory.Move(root, movedRoot);
            Directory.Move(movedRoot, root);

            var sentinel = Path.Combine(external, "sentinel.bin");
            var hostileLink = Path.Combine(levelC, "hostile.bin");
            File.WriteAllText(sentinel, "sentinel", Encoding.UTF8);
            if (!CreateNativeHardLink(
                    ToExtendedTestPath(hostileLink),
                    ToExtendedTestPath(sentinel),
                    IntPtr.Zero))
            {
                throw new Win32Exception(
                    Marshal.GetLastPInvokeError(),
                    "Não foi possível criar o hardlink adversarial.");
            }
            var hardlinkRejected = false;
            using (var lease = TurboBoxManager.PathIdentity.OpenDirectoryTree(levelC))
            {
                try
                {
                    using var forbidden = lease.OpenFile(
                        hostileLink,
                        FileMode.Open,
                        FileAccess.Read,
                        FileShare.Read,
                        1,
                        FileOptions.None);
                }
                catch (InvalidDataException)
                {
                    hardlinkRejected = true;
                }
            }
            if (!hardlinkRejected)
                throw new InvalidDataException("O helper aceitou uma folha com hardlink.");
            var hardlinkDeleteRejected = false;
            try
            {
                _ = TurboBoxManager.PathIdentity.DeleteFileExact(hostileLink, root);
            }
            catch (InvalidDataException)
            {
                hardlinkDeleteRejected = true;
            }
            if (!hardlinkDeleteRejected)
                throw new InvalidDataException("A exclusão exata aceitou um hardlink hostil.");
            if (File.ReadAllText(sentinel, Encoding.UTF8) != "sentinel")
                throw new InvalidDataException("A exclusão hostil alterou o alvo externo.");
            File.Delete(hostileLink);

            var ordinary = Path.Combine(levelC, "ordinary.bin");
            File.WriteAllText(ordinary, "delete-exact", Encoding.UTF8);
            if (!TurboBoxManager.PathIdentity.DeleteFileExact(ordinary, root)
                || File.Exists(ordinary))
                throw new InvalidDataException("A exclusão por handle não removeu o objeto exato.");

            var junctionTarget = Path.Combine(external, "junction-target");
            var junction = Path.Combine(levelA, "junction");
            Directory.CreateDirectory(junctionTarget);
            File.WriteAllText(Path.Combine(junctionTarget, "outside.txt"), "outside", Encoding.UTF8);
            CreateDirectoryJunction(junction, junctionTarget);
            var junctionRejected = false;
            try
            {
                using var forbidden = TurboBoxManager.PathIdentity.OpenDirectoryTree(
                    Path.Combine(junction, "child"),
                    createIfMissing: true);
            }
            catch (InvalidDataException)
            {
                junctionRejected = true;
            }
            if (!junctionRejected)
                throw new InvalidDataException("O helper atravessou um junction hostil.");
            if (File.ReadAllText(
                    Path.Combine(junctionTarget, "outside.txt"),
                    Encoding.UTF8) != "outside")
                throw new InvalidDataException("O teste de junction alterou o alvo externo.");
            Directory.Delete(junction);

            var longDirectory = Path.Combine(
                levelC,
                new string('x', 90),
                new string('y', 90),
                new string('z', 90));
            using (var longLease = TurboBoxManager.PathIdentity.OpenDirectoryTree(
                       longDirectory,
                       createIfMissing: true))
            {
                var longFile = Path.Combine(longDirectory, "long.bin");
                using var output = longLease.OpenFile(
                    longFile,
                    FileMode.CreateNew,
                    FileAccess.ReadWrite,
                    FileShare.Read,
                    1,
                    FileOptions.WriteThrough);
                output.WriteByte(0x5A);
                output.Flush(flushToDisk: true);
                _ = TurboBoxManager.PathIdentity.CaptureFileIdentity(
                    output.SafeFileHandle,
                    longFile);
            }

            if (TurboBoxManager.PathIdentity.OutstandingDirectoryHandles != 0)
                throw new InvalidDataException(
                    "O teste adversarial deixou handles de identidade ativos.");
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
            if (Directory.Exists(external)) Directory.Delete(external, recursive: true);
        }
    }

    private static void VerifyAsyncThumbnailBinding(StoreWindow window)
    {
        if (window.CatalogItems.Take(2).ToArray() is not { Length: 2 } items
            || window.FindName("RetroUniversalVideoPlayerHost") is not Grid host)
        {
            throw new InvalidDataException(
                "O teste assíncrono de capas precisa de duas capas e um host WPF.");
        }

        using var releaseSlowGetter = new ManualResetEventSlim();
        var slow = new AsyncThumbnailProbe(items[0].ImageSource, releaseSlowGetter);
        var current = new AsyncThumbnailProbe(items[1].ImageSource, release: null);
        var image = new Image();
        var heartbeatCount = 0;
        var heartbeat = new DispatcherTimer(
            TimeSpan.FromMilliseconds(1),
            DispatcherPriority.Send,
            (_, _) => heartbeatCount++,
            window.Dispatcher);
        host.Children.Add(image);
        heartbeat.Start();
        try
        {
            image.DataContext = slow;
            BindingOperations.SetBinding(
                image,
                Image.SourceProperty,
                new Binding(nameof(AsyncThumbnailProbe.Thumbnail))
                {
                    IsAsync = true,
                    Mode = BindingMode.OneWay
                });
            WaitForDispatcherTask(window, slow.Started.Task, "início assíncrono da capa antiga");

            image.DataContext = current;
            WaitForDispatcherTask(window, current.Completed.Task, "decode assíncrono da capa atual");
            WaitForDispatcherCondition(
                window,
                () => IsBitmapFromSource(image.Source, current.Source),
                "aplicação da capa atual");

            releaseSlowGetter.Set();
            WaitForDispatcherTask(window, slow.Completed.Task, "término da capa obsoleta");
            window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);

            if (!IsBitmapFromSource(image.Source, current.Source))
                throw new InvalidDataException(
                    "O resultado obsoleto de capa sobrescreveu a navegação mais recente.");
            if (image.Source is not BitmapSource { IsFrozen: true })
                throw new InvalidDataException(
                    "A capa decodificada no worker não foi congelada para uso cross-thread.");
            if (slow.GetterThreadId == Environment.CurrentManagedThreadId
                || current.GetterThreadId == Environment.CurrentManagedThreadId)
            {
                throw new InvalidDataException(
                    "Binding IsAsync executou o loader de capa no dispatcher.");
            }
            if (heartbeatCount == 0)
                throw new InvalidDataException(
                    "O dispatcher não processou heartbeat durante o decode de capas.");

            var concurrentLoads = Task.WhenAll(Enumerable.Range(0, 16).Select(_ =>
                Task.Run(() => CatalogThumbnailLoader.Load(items[0].ImageSource, 777))));
            WaitForDispatcherTask(
                window,
                concurrentLoads,
                "deduplicação concorrente do decode de capas");
            var thumbnails = concurrentLoads.GetAwaiter().GetResult();
            if (thumbnails[0] is not { IsFrozen: true } firstThumbnail
                || thumbnails.Any(thumbnail =>
                    thumbnail is null || !ReferenceEquals(thumbnail, firstThumbnail)))
            {
                throw new InvalidDataException(
                    "O cache concorrente não deduplicou uma geração de miniatura congelada.");
            }
        }
        finally
        {
            releaseSlowGetter.Set();
            heartbeat.Stop();
            BindingOperations.ClearBinding(image, Image.SourceProperty);
            host.Children.Remove(image);
        }
    }

    private static bool IsBitmapFromSource(ImageSource? imageSource, string expectedSource)
    {
        if (imageSource is not BitmapImage { UriSource: { } actualUri }) return false;
        var expectedUri = new Uri(expectedSource, UriKind.Absolute);
        return actualUri.Equals(expectedUri);
    }

    private static void WaitForDispatcherCondition(
        StoreWindow window,
        Func<bool> condition,
        string scenario)
    {
        if (condition()) return;
        var frame = new DispatcherFrame();
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
        var timer = new DispatcherTimer(
            TimeSpan.FromMilliseconds(2),
            DispatcherPriority.Send,
            (_, _) =>
            {
                if (condition() || DateTime.UtcNow >= deadline)
                    frame.Continue = false;
            },
            window.Dispatcher);
        timer.Start();
        try
        {
            Dispatcher.PushFrame(frame);
        }
        finally
        {
            timer.Stop();
        }
        if (!condition())
            throw new TimeoutException($"Timeout aguardando {scenario}.");
    }

    private sealed class AsyncThumbnailProbe(
        string source,
        ManualResetEventSlim? release)
    {
        public string Source { get; } = source;
        public TaskCompletionSource Started { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Completed { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int GetterThreadId { get; private set; }

        public BitmapSource? Thumbnail
        {
            get
            {
                GetterThreadId = Environment.CurrentManagedThreadId;
                Started.TrySetResult();
                try
                {
                    release?.Wait();
                    return CatalogThumbnailLoader.Load(Source, 384);
                }
                finally
                {
                    Completed.TrySetResult();
                }
            }
        }
    }

    private static void VerifyArtifactPolicyDivergenceFailsClosed()
    {
        var unavailable = new CatalogItem { Id = "unavailable", Extract = true };
        if (unavailable.CanDownload
            || !unavailable.DownloadActionLabel.Equals("INDISPONÍVEL", StringComparison.Ordinal))
            throw new InvalidDataException(
                "Um card sem artefato autorizado deveria ficar desabilitado e rotulado INDISPONÍVEL.");

        var extractArtifact = CreateArtifact(CatalogExtractPolicy.ExtractArchive);
        var visualSaysNo = new CatalogItem
        {
            Id = "visual-none",
            Extract = false,
            Artifact = extractArtifact
        };
        var noneArtifact = CreateArtifact(CatalogExtractPolicy.None);
        var visualSaysExtract = new CatalogItem
        {
            Id = "visual-extract",
            Extract = true,
            Artifact = noneArtifact
        };
        if (!visualSaysNo.HasExtractPolicyConflict
            || visualSaysNo.CanDownload
            || !visualSaysExtract.HasExtractPolicyConflict
            || visualSaysExtract.CanDownload)
            throw new InvalidDataException(
                "Uma divergência entre o catálogo visual e ExtractPolicy deveria falhar fechada.");

        var matching = new CatalogItem
        {
            Id = "matching",
            Extract = true,
            Artifact = extractArtifact
        };
        if (!matching.HasAuthorizedArtifact || !matching.CanDownload)
            throw new InvalidDataException(
                "Uma política assinada coerente deveria permanecer elegível.");
    }

    private static CatalogArtifactDescriptor CreateArtifact(CatalogExtractPolicy policy) => new()
    {
        ArtifactId = new string('1', 32),
        ArtifactVersion = 1,
        ContentLength = 1,
        Sha256 = new string('2', 64),
        SafeFileName = "artifact.zip",
        FileExtension = ".zip",
        ExtractPolicy = policy,
        ManifestIdentity = new string('3', 64)
    };

    private static void VerifyBackgroundVideoManifestAndRouting()
    {
        var expectedFiles = new HashSet<string>(StringComparer.Ordinal)
        {
            "Turborama-background-system-tools.mp4",
            "Turborama-background-playstation.mp4",
            "Turborama-background-ps2.mp4",
            "Turborama-background-ps4.mp4",
            "Turborama-background-ps5.mp4",
            "Turborama-background-psp.mp4",
            "Turborama-background-ps-vita.mp4",
            "Turborama-background-sega-saturn.mp4",
            "Turborama-background-xbox-one-x.mp4",
            "Turborama-background-nintendo-generic.mp4",
            "Turborama-background-nintendo-switch.mp4",
            "Turborama-background-nintendo-wii.mp4",
            "Turborama-background-windows.mp4",
            "Turborama-background-retro.mp4",
            "Turborama-background.mp4"
        };
        using var manifestStream = typeof(StoreWindow).Assembly.GetManifestResourceStream(
                                       "Turborama.BackgroundVideoIntegrity.json")
                                   ?? throw new InvalidDataException(
                                       "O manifesto incorporado dos vídeos de fundo está ausente.");
        using var document = JsonDocument.Parse(
            manifestStream,
            new JsonDocumentOptions { MaxDepth = 4 });
        var properties = document.RootElement.EnumerateObject().ToArray();
        var actualFiles = properties.Select(property => property.Name)
            .ToHashSet(StringComparer.Ordinal);
        if (properties.Length != 15 || !actualFiles.SetEquals(expectedFiles))
            throw new InvalidDataException(
                "O manifesto incorporado deve conter exatamente os 15 vídeos de fundo aprovados.");

        var root = Path.Combine(AppContext.BaseDirectory, "Assets", "BackgroundVideos");
        foreach (var property in properties)
        {
            var expectedLength = property.Value.GetProperty("length").GetInt64();
            var expectedSha256 = property.Value.GetProperty("sha256").GetString();
            var path = Path.Combine(root, property.Name);
            var info = new FileInfo(path);
            if (!info.Exists || info.Length != expectedLength)
                throw new InvalidDataException(
                    $"O vídeo de fundo '{property.Name}' está ausente ou com tamanho divergente.");
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            var actualSha256 = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
            if (!actualSha256.Equals(expectedSha256, StringComparison.Ordinal))
                throw new InvalidDataException(
                    $"O vídeo de fundo '{property.Name}' não corresponde ao SHA-256 incorporado.");
        }

        var resolver = typeof(StoreWindow).GetMethod(
                           "ResolveRetroUniversalVideoFileName",
                           BindingFlags.Static | BindingFlags.NonPublic)
                       ?? throw new MissingMethodException(
                           nameof(StoreWindow),
                           "ResolveRetroUniversalVideoFileName");
        var routes = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["system-tools"] = "Turborama-background-system-tools.mp4",
            ["playstation-1"] = "Turborama-background-playstation.mp4",
            ["playstation-2"] = "Turborama-background-ps2.mp4",
            ["playstation-2-br"] = "Turborama-background-ps2.mp4",
            ["playstation-4"] = "Turborama-background-ps4.mp4",
            ["playstation-5"] = "Turborama-background-ps5.mp4",
            ["psp"] = "Turborama-background-psp.mp4",
            ["ps-vita"] = "Turborama-background-ps-vita.mp4",
            ["sega-saturn"] = "Turborama-background-sega-saturn.mp4",
            ["xbox"] = "Turborama-background-xbox-one-x.mp4",
            ["xbox-360"] = "Turborama-background-xbox-one-x.mp4",
            ["xbox-one"] = "Turborama-background-xbox-one-x.mp4",
            ["xbox-series"] = "Turborama-background-xbox-one-x.mp4",
            ["nintendo-3ds"] = "Turborama-background-nintendo-generic.mp4",
            ["gamecube"] = "Turborama-background-nintendo-generic.mp4",
            ["nintendo-switch"] = "Turborama-background-nintendo-switch.mp4",
            ["nintendo-wii"] = "Turborama-background-nintendo-wii.mp4",
            ["nintendo-wii-u"] = "Turborama-background-nintendo-wii.mp4",
            ["windows"] = "Turborama-background-windows.mp4",
            ["retro-games"] = "Turborama-background-retro.mp4",
            ["playstation-3"] = "Turborama-background.mp4",
            ["unknown-fallback"] = "Turborama-background.mp4"
        };
        foreach (var (categoryId, expectedFile) in routes)
        {
            var resolved = resolver.Invoke(null, [categoryId]) as string;
            if (resolved is null || !resolved.Equals(expectedFile, StringComparison.Ordinal))
                throw new InvalidDataException(
                    $"A categoria '{categoryId}' não resolveu o vídeo aprovado '{expectedFile}'.");
        }

        var nullFallback = resolver.Invoke(null, [null]) as string;
        if (nullFallback is null || !nullFallback.Equals(
                "Turborama-background.mp4",
                StringComparison.Ordinal))
            throw new InvalidDataException("O fallback nulo não resolveu o vídeo universal aprovado.");

        VerifyBackgroundVideoLeaseRejectsTampering(root);
    }

    private static void VerifyBackgroundVideoLeaseRejectsTampering(string videoRoot)
    {
        const string fileName = "Turborama-background-retro.mp4";
        var sourcePath = Path.Combine(videoRoot, fileName);
        var temporaryRoot = Path.Combine(
            Path.GetTempPath(),
            $"Turborama-VideoLease-{Guid.NewGuid():N}");
        var movedRoot = temporaryRoot + "-moved";
        var reparseTargetRoot = temporaryRoot + "-target";
        var reparseRoot = temporaryRoot + "-reparse";
        Directory.CreateDirectory(temporaryRoot);
        var candidatePath = Path.Combine(temporaryRoot, fileName);
        File.Copy(sourcePath, candidatePath);

        var leaseFactory = typeof(StoreWindow).GetMethod(
                               "OpenTrustedBackgroundVideoLease",
                               BindingFlags.Static | BindingFlags.NonPublic)
                           ?? throw new MissingMethodException(
                               nameof(StoreWindow),
                               "OpenTrustedBackgroundVideoLease");
        try
        {
            var validLease = leaseFactory.Invoke(null, [candidatePath, fileName]) as IDisposable
                             ?? throw new InvalidDataException(
                                 "O vídeo íntegro não recebeu um lease de identidade.");
            try
            {
                using (new FileStream(
                           candidatePath,
                           FileMode.Open,
                           FileAccess.Read,
                           FileShare.Read))
                {
                }

                try
                {
                    using var forbiddenWriter = new FileStream(
                        candidatePath,
                        FileMode.Open,
                        FileAccess.Write,
                        FileShare.ReadWrite | FileShare.Delete);
                    throw new InvalidDataException(
                        "O lease de vídeo permitiu escrita ou substituição durante a reprodução.");
                }
                catch (IOException)
                {
                }

                try
                {
                    Directory.Move(temporaryRoot, movedRoot);
                    Directory.Move(movedRoot, temporaryRoot);
                    throw new InvalidDataException(
                        "O lease de vídeo permitiu renomear um ancestral do caminho ativo.");
                }
                catch (IOException)
                {
                }
                catch (UnauthorizedAccessException)
                {
                }
            }
            finally
            {
                validLease.Dispose();
            }

            Directory.Move(temporaryRoot, movedRoot);
            Directory.Move(movedRoot, temporaryRoot);

            using (var tamper = new FileStream(
                       candidatePath,
                       FileMode.Open,
                       FileAccess.ReadWrite,
                       FileShare.None))
            {
                tamper.Position = 12;
                var original = tamper.ReadByte();
                if (original < 0)
                    throw new InvalidDataException("O vídeo de controle é curto demais para adulteração.");
                tamper.Position = 12;
                tamper.WriteByte((byte)(original ^ 0x5A));
                tamper.Flush(flushToDisk: true);
            }

            if (leaseFactory.Invoke(null, [candidatePath, fileName]) is IDisposable forgedLease)
            {
                forgedLease.Dispose();
                throw new InvalidDataException(
                    "O resolver aceitou um vídeo adulterado depois de uma validação anterior.");
            }

            Directory.CreateDirectory(reparseTargetRoot);
            File.Copy(sourcePath, Path.Combine(reparseTargetRoot, fileName));
            CreateDirectoryJunction(reparseRoot, reparseTargetRoot);
            if (leaseFactory.Invoke(
                    null,
                    [Path.Combine(reparseRoot, fileName), fileName]) is IDisposable reparseLease)
            {
                reparseLease.Dispose();
                throw new InvalidDataException(
                    "O resolver aceitou um vídeo alcançado por reparse point.");
            }
        }
        finally
        {
            try { Directory.Delete(reparseRoot); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
            try { File.Delete(Path.Combine(reparseTargetRoot, fileName)); } catch (IOException) { }
            try { Directory.Delete(reparseTargetRoot); } catch (IOException) { }
            try { File.Delete(Path.Combine(movedRoot, fileName)); } catch (IOException) { }
            try { Directory.Delete(movedRoot); } catch (IOException) { }
            try { File.Delete(candidatePath); } catch (IOException) { }
            try { Directory.Delete(temporaryRoot); } catch (IOException) { }
        }
    }

    private static void CreateDirectoryJunction(string junctionPath, string targetPath)
    {
        Directory.CreateDirectory(junctionPath);
        var canonicalTarget = Path.TrimEndingDirectorySeparator(Path.GetFullPath(targetPath));
        var substituteName = @"\??\" + canonicalTarget;
        var substituteBytes = Encoding.Unicode.GetBytes(substituteName);
        var printBytes = Encoding.Unicode.GetBytes(canonicalTarget);
        var pathBufferLength = checked(substituteBytes.Length + sizeof(char) + printBytes.Length + sizeof(char));
        var reparseDataLength = checked((ushort)(8 + pathBufferLength));
        var buffer = new byte[8 + reparseDataLength];
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(0, 4), NativeIoReparseTagMountPoint);
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(4, 2), reparseDataLength);
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(8, 2), 0);
        BinaryPrimitives.WriteUInt16LittleEndian(
            buffer.AsSpan(10, 2),
            checked((ushort)substituteBytes.Length));
        BinaryPrimitives.WriteUInt16LittleEndian(
            buffer.AsSpan(12, 2),
            checked((ushort)(substituteBytes.Length + sizeof(char))));
        BinaryPrimitives.WriteUInt16LittleEndian(
            buffer.AsSpan(14, 2),
            checked((ushort)printBytes.Length));
        substituteBytes.CopyTo(buffer, 16);
        printBytes.CopyTo(buffer, 16 + substituteBytes.Length + sizeof(char));

        using var handle = OpenNativeReparseHandle(
            ToExtendedTestPath(junctionPath),
            NativeGenericWrite,
            shareMode: 0,
            IntPtr.Zero,
            NativeOpenExisting,
            NativeFileFlagBackupSemantics | NativeFileFlagOpenReparsePoint,
            IntPtr.Zero);
        if (handle.IsInvalid)
        {
            throw new Win32Exception(
                Marshal.GetLastPInvokeError(),
                "Não foi possível abrir o diretório de controle para criar um junction.");
        }

        var nativeBuffer = Marshal.AllocHGlobal(buffer.Length);
        try
        {
            Marshal.Copy(buffer, 0, nativeBuffer, buffer.Length);
            if (SetNativeReparsePoint(
                    handle,
                    NativeFsctlSetReparsePoint,
                    nativeBuffer,
                    (uint)buffer.Length,
                    IntPtr.Zero,
                    outputBufferSize: 0,
                    out _,
                    IntPtr.Zero) == 0)
            {
                throw new Win32Exception(
                    Marshal.GetLastPInvokeError(),
                    "Não foi possível criar o junction de controle do lease de vídeo.");
            }
        }
        finally
        {
            Marshal.FreeHGlobal(nativeBuffer);
        }
    }

    private static string ToExtendedTestPath(string path)
    {
        var canonicalPath = Path.GetFullPath(path);
        if (canonicalPath.StartsWith(@"\\?\", StringComparison.Ordinal))
            return canonicalPath;
        return canonicalPath.StartsWith(@"\\", StringComparison.Ordinal)
            ? @"\\?\UNC\" + canonicalPath[2..]
            : @"\\?\" + canonicalPath;
    }

    private static StoreWindow CreateAuthorizedWindowForRendering()
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var response = new SuiteSessionResponse(
            SuiteOnlineLicenseProtocol.SchemaVersion,
            SuiteOnlineLicenseProtocol.ProductId,
            "TR-WPF-VERIFIER",
            new string('4', 64),
            new string('1', 64),
            "ACTIVE",
            now,
            now + 300,
            60);

        var licensingAssembly = typeof(AuthorizedStoreContext).Assembly;
        var stateType = licensingAssembly.GetType(
                            "TurboBoxManager.Licensing.SuiteAuthorizationState",
                            throwOnError: true)
                        ?? throw new TypeLoadException("SuiteAuthorizationState ausente.");
        var state = Activator.CreateInstance(
                        stateType,
                        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                        binder: null,
                        args: [TimeProvider.System, response],
                        culture: null)
                    ?? throw new InvalidOperationException(
                        "Não foi possível criar o estado autorizado do verificador.");
        var context = Activator.CreateInstance(
                          typeof(AuthorizedStoreContext),
                          BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                          binder: null,
                          args: [state],
                          culture: null) as AuthorizedStoreContext
                      ?? throw new InvalidOperationException(
                          "Não foi possível criar a capacidade do verificador.");

        var runtime = CreateAvailableTestRuntime(licensingAssembly);
        var currentContextField = typeof(SuiteLicensingRuntime).GetField(
                                      "_currentContext",
                                      BindingFlags.Instance | BindingFlags.NonPublic)
                                  ?? throw new MissingFieldException(
                                      nameof(SuiteLicensingRuntime),
                                      "_currentContext");
        currentContextField.SetValue(runtime, context);

        try
        {
            return Activator.CreateInstance(
                       typeof(StoreWindow),
                       BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                       binder: null,
                       args: [context, runtime],
                       culture: null) as StoreWindow
                   ?? throw new InvalidOperationException(
                       "Não foi possível criar a janela autorizada do verificador.");
        }
        catch
        {
            runtime.DisposeAsync().AsTask().GetAwaiter().GetResult();
            throw;
        }
    }

    private static SuiteLicensingRuntime CreateAvailableTestRuntime(Assembly licensingAssembly)
    {
        using var onlineAssertionKey = RSA.Create(2048);
        var onlineAssertionSpki = onlineAssertionKey.ExportSubjectPublicKeyInfo();
        SuiteAuthorityConfiguration authority;
        try
        {
            authority = new SuiteAuthorityConfiguration(
                new Uri("https://licensing.invalid/", UriKind.Absolute),
                SuiteIdentityPolicy.SoftwareOnly,
                new string('a', 64),
                SuiteAuthorityConfigurationVerifier.KeyIdFromSpki(onlineAssertionSpki),
                onlineAssertionSpki,
                new string('b', 64),
                DateTimeOffset.UtcNow.AddMinutes(-1),
                DateTimeOffset.UtcNow.AddHours(1));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(onlineAssertionSpki);
        }
        var identityType = licensingAssembly.GetType(
                               "TurboBoxManager.Licensing.SuiteCngMachineIdentity",
                               throwOnError: true)
                           ?? throw new TypeLoadException("SuiteCngMachineIdentity ausente.");
        var identity = Activator.CreateInstance(
                           identityType,
                           BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                           binder: null,
                           args: [SuiteIdentityPolicy.SoftwareOnly],
                           culture: null)
                       ?? throw new InvalidOperationException(
                           "Não foi possível criar a identidade isolada do verificador.");
        var clientType = licensingAssembly.GetType(
                             "TurboBoxManager.Licensing.SuiteLicenseClient",
                             throwOnError: true)
                         ?? throw new TypeLoadException("SuiteLicenseClient ausente.");
        var client = Activator.CreateInstance(
                         clientType,
                         BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                         binder: null,
                         args: [authority, identity, null],
                         culture: null)
                     ?? throw new InvalidOperationException(
                         "Não foi possível criar o cliente isolado do verificador.");
        return Activator.CreateInstance(
                   typeof(SuiteLicensingRuntime),
                   BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                   binder: null,
                   args: [client, authority, TimeProvider.System],
                   culture: null) as SuiteLicensingRuntime
               ?? throw new InvalidOperationException(
                   "Não foi possível criar o runtime isolado do verificador.");
    }

    private static void VerifyRevocationCancelsStoreOperations(StoreWindow window)
    {
        var authorization = typeof(StoreWindow).GetField(
                                "_authorization",
                                BindingFlags.Instance | BindingFlags.NonPublic)
                            ?.GetValue(window) as AuthorizedStoreContext
                            ?? throw new MissingFieldException(nameof(StoreWindow), "_authorization");
        var state = typeof(AuthorizedStoreContext).GetProperty(
                        "StateForRuntime",
                        BindingFlags.Instance | BindingFlags.NonPublic)
                    ?.GetValue(authorization)
                    ?? throw new MissingMemberException(
                        nameof(AuthorizedStoreContext),
                        "StateForRuntime");
        var runtime = typeof(StoreWindow).GetField(
                          "_licensingRuntime",
                          BindingFlags.Instance | BindingFlags.NonPublic)
                      ?.GetValue(window) as SuiteLicensingRuntime
                      ?? throw new MissingFieldException(nameof(StoreWindow), "_licensingRuntime");
        var revoke = typeof(SuiteLicensingRuntime).GetMethod(
                         "Revoke",
                         BindingFlags.Instance | BindingFlags.NonPublic)
                     ?? throw new MissingMethodException(
                         nameof(SuiteLicensingRuntime),
                         "Revoke");
        var existingWindows = Application.Current.Windows.Cast<Window>().ToHashSet();
        revoke.Invoke(runtime, [state, "WPF_TEST_REVOKED"]);
        if (!SpinWait.SpinUntil(
                () => authorization.AuthorizationCancellationToken.IsCancellationRequested,
                TimeSpan.FromSeconds(2)))
            throw new InvalidDataException(
                "A revogação não cancelou o token sticky da capacidade autorizada.");

        var operationCancellation = typeof(StoreWindow).GetField(
                                        "_storeOperationCancellation",
                                        BindingFlags.Instance | BindingFlags.NonPublic)
                                    ?.GetValue(window) as CancellationTokenSource
                                    ?? throw new MissingFieldException(
                                        nameof(StoreWindow),
                                        "_storeOperationCancellation");
        if (!operationCancellation.IsCancellationRequested)
            throw new InvalidDataException(
                "A revogação não alcançou a fila/download/extração da loja.");

        var authorizationGuard = typeof(StoreWindow).GetMethod(
                                     "ThrowIfOperationUnauthorized",
                                     BindingFlags.Instance | BindingFlags.NonPublic)
                                 ?? throw new MissingMethodException(
                                     nameof(StoreWindow),
                                     "ThrowIfOperationUnauthorized");
        try
        {
            authorizationGuard.Invoke(window, null);
            throw new InvalidDataException(
                "Uma operação privilegiada continuou depois da revogação.");
        }
        catch (TargetInvocationException exception) when (
            exception.InnerException is OperationCanceledException or SuiteAuthorizationException)
        {
        }

        PremiumLoginWindow? login = null;
        try
        {
            window.Dispatcher.Invoke(
                () => window.UpdateLayout(),
                DispatcherPriority.ApplicationIdle);
            login = Application.Current.Windows
                .OfType<PremiumLoginWindow>()
                .SingleOrDefault(candidate => !existingWindows.Contains(candidate));
            if (window.IsEnabled
                || window.IsVisible
                || window.FindName("SessionStatusText") is not TextBlock status
                || !status.Text.Equals("SESSÃO ENCERRADA", StringComparison.Ordinal)
                || login is not { IsVisible: true })
                throw new InvalidDataException(
                    "O callback de revogação não desabilitou/fechou a loja e não abriu o login.");
        }
        finally
        {
            login?.Close();
        }
    }

    private static void VerifyVideoLeaseLifecycle(StoreWindow window)
    {
        if (window.FindName("RetroUniversalVideoPlayerHost") is not Grid universalHost
            || window.FindName("RetroSystemVideoPlayerHost") is not Grid systemHost)
            throw new InvalidDataException("Os dois hosts de vídeo não foram materializados.");
        if (!universalHost.ClipToBounds
            || !systemHost.ClipToBounds
            || Panel.GetZIndex(systemHost) <= Panel.GetZIndex(universalHost)
            || systemHost.Parent is not Grid videoBackground
            || !videoBackground.Children.OfType<Border>().Any(overlay =>
                Panel.GetZIndex(overlay) > Panel.GetZIndex(systemHost)))
            throw new InvalidDataException(
                "O vídeo do item precisa ficar recortado acima do fallback e abaixo dos overlays.");

        VerifyMediaElementCanReadRetainedLease(
            universalHost,
            window.SelectedCategory?.Id);

        var universalLeaseField = typeof(StoreWindow).GetField(
                                      "_retroUniversalVideoLease",
                                      BindingFlags.Instance | BindingFlags.NonPublic)
                                  ?? throw new MissingFieldException(
                                      nameof(StoreWindow),
                                      "_retroUniversalVideoLease");
        var universalPlayerField = typeof(StoreWindow).GetField(
                                       "_retroUniversalVideoPlayer",
                                       BindingFlags.Instance | BindingFlags.NonPublic)
                                   ?? throw new MissingFieldException(
                                       nameof(StoreWindow),
                                       "_retroUniversalVideoPlayer");
        var startUniversal = typeof(StoreWindow).GetMethod(
                                 "StartRetroUniversalVideo",
                                 BindingFlags.Instance | BindingFlags.NonPublic)
                              ?? throw new MissingMethodException(
                                  nameof(StoreWindow),
                                  "StartRetroUniversalVideo");
        var universalLoadTaskField = typeof(StoreWindow).GetField(
                                         "_retroUniversalVideoLoadTask",
                                         BindingFlags.Instance | BindingFlags.NonPublic)
                                     ?? throw new MissingFieldException(
                                         nameof(StoreWindow),
                                         "_retroUniversalVideoLoadTask");
        startUniversal.Invoke(window, null);
        WaitForDispatcherTask(
            window,
            RequireTask(universalLoadTaskField, window),
            "carregamento inicial do vídeo universal");
        var initialUniversalLease = universalLeaseField.GetValue(window)
                                    ?? throw new InvalidDataException(
                                        "O fallback universal não manteve o arquivo validado aberto.");
        if (!IsLeaseActive(initialUniversalLease)
            || universalPlayerField.GetValue(window) is not MediaElement initialUniversalPlayer
            || !universalHost.Children.Contains(initialUniversalPlayer))
            throw new InvalidDataException(
                "O lease universal não acompanhou o MediaElement ativo.");
        startUniversal.Invoke(window, null);
        if (!ReferenceEquals(initialUniversalLease, universalLeaseField.GetValue(window)))
            throw new InvalidDataException(
                "Atualizar a mesma categoria recalculou o hash do vídeo universal.");

        var openCatalog = typeof(StoreWindow).GetMethod(
                              "OpenCatalog",
                              BindingFlags.Instance | BindingFlags.NonPublic)
                          ?? throw new MissingMethodException(nameof(StoreWindow), "OpenCatalog");
        var retroCategory = window.CatalogCategories.Single(category =>
            category.Id.Equals("retro-games", StringComparison.OrdinalIgnoreCase));
        openCatalog.Invoke(window, [retroCategory]);
        var retroUniversalTask = RequireTask(universalLoadTaskField, window);

        var systemLoadTaskField = typeof(StoreWindow).GetField(
                                      "_retroSystemVideoLoadTask",
                                      BindingFlags.Instance | BindingFlags.NonPublic)
                                  ?? throw new MissingFieldException(
                                      nameof(StoreWindow),
                                      "_retroSystemVideoLoadTask");
        var pendingItemField = typeof(StoreWindow).GetField(
                                   "_pendingRetroSystemVideoItemId",
                                   BindingFlags.Instance | BindingFlags.NonPublic)
                               ?? throw new MissingFieldException(
                                   nameof(StoreWindow),
                                   "_pendingRetroSystemVideoItemId");
        var pendingTask = RequireTask(systemLoadTaskField, window);
        var pendingItem = pendingItemField.GetValue(window) as string;
        if (string.IsNullOrEmpty(pendingItem))
            throw new InvalidDataException(
                "Selecionar um sistema não registrou o carregamento assíncrono de seu vídeo.");

        var updateSelection = typeof(StoreWindow).GetMethod(
                                  "UpdateRetroCarouselSelection",
                                  BindingFlags.Instance | BindingFlags.NonPublic)
                              ?? throw new MissingMethodException(
                                  nameof(StoreWindow),
                                  "UpdateRetroCarouselSelection");
        updateSelection.Invoke(window, null);
        if (!ReferenceEquals(pendingTask, RequireTask(systemLoadTaskField, window)))
            throw new InvalidDataException(
                "Atualizar o mesmo item reiniciou o hash assíncrono do vídeo pendente.");

        WaitForDispatcherTask(
            window,
            Task.WhenAll(pendingTask, retroUniversalTask),
            "vídeos pendentes do sistema e da categoria");
        var systemLeaseField = typeof(StoreWindow).GetField(
                                   "_retroSystemVideoLease",
                                   BindingFlags.Instance | BindingFlags.NonPublic)
                               ?? throw new MissingFieldException(
                                   nameof(StoreWindow),
                                   "_retroSystemVideoLease");
        var systemPlayerField = typeof(StoreWindow).GetField(
                                    "_retroSystemVideoPlayer",
                                    BindingFlags.Instance | BindingFlags.NonPublic)
                                ?? throw new MissingFieldException(
                                    nameof(StoreWindow),
                                    "_retroSystemVideoPlayer");
        var activeSystemLease = systemLeaseField.GetValue(window)
                                ?? throw new InvalidDataException(
                                    "O lease pendente não foi transferido ao player do sistema.");
        if (!IsLeaseActive(activeSystemLease)
            || systemPlayerField.GetValue(window) is not MediaElement systemPlayer
            || !systemHost.Children.Contains(systemPlayer))
            throw new InvalidDataException(
                "O vídeo do sistema não manteve seu lease durante a reprodução.");
        updateSelection.Invoke(window, null);
        if (!ReferenceEquals(activeSystemLease, systemLeaseField.GetValue(window)))
            throw new InvalidDataException(
                "Atualizar o mesmo sistema recalculou o hash do player ativo.");

        var stopSystem = typeof(StoreWindow).GetMethod(
                             "StopRetroSystemVideo",
                             BindingFlags.Instance | BindingFlags.NonPublic)
                         ?? throw new MissingMethodException(
                             nameof(StoreWindow),
                             "StopRetroSystemVideo");
        stopSystem.Invoke(window, [false]);
        if (IsLeaseActive(activeSystemLease)
            || systemLeaseField.GetValue(window) is not null
            || systemHost.Children.Contains(systemPlayer))
            throw new InvalidDataException(
                "Parar o vídeo do sistema não liberou o lease depois de fechar o player.");

        var currentUniversalLease = universalLeaseField.GetValue(window)
                                    ?? throw new InvalidDataException(
                                        "O fallback universal foi removido junto com o vídeo do sistema.");
        if (!IsLeaseActive(currentUniversalLease))
            throw new InvalidDataException(
                "Parar o vídeo do sistema também encerrou o fallback universal.");

        VerifyRapidVideoNavigation(
            window,
            openCatalog,
            universalLoadTaskField,
            universalLeaseField);
        VerifyFailedVideoCloseQuarantine();
    }

    private static void VerifyRapidVideoNavigation(
        StoreWindow window,
        MethodInfo openCatalog,
        FieldInfo universalLoadTaskField,
        FieldInfo universalLeaseField)
    {
        var leaseFactory = typeof(StoreWindow).GetMethod(
                               "OpenRetroUniversalVideoLease",
                               BindingFlags.Static | BindingFlags.NonPublic)
                           ?? throw new MissingMethodException(
                               nameof(StoreWindow),
                               "OpenRetroUniversalVideoLease");
        using var probeLease = leaseFactory.Invoke(null, ["psp"]) as IDisposable
                               ?? throw new InvalidDataException(
                                   "O teste não obteve o vídeo grande de navegação.");
        var staleVideoPath = probeLease.GetType().GetProperty(
                                 "Path",
                                 BindingFlags.Instance | BindingFlags.Public)
                             ?.GetValue(probeLease) as string
                             ?? throw new MissingMemberException(
                                 probeLease.GetType().FullName,
                                 "Path");
        probeLease.Dispose();

        var psp = window.CatalogCategories.Single(category =>
            category.Id.Equals("psp", StringComparison.OrdinalIgnoreCase));
        var playstation4 = window.CatalogCategories.Single(category =>
            category.Id.Equals("playstation-4", StringComparison.OrdinalIgnoreCase));
        var heartbeatCount = 0;
        var heartbeat = new DispatcherTimer(
            TimeSpan.FromMilliseconds(1),
            DispatcherPriority.Send,
            (_, _) => heartbeatCount++,
            window.Dispatcher);
        heartbeat.Start();
        try
        {
            openCatalog.Invoke(window, [psp]);
            var staleTask = RequireTask(universalLoadTaskField, window);
            openCatalog.Invoke(window, [playstation4]);
            var currentTask = RequireTask(universalLoadTaskField, window);
            if (ReferenceEquals(staleTask, currentTask))
                throw new InvalidDataException(
                    "Navegação rápida não criou uma nova geração de vídeo.");

            WaitForDispatcherTask(
                window,
                Task.WhenAll(staleTask, currentTask),
                "navegação rápida entre vídeos universais");
        }
        finally
        {
            heartbeat.Stop();
        }

        if (heartbeatCount == 0)
            throw new InvalidDataException(
                "O dispatcher não processou heartbeat enquanto hashes de vídeo rodavam.");

        var activeCategoryField = typeof(StoreWindow).GetField(
                                      "_activeRetroUniversalVideoCategoryId",
                                      BindingFlags.Instance | BindingFlags.NonPublic)
                                  ?? throw new MissingFieldException(
                                      nameof(StoreWindow),
                                      "_activeRetroUniversalVideoCategoryId");
        if (activeCategoryField.GetValue(window) is not string activeCategory
            || !activeCategory.Equals("playstation-4", StringComparison.OrdinalIgnoreCase)
            || universalLeaseField.GetValue(window) is not { } activeLease
            || !IsLeaseActive(activeLease))
        {
            throw new InvalidDataException(
                "Um resultado obsoleto de vídeo venceu a categoria mais recente.");
        }

        try
        {
            using var exclusive = new FileStream(
                staleVideoPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.None);
        }
        catch (IOException exception)
        {
            throw new InvalidDataException(
                "O resultado obsoleto reteve o lease do vídeo cancelado.",
                exception);
        }
    }

    private static Task RequireTask(FieldInfo field, StoreWindow window) =>
        field.GetValue(window) as Task
        ?? throw new InvalidDataException($"O campo '{field.Name}' não contém uma Task.");

    private static void WaitForDispatcherTask(
        StoreWindow window,
        Task task,
        string scenario)
    {
        if (!task.IsCompleted)
        {
            var frame = new DispatcherFrame();
            var timedOut = false;
            var timeout = new DispatcherTimer(
                DispatcherPriority.Send,
                window.Dispatcher)
            {
                Interval = TimeSpan.FromSeconds(15)
            };
            timeout.Tick += (_, _) =>
            {
                timedOut = true;
                frame.Continue = false;
            };
            _ = task.ContinueWith(
                _ => window.Dispatcher.BeginInvoke(
                    DispatcherPriority.Send,
                    new Action(() => frame.Continue = false)),
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
            timeout.Start();
            try
            {
                Dispatcher.PushFrame(frame);
            }
            finally
            {
                timeout.Stop();
            }
            if (timedOut)
                throw new TimeoutException(
                    $"Timeout aguardando {scenario} sem bloquear o dispatcher.");
        }

        try
        {
            task.GetAwaiter().GetResult();
        }
        catch (Exception exception)
        {
            throw new InvalidDataException($"Falha em {scenario}.", exception);
        }
    }

    private static bool IsLeaseActive(object lease) =>
        lease.GetType().GetProperty("IsActive", BindingFlags.Instance | BindingFlags.Public)
            ?.GetValue(lease) as bool?
        ?? throw new MissingMemberException(lease.GetType().FullName, "IsActive");

    private static void VerifyMediaElementCanReadRetainedLease(
        Grid host,
        string? categoryId)
    {
        var leaseFactory = typeof(StoreWindow).GetMethod(
                               "OpenRetroUniversalVideoLease",
                               BindingFlags.Static | BindingFlags.NonPublic)
                           ?? throw new MissingMethodException(
                               nameof(StoreWindow),
                               "OpenRetroUniversalVideoLease");
        var lease = leaseFactory.Invoke(null, [categoryId]) as IDisposable
                    ?? throw new InvalidDataException(
                        "O teste de compatibilidade não obteve um lease de vídeo íntegro.");
        var ownershipTransferred = false;
        try
        {
            var path = lease.GetType().GetProperty(
                           "Path",
                           BindingFlags.Instance | BindingFlags.Public)
                       ?.GetValue(lease) as string
                       ?? throw new MissingMemberException(lease.GetType().FullName, "Path");
            var playerFactory = typeof(StoreWindow).GetMethod(
                                    "CreateResponsiveBackgroundVideoPlayer",
                                    BindingFlags.Static | BindingFlags.NonPublic)
                                ?? throw new MissingMethodException(
                                    nameof(StoreWindow),
                                    "CreateResponsiveBackgroundVideoPlayer");
            var player = playerFactory.Invoke(null, [host]) as MediaElement
                         ?? throw new InvalidDataException(
                             "O teste não conseguiu criar o player responsivo.");
            var frame = new DispatcherFrame();
            var opened = false;
            var timedOut = false;
            Exception? mediaFailure = null;
            var timeout = new DispatcherTimer(
                DispatcherPriority.Send,
                host.Dispatcher)
            {
                Interval = TimeSpan.FromSeconds(10)
            };

            void CompleteFrame()
            {
                if (frame.Continue) frame.Continue = false;
            }

            void HandleOpened(object sender, RoutedEventArgs args)
            {
                opened = true;
                CompleteFrame();
            }

            void HandleFailed(object? sender, ExceptionRoutedEventArgs args)
            {
                mediaFailure = args.ErrorException
                               ?? new InvalidOperationException(
                                   "MediaElement falhou sem fornecer uma exceção.");
                CompleteFrame();
            }

            void HandleTimeout(object? sender, EventArgs args)
            {
                timedOut = true;
                CompleteFrame();
            }

            player.MediaOpened += HandleOpened;
            player.MediaFailed += HandleFailed;
            timeout.Tick += HandleTimeout;
            host.Children.Add(player);
            try
            {
                timeout.Start();
                player.Source = new Uri(path, UriKind.Absolute);
                player.Play();
                Dispatcher.PushFrame(frame);
                if (mediaFailure is not null)
                {
                    throw new InvalidDataException(
                        "MediaElement não conseguiu abrir o MP4 enquanto FileShare.Read mantinha o lease.",
                        mediaFailure);
                }
                if (!opened || timedOut)
                {
                    throw new TimeoutException(
                        "MediaElement não sinalizou MediaOpened nem MediaFailed em 10 segundos.");
                }
                if (player.NaturalVideoWidth <= 0
                    || player.NaturalVideoHeight <= 0
                    || player.Stretch != Stretch.UniformToFill
                    || player.StretchDirection != StretchDirection.Both
                    || player.RenderTransform is not null and not MatrixTransform { Matrix.IsIdentity: true })
                    throw new InvalidDataException(
                        "O vídeo real não preencheu toda a área de forma proporcional e centralizada após MediaOpened.");
            }
            finally
            {
                timeout.Stop();
                timeout.Tick -= HandleTimeout;
                player.MediaOpened -= HandleOpened;
                player.MediaFailed -= HandleFailed;
                try { player.Stop(); } catch (InvalidOperationException) { }
                var playerClosed = false;
                try { player.Close(); playerClosed = true; }
                catch (InvalidOperationException) { }
                try { player.Source = null; playerClosed = true; }
                catch (InvalidOperationException) { }
                host.Children.Remove(player);
                if (!playerClosed)
                {
                    var retain = typeof(StoreWindow).GetMethod(
                                     "ReleaseOrRetainVideoLease",
                                     BindingFlags.Static | BindingFlags.NonPublic)
                                 ?? throw new MissingMethodException(
                                     nameof(StoreWindow),
                                     "ReleaseOrRetainVideoLease");
                    retain.Invoke(null, [player, lease, false]);
                    ownershipTransferred = true;
                }
            }
        }
        finally
        {
            if (!ownershipTransferred) lease.Dispose();
        }
    }

    private static void VerifyFailedVideoCloseQuarantine()
    {
        var leaseFactory = typeof(StoreWindow).GetMethod(
                               "OpenRetroUniversalVideoLease",
                               BindingFlags.Static | BindingFlags.NonPublic)
                           ?? throw new MissingMethodException(
                               nameof(StoreWindow),
                               "OpenRetroUniversalVideoLease");
        var lease = leaseFactory.Invoke(null, ["retro-games"]) as IDisposable
                    ?? throw new InvalidDataException(
                        "O teste de quarantine não obteve um lease de vídeo íntegro.");
        var ownershipTransferred = false;
        try
        {
            var quarantineField = typeof(StoreWindow).GetField(
                                      "FailedVideoCloseQuarantine",
                                      BindingFlags.Static | BindingFlags.NonPublic)
                                  ?? throw new MissingFieldException(
                                      nameof(StoreWindow),
                                      "FailedVideoCloseQuarantine");
            var quarantine = quarantineField.GetValue(null) as System.Collections.IList
                             ?? throw new InvalidDataException(
                                 "A quarantine de vídeo não expõe uma coleção verificável.");
            var release = typeof(StoreWindow).GetMethod(
                              "ReleaseOrRetainVideoLease",
                              BindingFlags.Static | BindingFlags.NonPublic)
                          ?? throw new MissingMethodException(
                              nameof(StoreWindow),
                              "ReleaseOrRetainVideoLease");
            var disabled = typeof(StoreWindow).GetProperty(
                               "IsVideoPlaybackDisabled",
                               BindingFlags.Static | BindingFlags.NonPublic)
                           ?? throw new MissingMemberException(
                               nameof(StoreWindow),
                               "IsVideoPlaybackDisabled");
            var player = new MediaElement();
            var initialCount = quarantine.Count;
            release.Invoke(null, [player, lease, false]);
            ownershipTransferred = true;

            if (!IsLeaseActive(lease)
                || disabled.GetValue(null) as bool? != true
                || quarantine.Count != initialCount + 1)
                throw new InvalidDataException(
                    "Uma falha de Close não reteve o player e o lease pelo lifetime do processo.");

            var quarantined = quarantine[quarantine.Count - 1]
                              ?? throw new InvalidDataException(
                                  "A quarantine adicionou uma entrada nula.");
            var playerField = quarantined.GetType().GetField("Item1")
                              ?? throw new MissingFieldException(
                                  quarantined.GetType().FullName,
                                  "Item1");
            var leaseField = quarantined.GetType().GetField("Item2")
                             ?? throw new MissingFieldException(
                                 quarantined.GetType().FullName,
                                 "Item2");
            if (!ReferenceEquals(player, playerField.GetValue(quarantined))
                || !ReferenceEquals(lease, leaseField.GetValue(quarantined)))
                throw new InvalidDataException(
                    "A quarantine separou o MediaElement do lease que protege seu path.");
        }
        finally
        {
            if (!ownershipTransferred) lease.Dispose();
        }
    }

    private static void VerifyResponsiveBackgroundVideo(StoreWindow window)
    {
        if (window.FindName("RetroSystemVideoBackground") is not Grid background
            || window.FindName("RetroUniversalVideoPlayerHost") is not Grid host
            || window.FindName("RetroSystemVideoPlayerHost") is not Grid systemHost
            || window.FindName("RetroCarouselHost") is not Grid carouselHost
            || window.FindName("RetroCarouselScaleHost") is not Viewbox scaleHost
            || window.FindName("RetroCarouselViewport") is not Grid carouselViewport
            || window.FindName("RetroCarouselCurrent") is not ContentControl carouselCurrent
            || window.FindName("RetroCarouselActionBar") is not ContentControl carouselActionBar
            || window.FindName("TitleBarHost") is not Border titleBarHost
            || window.FindName("TitleBranding") is not StackPanel titleBranding
            || window.FindName("WindowChromeButtons") is not StackPanel windowChromeButtons
            || window.FindName("GlobalMusicPlayer") is not Border globalMusicPlayer
            || window.FindName("TitleCurrentPlatform") is not TextBlock titleCurrentPlatform
            || window.FindName("CatalogPage") is not Grid catalogPage
            || window.FindName("CatalogMetalFrameOverlay") is not Grid catalogMetalFrameOverlay
            || window.FindName("CatalogHudHeader") is not Border catalogHudHeader
            || window.FindName("CatalogContentPanel") is not Grid catalogContentPanel
            || window.FindName("CatalogBottomActions") is not StackPanel catalogBottomActions
            || window.FindName("CatalogHudOpenFolderButton") is not Button hudOpenFolder
            || window.FindName("CatalogHudSupportButton") is not Button hudSupport
            || window.FindName("CatalogHudChooseInstallButton") is not Button hudChooseInstall
            || window.FindName("CatalogHudChooseTempButton") is not Button hudChooseTemp
            || window.FindName("CatalogHudResetTempButton") is not Button hudResetTemp
            || window.FindName("InstallFolderPath") is not TextBlock installPath
            || window.FindName("TempFolderPath") is not TextBlock tempPath
            || window.FindName("CatalogHudSearchPanel") is not Border hudSearchPanel
            || window.FindName("RetroCarouselInfoPanel") is not Grid retroCarouselInfoPanel
            || window.FindName("RetroCarouselInfoOuterFrame") is not Border retroCarouselInfoOuterFrame
            || window.FindName("RetroCarouselInfoInnerFrame") is not Border retroCarouselInfoInnerFrame
            || window.FindName("RetroCarouselFooter") is not Border retroCarouselFooter)
            throw new InvalidDataException("A área responsiva do vídeo de fundo não foi criada.");
        if (!background.ClipToBounds
            || !host.ClipToBounds
            || !systemHost.ClipToBounds
            || background.Children.Count != 3
            || !ReferenceEquals(background.Children[0], host)
            || !ReferenceEquals(background.Children[1], systemHost)
            || background.Children[2] is not Border videoOverlay
            || videoOverlay.Background is not LinearGradientBrush
            || !ReferenceEquals(
                videoOverlay.Background,
                window.Resources["CurrentSystemVideoOverlayBrush"])
            || Panel.GetZIndex(videoOverlay) != 2
            || videoOverlay.HorizontalAlignment != HorizontalAlignment.Stretch
            || videoOverlay.VerticalAlignment != VerticalAlignment.Stretch
            || host.HorizontalAlignment != HorizontalAlignment.Stretch
            || host.VerticalAlignment != VerticalAlignment.Stretch
            || systemHost.HorizontalAlignment != HorizontalAlignment.Stretch
            || systemHost.VerticalAlignment != VerticalAlignment.Stretch
            || scaleHost.Stretch != Stretch.Uniform
            || scaleHost.StretchDirection != StretchDirection.Both
            || Math.Abs(carouselViewport.Width - 1060) > double.Epsilon
            || Math.Abs(carouselViewport.Height - 504) > double.Epsilon
            || Math.Abs(carouselActionBar.Width - 320) > double.Epsilon
            || carouselCurrent.RenderTransform is not TransformGroup currentTransforms
            || currentTransforms.Children.OfType<ScaleTransform>().SingleOrDefault() is not { } currentScale
            || currentScale.ScaleX < 1.27
            || currentScale.ScaleY < 1.27)
            throw new InvalidDataException(
                "O vídeo precisa preencher a área e o carrossel deve aproveitar o espaço liberado com capas ampliadas.");

        var factory = typeof(StoreWindow).GetMethod(
                          "CreateResponsiveBackgroundVideoPlayer",
                          BindingFlags.Static | BindingFlags.NonPublic)
                      ?? throw new MissingMethodException(
                          nameof(StoreWindow),
                          "CreateResponsiveBackgroundVideoPlayer");
        var player = factory.Invoke(null, [host]) as MediaElement
                     ?? throw new InvalidDataException("O player responsivo não pôde ser criado.");
        var systemFactory = typeof(StoreWindow).GetMethod(
                                "CreateResponsiveSystemVideoPlayer",
                                BindingFlags.Static | BindingFlags.NonPublic)
                            ?? throw new MissingMethodException(
                                nameof(StoreWindow),
                                "CreateResponsiveSystemVideoPlayer");
        var systemPlayer = systemFactory.Invoke(null, [systemHost]) as MediaElement
                           ?? throw new InvalidDataException(
                               "O player proporcional do sistema não pôde ser criado.");
        if (player.Stretch != Stretch.UniformToFill
            || player.StretchDirection != StretchDirection.Both
            || player.HorizontalAlignment != HorizontalAlignment.Stretch
            || player.VerticalAlignment != VerticalAlignment.Stretch
            || BindingOperations.GetBindingExpressionBase(player, FrameworkElement.WidthProperty) is null
            || BindingOperations.GetBindingExpressionBase(player, FrameworkElement.HeightProperty) is null
            || !player.IsMuted
            || Math.Abs(player.Volume) > double.Epsilon
            || player.RenderTransform is not null and not MatrixTransform { Matrix.IsIdentity: true })
            throw new InvalidDataException(
                "O player universal precisa preencher toda a área, proporcionalmente, centralizado e sem áudio.");
        if (systemPlayer.Stretch != Stretch.UniformToFill
            || systemPlayer.StretchDirection != StretchDirection.Both
            || systemPlayer.HorizontalAlignment != HorizontalAlignment.Stretch
            || systemPlayer.VerticalAlignment != VerticalAlignment.Stretch
            || BindingOperations.GetBindingExpressionBase(systemPlayer, FrameworkElement.WidthProperty) is null
            || BindingOperations.GetBindingExpressionBase(systemPlayer, FrameworkElement.HeightProperty) is null
            || !systemPlayer.IsMuted
            || Math.Abs(systemPlayer.Volume) > double.Epsilon
            || systemPlayer.RenderTransform is not null and not MatrixTransform { Matrix.IsIdentity: true })
            throw new InvalidDataException(
                "O vídeo específico do sistema precisa preencher toda a área, proporcionalmente e centralizado.");
        host.Children.Add(player);
        systemHost.Children.Add(systemPlayer);
        var originalCarouselVisibility = carouselHost.Visibility;
        carouselHost.Visibility = Visibility.Visible;
        try
        {
            foreach (var (width, height) in new[]
                     {
                         (900d, 480d),
                         (960d, 540d),
                         (1080d, 680d),
                         (1600d, 900d)
                     })
            {
                window.Width = width;
                window.Height = height;
                window.UpdateLayout();

                var visibleCarouselActions = FindVisualDescendants<Button>(carouselActionBar)
                    .Where(candidate => candidate.IsVisible)
                    .ToArray();
                if (visibleCarouselActions.Length != 1
                    || Math.Abs(
                        visibleCarouselActions[0].ActualWidth
                        - carouselActionBar.ActualWidth) > .5
                    || visibleCarouselActions[0].ActualHeight < 23.5)
                    throw new InvalidDataException(
                        $"O botão da capa principal não ocupou toda a largura em {width:0}×{height:0}.");

                var brandingBounds = titleBranding
                    .TransformToAncestor(titleBarHost)
                    .TransformBounds(new Rect(titleBranding.RenderSize));
                var playerBounds = globalMusicPlayer
                    .TransformToAncestor(titleBarHost)
                    .TransformBounds(new Rect(globalMusicPlayer.RenderSize));
                var chromeBounds = windowChromeButtons
                    .TransformToAncestor(titleBarHost)
                    .TransformBounds(new Rect(windowChromeButtons.RenderSize));
                const double titleBarTolerance = 0.5;
                if (Math.Abs(globalMusicPlayer.ActualWidth - 420d) > titleBarTolerance
                    || brandingBounds.Left < -titleBarTolerance
                    || brandingBounds.Right > playerBounds.Left + titleBarTolerance
                    || playerBounds.Right > chromeBounds.Left + titleBarTolerance
                    || chromeBounds.Right > titleBarHost.ActualWidth + titleBarTolerance
                    || brandingBounds.Top < -titleBarTolerance
                    || playerBounds.Top < -titleBarTolerance
                    || chromeBounds.Top < -titleBarTolerance
                    || brandingBounds.Bottom > titleBarHost.ActualHeight + titleBarTolerance
                    || playerBounds.Bottom > titleBarHost.ActualHeight + titleBarTolerance
                    || chromeBounds.Bottom > titleBarHost.ActualHeight + titleBarTolerance)
                    throw new InvalidDataException(
                        $"O player global sobrepôs a marca ou os controles em {width:0}×{height:0}: " +
                        $"marca={brandingBounds}, player={playerBounds}, janela={chromeBounds}.");

                var hudHeaderBounds = catalogHudHeader
                    .TransformToAncestor(catalogPage)
                    .TransformBounds(new Rect(catalogHudHeader.RenderSize));
                var contentBounds = catalogContentPanel
                    .TransformToAncestor(catalogPage)
                    .TransformBounds(new Rect(catalogContentPanel.RenderSize));
                var searchPanelBounds = hudSearchPanel
                    .TransformToAncestor(catalogHudHeader)
                    .TransformBounds(new Rect(hudSearchPanel.RenderSize));
                var bottomActionBounds = catalogBottomActions
                    .TransformToAncestor(catalogContentPanel)
                    .TransformBounds(new Rect(catalogBottomActions.RenderSize));
                const double hudTolerance = 0.5;
                if (!catalogHudHeader.IsVisible
                    || !hudSearchPanel.IsVisible
                    || !hudOpenFolder.IsVisible
                    || !hudSupport.IsVisible
                    || !hudChooseInstall.IsVisible
                    || !hudChooseTemp.IsVisible
                    || !hudResetTemp.IsVisible
                    || hudHeaderBounds.Left < -hudTolerance
                    || hudHeaderBounds.Right > catalogPage.ActualWidth + hudTolerance
                    || searchPanelBounds.Left < -hudTolerance
                    || searchPanelBounds.Right > catalogHudHeader.ActualWidth + hudTolerance
                    || Math.Abs(catalogHudHeader.ActualHeight - 50) > hudTolerance
                    || hudHeaderBounds.Bottom > contentBounds.Top + hudTolerance
                    || contentBounds.Bottom > catalogPage.ActualHeight + hudTolerance
                    || bottomActionBounds.Right > catalogContentPanel.ActualWidth + hudTolerance
                    || bottomActionBounds.Bottom > catalogContentPanel.ActualHeight + hudTolerance
                    || Math.Abs(bottomActionBounds.Right - catalogContentPanel.ActualWidth) > hudTolerance
                    || bottomActionBounds.Top < catalogContentPanel.ActualHeight - 54
                    || hudSearchPanel.ActualWidth < 70
                    || installPath.ActualWidth < 12
                    || tempPath.ActualWidth < 12)
                    throw new InvalidDataException(
                        $"A barra HUD única perdeu espaço ou ultrapassou o catálogo em {width:0}×{height:0}: " +
                        $"cabeçalho={hudHeaderBounds}, conteúdo={contentBounds}, busca={searchPanelBounds}.");

                if (Math.Abs(player.Width - host.ActualWidth) > 0.5
                    || Math.Abs(player.Height - host.ActualHeight) > 0.5
                    || Math.Abs(systemPlayer.Width - systemHost.ActualWidth) > 0.5
                    || Math.Abs(systemPlayer.Height - systemHost.ActualHeight) > 0.5
                    || Math.Abs(host.ActualWidth - background.ActualWidth) > 0.5
                    || Math.Abs(host.ActualHeight - background.ActualHeight) > 0.5
                    || Math.Abs(systemHost.ActualWidth - background.ActualWidth) > 0.5
                    || Math.Abs(systemHost.ActualHeight - background.ActualHeight) > 0.5
                    || Math.Abs(videoOverlay.ActualWidth - background.ActualWidth) > 0.5
                    || Math.Abs(videoOverlay.ActualHeight - background.ActualHeight) > 0.5
                    || Math.Abs(background.ActualWidth - carouselHost.ActualWidth) > 0.5
                    || Math.Abs(background.ActualHeight - carouselHost.ActualHeight) > 0.5)
                    throw new InvalidDataException(
                        $"O vídeo de fundo não acompanhou a janela em {width:0}×{height:0}: " +
                        $"player={player.Width:0.##}×{player.Height:0.##}, " +
                        $"host={host.ActualWidth:0.##}×{host.ActualHeight:0.##}, " +
                        $"fundo={background.ActualWidth:0.##}×{background.ActualHeight:0.##}.");

                var displayedCarouselBounds = carouselViewport
                    .TransformToAncestor(carouselHost)
                    .TransformBounds(new Rect(carouselViewport.RenderSize));
                const double tolerance = 0.5;
                if (displayedCarouselBounds.Left < -tolerance
                    || displayedCarouselBounds.Top < -tolerance
                    || displayedCarouselBounds.Right > carouselHost.ActualWidth + tolerance
                    || displayedCarouselBounds.Bottom > carouselHost.ActualHeight + tolerance)
                    throw new InvalidDataException(
                        $"O carrossel ultrapassou a área disponível em {width:0}×{height:0}: " +
                        $"carrossel={displayedCarouselBounds}, " +
                        $"host={carouselHost.ActualWidth:0.##}×{carouselHost.ActualHeight:0.##}.");

                AssertElementWithinAncestor(
                    background,
                    window,
                    width,
                    height,
                    "host do vídeo de fundo");
                AssertElementWithinAncestor(
                    host,
                    window,
                    width,
                    height,
                    "host do vídeo universal");
                AssertElementWithinAncestor(
                    systemHost,
                    window,
                    width,
                    height,
                    "host do vídeo do sistema");
                AssertElementWithinAncestor(
                    carouselHost,
                    window,
                    width,
                    height,
                    "host do carrossel");
                AssertElementWithinAncestor(
                    globalMusicPlayer,
                    window,
                    width,
                    height,
                    "player global de música");
                AssertElementWithinAncestor(
                    visibleCarouselActions[0],
                    carouselActionBar,
                    width,
                    height,
                    "botão integral da capa principal");
                AssertElementWithinAncestor(
                    catalogHudHeader,
                    catalogPage,
                    width,
                    height,
                    "cabeçalho HUD do catálogo");
                AssertElementWithinAncestor(
                    catalogContentPanel,
                    catalogPage,
                    width,
                    height,
                    "conteúdo ampliado abaixo do HUD");
                AssertElementWithinAncestor(
                    catalogMetalFrameOverlay,
                    catalogPage,
                    width,
                    height,
                    "moldura metálica integral do catálogo");
                AssertElementWithinAncestor(
                    catalogBottomActions,
                    catalogContentPanel,
                    width,
                    height,
                    "ações inferiores do catálogo");
                AssertElementWithinAncestor(
                    hudSearchPanel,
                    catalogHudHeader,
                    width,
                    height,
                    "painel HUD de pesquisa");
                AssertElementWithinAncestor(
                    installPath,
                    catalogHudHeader,
                    width,
                    height,
                    "caminho de instalação no HUD");
                AssertElementWithinAncestor(
                    tempPath,
                    catalogHudHeader,
                    width,
                    height,
                    "caminho temporário no HUD");

                foreach (var hudButton in new[]
                         {
                             hudChooseInstall,
                             hudChooseTemp,
                             hudResetTemp
                         })
                {
                    AssertElementWithinAncestor(
                        hudButton,
                        catalogHudHeader,
                        width,
                        height,
                        $"ação '{AutomationProperties.GetName(hudButton)}' do HUD");
                    AssertHudButtonTemplateWithinButton(
                        hudButton,
                        width,
                        height);
                }

                foreach (var bottomButton in new[] { hudOpenFolder, hudSupport })
                {
                    AssertElementWithinAncestor(
                        bottomButton,
                        catalogBottomActions,
                        width,
                        height,
                        $"ação inferior '{AutomationProperties.GetName(bottomButton)}'");
                    AssertHudButtonTemplateWithinButton(
                        bottomButton,
                        width,
                        height);
                }

                AssertElementWithinAncestor(
                    titleCurrentPlatform,
                    titleBranding,
                    width,
                    height,
                    "nome da plataforma na barra superior");
                AssertElementWithinAncestor(
                    retroCarouselInfoOuterFrame,
                    retroCarouselInfoPanel,
                    width,
                    height,
                    "moldura externa da descrição do jogo");
                AssertElementWithinAncestor(
                    retroCarouselInfoInnerFrame,
                    retroCarouselInfoPanel,
                    width,
                    height,
                    "moldura interna da descrição do jogo");
                AssertElementWithinAncestor(
                    retroCarouselFooter,
                    catalogContentPanel,
                    width,
                    height,
                    "rodapé metálico do carrossel");

                foreach (var button in FindVisualDescendants<Button>(carouselViewport)
                             .Where(candidate => candidate.IsVisible
                                                 && candidate.IsEnabled
                                                 && candidate.Opacity > 0
                                                 && candidate.ActualWidth > 0
                                                 && candidate.ActualHeight > 0))
                {
                    AssertElementWithinAncestor(
                        button,
                        carouselHost,
                        width,
                        height,
                        $"botão '{AutomationProperties.GetName(button)}'");
                }
            }

            var clamp = typeof(StoreWindow).GetMethod(
                            "ClampWindowToCurrentMonitor",
                            BindingFlags.Instance | BindingFlags.NonPublic)
                        ?? throw new MissingMethodException(
                            nameof(StoreWindow),
                            "ClampWindowToCurrentMonitor");
            window.WindowState = WindowState.Maximized;
            clamp.Invoke(window, null);
            if (window.WindowState != WindowState.Maximized)
                throw new InvalidDataException(
                    "O clamp por work area alterou uma janela maximizada.");
            window.WindowState = WindowState.Normal;
        }
        finally
        {
            carouselHost.Visibility = originalCarouselVisibility;
            host.Children.Remove(player);
            systemHost.Children.Remove(systemPlayer);
        }
    }

    private static void VerifyLicenseMasking()
    {
        if (StoreWindow.FormatMaskedLicenseId("ABCDEF") != "Cliente ••••CDEF"
            || StoreWindow.FormatMaskedLicenseId("ABCD") != "Cliente ••••"
            || StoreWindow.FormatMaskedLicenseId(new string('X', 60) + "1234")
            != "Cliente ••••1234")
            throw new InvalidDataException(
                "O mascaramento da licença não protegeu os limites esperados.");
    }

    private static void VerifyEmbeddedMusicResources()
    {
        var tracks = EmbeddedMusicLibrary.Tracks;
        if (tracks.Count != 8)
            throw new InvalidDataException(
                "A playlist interna precisa conter as oito músicas únicas aprovadas.");
        var resourceNames = new HashSet<string>(StringComparer.Ordinal);
        var fileNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var hashes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var track in tracks)
        {
            if (!resourceNames.Add(track.ResourceName)
                || !fileNames.Add(track.FileName)
                || !hashes.Add(track.Sha256))
                throw new InvalidDataException(
                    "A playlist interna contém recurso, arquivo ou música duplicada.");
            using var stream = typeof(EmbeddedMusicLibrary).Assembly.GetManifestResourceStream(
                                   track.ResourceName)
                               ?? throw new InvalidDataException(
                                   $"A música interna '{track.DisplayName}' não foi incorporada.");
            if (stream.Length != track.Length)
                throw new InvalidDataException(
                    $"A música interna '{track.DisplayName}' possui tamanho incorreto.");
            var actualHash = Convert.ToHexString(SHA256.HashData(stream));
            if (!actualHash.Equals(track.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException(
                    $"A música interna '{track.DisplayName}' possui SHA-256 incorreto.");
        }

        var paths = EmbeddedMusicLibrary.PreparePlaylist(CancellationToken.None);
        if (paths.Count != tracks.Count)
            throw new InvalidDataException("O cache validado não materializou a playlist interna.");
        using var lease = EmbeddedMusicLibrary.OpenVerifiedTrackLease(
            paths[0],
            CancellationToken.None);
        lease.Revalidate();
        try
        {
            using var unexpectedWriter = new FileStream(
                paths[0],
                FileMode.Open,
                FileAccess.Write,
                FileShare.ReadWrite | FileShare.Delete);
            throw new InvalidDataException(
                "A música validada permaneceu gravável enquanto o decoder poderia usá-la.");
        }
        catch (IOException)
        {
        }
        lease.Revalidate();
    }

    private static void VerifyBuiltInMusicAutoplay(StoreWindow window)
    {
        var tracksField = typeof(StoreWindow).GetField(
                              "_musicTracks",
                              BindingFlags.Instance | BindingFlags.NonPublic)
                          ?? throw new MissingFieldException(nameof(StoreWindow), "_musicTracks");
        var playingField = typeof(StoreWindow).GetField(
                               "_isMusicPlaying",
                               BindingFlags.Instance | BindingFlags.NonPublic)
                           ?? throw new MissingFieldException(nameof(StoreWindow), "_isMusicPlaying");
        var trackIndexField = typeof(StoreWindow).GetField(
                                  "_musicTrackIndex",
                                  BindingFlags.Instance | BindingFlags.NonPublic)
                              ?? throw new MissingFieldException(
                                  nameof(StoreWindow),
                                  "_musicTrackIndex");
        var carouselIndexField = typeof(StoreWindow).GetField(
                                     "_retroCarouselIndex",
                                     BindingFlags.Instance | BindingFlags.NonPublic)
                                 ?? throw new MissingFieldException(
                                     nameof(StoreWindow),
                                     "_retroCarouselIndex");
        var enabledField = typeof(StoreWindow).GetField(
                               "_isMusicPlaybackEnabled",
                               BindingFlags.Instance | BindingFlags.NonPublic)
                           ?? throw new MissingFieldException(
                               nameof(StoreWindow),
                               "_isMusicPlaybackEnabled");
        var openedPathField = typeof(StoreWindow).GetField(
                                  "_openedMusicTrackPath",
                                  BindingFlags.Instance | BindingFlags.NonPublic)
                              ?? throw new MissingFieldException(
                                  nameof(StoreWindow),
                                  "_openedMusicTrackPath");
        var leaseField = typeof(StoreWindow).GetField(
                             "_activeEmbeddedMusicTrackLease",
                             BindingFlags.Instance | BindingFlags.NonPublic)
                         ?? throw new MissingFieldException(
                             nameof(StoreWindow),
                             "_activeEmbeddedMusicTrackLease");
        var playerField = typeof(StoreWindow).GetField(
                              "_musicPlayer",
                              BindingFlags.Instance | BindingFlags.NonPublic)
                          ?? throw new MissingFieldException(nameof(StoreWindow), "_musicPlayer");
        var frame = new DispatcherFrame();
        var succeeded = false;
        var poll = new DispatcherTimer(
            DispatcherPriority.ApplicationIdle,
            window.Dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(50)
        };
        var timeout = new DispatcherTimer(
            DispatcherPriority.Send,
            window.Dispatcher)
        {
            Interval = TimeSpan.FromSeconds(15)
        };
        void Complete()
        {
            if (frame.Continue) frame.Continue = false;
        }
        poll.Tick += (_, _) =>
        {
            var tracks = tracksField.GetValue(window) as List<string>;
            var isPlaying = playingField.GetValue(window) as bool? == true;
            var activeLease = leaseField.GetValue(window) as EmbeddedMusicTrackLease;
            var player = playerField.GetValue(window) as MediaPlayer;
            if (tracks?.Count != EmbeddedMusicLibrary.Tracks.Count
                || !isPlaying
                || activeLease is null
                || player is null
                || !player.NaturalDuration.HasTimeSpan
                || player.NaturalDuration.TimeSpan <= TimeSpan.Zero)
                return;
            var expectedNames = EmbeddedMusicLibrary.Tracks.Select(track => track.FileName);
            if (!tracks.Select(Path.GetFileName).SequenceEqual(
                    expectedNames,
                    StringComparer.OrdinalIgnoreCase))
                return;
            activeLease.Revalidate();
            succeeded = true;
            Complete();
        };
        timeout.Tick += (_, _) => Complete();
        try
        {
            poll.Start();
            timeout.Start();
            Dispatcher.PushFrame(frame);
        }
        finally
        {
            poll.Stop();
            timeout.Stop();
        }
        if (!succeeded)
            throw new InvalidDataException(
                "As músicas internas não iniciaram automaticamente na ordem incorporada.");

        if (window.FindName("GlobalMusicPlayer") is not Border globalPlayer
            || window.FindName("GlobalMusicTrackTitle") is not TextBlock trackTitle
            || window.FindName("GlobalMusicPlaybackStatus") is not TextBlock playbackStatus
            || window.FindName("GlobalMusicPlayPauseGlyph") is not TextBlock playPauseGlyph
            || window.FindName("MusicPreviousButton") is not Button previousButton
            || window.FindName("MusicPlayPauseButton") is not Button playPauseButton
            || window.FindName("MusicNextButton") is not Button nextButton
            || window.FindName("MusicStopButton") is not Button stopButton
            || window.FindName("MusicFolderButton") is not Button folderButton
            || window.FindName("GlobalMusicVolumeSlider") is not Slider volumeSlider
            || !globalPlayer.IsVisible
            || !previousButton.IsVisible
            || !playPauseButton.IsVisible
            || !nextButton.IsVisible
            || !stopButton.IsVisible
            || !folderButton.IsVisible
            || !volumeSlider.IsVisible
            || globalPlayer.ActualWidth < 300
            || string.IsNullOrWhiteSpace(trackTitle.Text)
            || playbackStatus.Text != "TOCANDO"
            || playPauseGlyph.Text != "Ⅱ"
            || Math.Abs(volumeSlider.Value - 35) > double.Epsilon)
            throw new InvalidDataException(
                "O player global não expôs faixa, reprodução, desligamento e volume na barra superior.");

        var musicPlayerInput = typeof(StoreWindow).GetMethod(
                                   "IsGlobalMusicPlayerInput",
                                   BindingFlags.Instance | BindingFlags.NonPublic)
                               ?? throw new MissingMethodException(
                                   nameof(StoreWindow),
                                   "IsGlobalMusicPlayerInput");
        foreach (var musicControl in new Control[]
                 {
                     previousButton,
                     playPauseButton,
                     nextButton,
                     stopButton,
                     folderButton,
                     volumeSlider
                 })
        {
            if (musicPlayerInput.Invoke(window, [musicControl]) as bool? != true)
                throw new InvalidDataException(
                    $"O controle '{musicControl.Name}' não foi isolado das setas do carrossel.");
        }
        var previewKeyDown = typeof(StoreWindow).GetMethod(
                                 "OnPreviewKeyDown",
                                 BindingFlags.Instance | BindingFlags.NonPublic)
                             ?? throw new MissingMethodException(
                                 nameof(StoreWindow),
                                 "OnPreviewKeyDown");
        var presentationSource = PresentationSource.FromVisual(window)
                                 ?? throw new InvalidDataException(
                                     "A janela não possui PresentationSource para validar o teclado.");
        var carouselIndexBeforeVolumeKey = carouselIndexField.GetValue(window) as int?;
        var volumeKey = new KeyEventArgs(
            Keyboard.PrimaryDevice,
            presentationSource,
            Environment.TickCount,
            Key.Right)
        {
            RoutedEvent = Keyboard.PreviewKeyDownEvent,
            Source = volumeSlider
        };
        previewKeyDown.Invoke(window, [volumeKey]);
        if (volumeKey.Handled
            || carouselIndexField.GetValue(window) as int? != carouselIndexBeforeVolumeKey)
            throw new InvalidDataException(
                "A seta de volume foi capturada pelo carrossel em vez do player global.");

        stopButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        if (enabledField.GetValue(window) as bool? != false
            || playingField.GetValue(window) as bool? != false
            || leaseField.GetValue(window) is not null
            || !string.IsNullOrEmpty(openedPathField.GetValue(window) as string)
            || playbackStatus.Text != "DESLIGADO"
            || playPauseGlyph.Text != "▶")
            throw new InvalidDataException(
                "O botão desligar não encerrou a reprodução e liberou a faixa incorporada.");

        var mediaOpened = typeof(StoreWindow).GetMethod(
                              "MusicPlayer_MediaOpened",
                              BindingFlags.Instance | BindingFlags.NonPublic)
                          ?? throw new MissingMethodException(
                              nameof(StoreWindow),
                              "MusicPlayer_MediaOpened");
        mediaOpened.Invoke(window, [null, EventArgs.Empty]);
        if (enabledField.GetValue(window) as bool? != false
            || playingField.GetValue(window) as bool? != false
            || playbackStatus.Text != "DESLIGADO"
            || playPauseGlyph.Text != "▶")
            throw new InvalidDataException(
                "Um MediaOpened atrasado reativou visualmente o player desligado.");

        playPauseButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        if (enabledField.GetValue(window) as bool? != true
            || playingField.GetValue(window) as bool? != true
            || leaseField.GetValue(window) is not EmbeddedMusicTrackLease resumedLease
            || string.IsNullOrWhiteSpace(openedPathField.GetValue(window) as string)
            || playbackStatus.Text != "TOCANDO"
            || playPauseGlyph.Text != "Ⅱ")
            throw new InvalidDataException(
                "O player global não retomou a faixa depois de desligar.");
        resumedLease.Revalidate();

        playPauseButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        var pausedTrackIndex = trackIndexField.GetValue(window) as int?
                               ?? throw new InvalidDataException(
                                   "O índice da faixa pausada não foi preservado.");
        var mediaEnded = typeof(StoreWindow).GetMethod(
                             "MusicPlayer_MediaEnded",
                             BindingFlags.Instance | BindingFlags.NonPublic)
                         ?? throw new MissingMethodException(
                             nameof(StoreWindow),
                             "MusicPlayer_MediaEnded");
        mediaEnded.Invoke(window, [null, EventArgs.Empty]);
        if (enabledField.GetValue(window) as bool? != true
            || playingField.GetValue(window) as bool? != false
            || trackIndexField.GetValue(window) as int? != pausedTrackIndex
            || playbackStatus.Text != "PAUSADO"
            || playPauseGlyph.Text != "▶")
            throw new InvalidDataException(
                "Um MediaEnded atrasado avançou ou retomou o player pausado.");
    }

    private static void VerifyLocalGamesOnlyShowsPhysicalContent()
    {
        var physicalPath = Path.GetTempFileName();
        try
        {
            CatalogLocalGameInspection Inspection(CatalogLocalGameStatus status, string path) =>
                new(status, path, "teste");
            var missingPath = physicalPath + ".ausente";
            if (!StoreWindow.IsVisibleLocalGame(
                    Inspection(CatalogLocalGameStatus.Downloaded, physicalPath))
                || !StoreWindow.IsVisibleLocalGame(
                    Inspection(CatalogLocalGameStatus.Incomplete, physicalPath))
                || !StoreWindow.IsVisibleLocalGame(
                    Inspection(CatalogLocalGameStatus.Unsafe, physicalPath))
                || StoreWindow.IsVisibleLocalGame(
                    Inspection(CatalogLocalGameStatus.Downloaded, missingPath))
                || StoreWindow.IsVisibleLocalGame(
                    Inspection(CatalogLocalGameStatus.Scanning, physicalPath))
                || StoreWindow.IsVisibleLocalGame(
                    Inspection(CatalogLocalGameStatus.NotDownloaded, physicalPath))
                || StoreWindow.IsVisibleLocalGame(
                    Inspection(CatalogLocalGameStatus.Unavailable, physicalPath)))
                throw new InvalidDataException(
                    "Jogos locais deve esconder o catálogo ausente e mostrar somente conteúdo físico.");
        }
        finally
        {
            File.Delete(physicalPath);
        }
    }

    private static void AssertElementWithinAncestor(
        FrameworkElement element,
        FrameworkElement ancestor,
        double requestedWidth,
        double requestedHeight,
        string label)
    {
        var bounds = element.TransformToAncestor(ancestor)
            .TransformBounds(new Rect(element.RenderSize));
        const double tolerance = 0.75;
        if (bounds.Left < -tolerance
            || bounds.Top < -tolerance
            || bounds.Right > ancestor.ActualWidth + tolerance
            || bounds.Bottom > ancestor.ActualHeight + tolerance)
        {
            throw new InvalidDataException(
                $"{label} ultrapassou os limites em " +
                $"{requestedWidth:0}×{requestedHeight:0}: " +
                $"elemento={bounds}, ancestral=" +
                $"{ancestor.ActualWidth:0.##}×{ancestor.ActualHeight:0.##}.");
        }
    }

    private static void AssertHudButtonTemplateWithinButton(
        Button button,
        double requestedWidth,
        double requestedHeight)
    {
        button.ApplyTemplate();
        foreach (var templatePartName in new[] { "HudSurface", "HudRail", "HudCorner" })
        {
            if (button.Template.FindName(templatePartName, button) is not FrameworkElement templatePart)
                throw new InvalidDataException(
                    $"O botão HUD '{AutomationProperties.GetName(button)}' perdeu a peça {templatePartName}.");
            AssertElementWithinAncestor(
                templatePart,
                button,
                requestedWidth,
                requestedHeight,
                $"peça {templatePartName} do botão HUD '{AutomationProperties.GetName(button)}'");
        }
    }

    private static IEnumerable<T> FindVisualDescendants<T>(DependencyObject root)
        where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            if (child is T match) yield return match;
            foreach (var descendant in FindVisualDescendants<T>(child))
                yield return descendant;
        }
    }
}
