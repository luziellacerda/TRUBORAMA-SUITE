using System.Reflection;
using System.Windows;
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
}
