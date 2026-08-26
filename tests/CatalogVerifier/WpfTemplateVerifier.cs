using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace TurboBoxManager.CatalogVerifier;

internal static class WpfTemplateVerifier
{
    public static void Run(string categoryId)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            Application? application = null;
            StoreWindow? window = null;
            try
            {
                application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                window = new StoreWindow(skipLogin: true)
                {
                    ShowActivated = false,
                    ShowInTaskbar = false,
                    Left = -12000,
                    Top = -12000,
                    Width = 1360,
                    Height = 728,
                    Opacity = 0
                };

                var category = window.CatalogCategories.Single(item =>
                    item.Id.Equals(categoryId, StringComparison.OrdinalIgnoreCase));
                var openCatalog = typeof(StoreWindow).GetMethod(
                                      "OpenCatalog",
                                      BindingFlags.Instance | BindingFlags.NonPublic)
                                  ?? throw new MissingMethodException(
                                      nameof(StoreWindow),
                                  "OpenCatalog");
                if (window.LibrarySystems.Count != 22 || window.LibraryTotalItemCount != 850)
                    throw new InvalidDataException("A Biblioteca precisa contabilizar 22 sistemas e 850 jogos.");

                var ps3 = window.CatalogCategories.Single(item =>
                    item.Id.Equals("playstation-3", StringComparison.OrdinalIgnoreCase));
                openCatalog.Invoke(window, [ps3]);
                if (window.Resources["CurrentSystemSidebarBrush"] is not SolidColorBrush ps3Sidebar
                    || ps3Sidebar.Color.R > 8 || ps3Sidebar.Color.G > 8 || ps3Sidebar.Color.B > 8)
                    throw new InvalidDataException("O menu lateral do PlayStation 3 precisa permanecer preto.");
                if (window.Resources["CurrentSystemVideoOverlayBrush"] is not LinearGradientBrush overlay
                    || overlay.GradientStops.Count < 2
                    || overlay.GradientStops[0].Color.A != 255
                    || overlay.GradientStops[^1].Color.A != 0)
                    throw new InvalidDataException("O vídeo precisa do degradê preto para transparente.");

                openCatalog.Invoke(window, [category]);
                window.Show();
                window.Dispatcher.Invoke(
                    () => window.UpdateLayout(),
                    DispatcherPriority.ApplicationIdle);
                VerifyResponsiveBackgroundVideo(window);
                window.Close();
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
        if (!thread.Join(TimeSpan.FromSeconds(20)))
            throw new TimeoutException("O catálogo WPF não concluiu a renderização de teste.");
        if (failure is not null)
            throw new InvalidOperationException(
                "O catálogo WPF falhou ao criar os templates reais.",
                failure);
    }

    private static void VerifyResponsiveBackgroundVideo(StoreWindow window)
    {
        if (window.FindName("RetroSystemVideoBackground") is not Grid background
            || window.FindName("RetroUniversalVideoPlayerHost") is not Grid host)
            throw new InvalidDataException("A área responsiva do vídeo de fundo não foi criada.");
        if (!background.ClipToBounds
            || !host.ClipToBounds
            || host.HorizontalAlignment != HorizontalAlignment.Stretch
            || host.VerticalAlignment != VerticalAlignment.Stretch)
            throw new InvalidDataException("O vídeo de fundo precisa preencher e recortar a área disponível.");

        var factory = typeof(StoreWindow).GetMethod(
                          "CreateResponsiveBackgroundVideoPlayer",
                          BindingFlags.Static | BindingFlags.NonPublic)
                      ?? throw new MissingMethodException(
                          nameof(StoreWindow),
                          "CreateResponsiveBackgroundVideoPlayer");
        var player = factory.Invoke(null, [host]) as MediaElement
                     ?? throw new InvalidDataException("O player responsivo não pôde ser criado.");
        host.Children.Add(player);
        try
        {
            foreach (var (width, height) in new[] { (1080d, 680d), (1600d, 900d) })
            {
                window.Width = width;
                window.Height = height;
                window.UpdateLayout();

                if (player.Stretch != Stretch.UniformToFill
                    || Math.Abs(player.Width - host.ActualWidth) > 0.5
                    || Math.Abs(player.Height - host.ActualHeight) > 0.5
                    || Math.Abs(host.ActualWidth - background.ActualWidth) > 0.5
                    || Math.Abs(host.ActualHeight - background.ActualHeight) > 0.5)
                    throw new InvalidDataException(
                        $"O vídeo de fundo não acompanhou a janela em {width:0}×{height:0}: " +
                        $"player={player.Width:0.##}×{player.Height:0.##}, " +
                        $"host={host.ActualWidth:0.##}×{host.ActualHeight:0.##}, " +
                        $"fundo={background.ActualWidth:0.##}×{background.ActualHeight:0.##}.");
            }
        }
        finally
        {
            host.Children.Remove(player);
        }
    }
}
