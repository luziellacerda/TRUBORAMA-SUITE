using System.IO;
using System.Net.Http;
using System.Security;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using TurboBoxManager.Licensing;
using TurboBoxManager.Catalog;

namespace TurboBoxManager;

public partial class PremiumLoginWindow : Window
{
    private readonly SuiteLicensingRuntime _licensingRuntime;
    private CancellationTokenSource? _activationCancellation;
    private bool _runtimeTransferred;

    public PremiumLoginWindow()
    {
        InitializeComponent();
        _licensingRuntime = SuiteLicensingFactory.CreateDefault();
        Closed += PremiumLoginWindow_Closed;
        SetAuthorityStatus();
    }

    private async void Enter_Click(object sender, RoutedEventArgs e)
    {
        if (_activationCancellation is not null) return;

        var licenseId = LicenseInput.Text;
        if (string.IsNullOrWhiteSpace(licenseId)
            || !licenseId.Equals(licenseId.Trim(), StringComparison.Ordinal))
        {
            ShowStatus("INFORME O ID EXATO DA LICENÇA");
            return;
        }
        if (!_licensingRuntime.IsAvailable)
        {
            ShowStatus("AUTORIDADE DO TURBORAMA SUITE INDISPONÍVEL NESTA COMPILAÇÃO");
            SetAuthorityStatus();
            return;
        }

        var activationCode = ActivationCodeInput.Password;
        ActivationCodeInput.Clear();
        var cancellation = new CancellationTokenSource();
        _activationCancellation = cancellation;
        EnterButton.IsEnabled = false;
        LicenseInput.IsEnabled = false;
        ActivationCodeInput.IsEnabled = false;
        ShowStatus(string.IsNullOrEmpty(activationCode)
            ? "VALIDANDO DISPOSITIVO JÁ ATIVADO..."
            : "ATIVANDO DISPOSITIVO E ABRINDO SESSÃO...");

        try
        {
            var context = string.IsNullOrEmpty(activationCode)
                ? await _licensingRuntime.OpenAsync(licenseId, cancellation.Token)
                : await _licensingRuntime.ActivateAndOpenAsync(
                    licenseId,
                    activationCode,
                    cancellation.Token);
            context.ThrowIfUnauthorized();

            var publicManifestPath = Path.Combine(
                AppContext.BaseDirectory,
                "Assets",
                "Catalog",
                "catalog.json");
            var publicCatalog = CatalogRepository.Load(publicManifestPath);
            var authorizedCatalog = await _licensingRuntime
                .ReadAuthorizedCatalogAsync(
                    context,
                    publicCatalog.Items,
                    cancellation.Token);
            context.ThrowIfUnauthorized();

            var store = new StoreWindow(
                context,
                _licensingRuntime,
                authorizedCatalog);
            store.Show();
            _runtimeTransferred = true;
            Close();
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            ShowStatus("VALIDAÇÃO CANCELADA");
        }
        catch (SuiteLicensingUnavailableException)
        {
            ShowStatus("AUTORIDADE DO TURBORAMA SUITE INDISPONÍVEL");
        }
        catch (SuiteActivationIndeterminateException)
        {
            ShowStatus("NÃO FOI POSSÍVEL CONFIRMAR A ATIVAÇÃO; TENTE ENTRAR SEM O CÓDIGO");
        }
        catch (Exception exception) when (exception is SuiteApiException
                                           or SuiteAuthorizationException
                                           or SecurityException)
        {
            ShowStatus("LICENÇA, CÓDIGO OU DISPOSITIVO NÃO AUTORIZADO");
        }
        catch (Exception exception) when (exception is HttpRequestException
                                           or TaskCanceledException)
        {
            ShowStatus("SERVIÇO DE LICENCIAMENTO TEMPORARIAMENTE INDISPONÍVEL");
        }
        finally
        {
            activationCode = string.Empty;
            if (ReferenceEquals(_activationCancellation, cancellation))
                _activationCancellation = null;
            cancellation.Dispose();
            if (!_runtimeTransferred)
            {
                EnterButton.IsEnabled = true;
                LicenseInput.IsEnabled = true;
                ActivationCodeInput.IsEnabled = true;
            }
        }
    }

    private void SetAuthorityStatus()
    {
        if (_licensingRuntime.IsAvailable)
        {
            ServiceStatusText.Text = "CONFIGURAÇÃO DA AUTORIDADE VÁLIDA";
            OnlineDot.Fill = new SolidColorBrush(Color.FromRgb(115, 255, 104));
        }
        else
        {
            ServiceStatusText.Text = "AUTORIDADE NÃO CONFIGURADA";
            OnlineDot.Fill = new SolidColorBrush(Color.FromRgb(107, 116, 110));
        }
    }

    private void ShowStatus(string message)
    {
        StatusText.Text = message;
        StatusText.Visibility = Visibility.Visible;
    }

    private async void PremiumLoginWindow_Closed(object? sender, EventArgs e)
    {
        Closed -= PremiumLoginWindow_Closed;
        _activationCancellation?.Cancel();
        if (!_runtimeTransferred)
        {
            try { await _licensingRuntime.DisposeAsync(); }
            catch { }
        }
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2) ToggleMaximize(); else DragMove();
    }

    private void ToggleMaximize() => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void Maximize_Click(object sender, RoutedEventArgs e) => ToggleMaximize();
    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
