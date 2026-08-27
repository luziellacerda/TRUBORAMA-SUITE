using System.IO;
using System.Security;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Input;

namespace Turborama.UiPreview;

public partial class PreviewLoginWindow : Window
{
    private const int MaximumAttempts = 5;
    private readonly string _credentialPath;
    private readonly string _sourceRevision;
    private int _failedAttempts;
    private int _verificationActive;
    private bool _previewOpened;

    public PreviewLoginWindow(string credentialPath, string sourceRevision)
    {
        _credentialPath = Path.GetFullPath(
            credentialPath ?? throw new ArgumentNullException(nameof(credentialPath)));
        _sourceRevision = sourceRevision
                          ?? throw new ArgumentNullException(nameof(sourceRevision));
        InitializeComponent();
        Loaded += (_, _) => PasswordInput.Focus();
        Closed += PreviewLoginWindow_Closed;
    }

    private async void Enter_Click(object sender, RoutedEventArgs e)
        => await VerifyAndOpenAsync();

    private async void PasswordInput_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        await VerifyAndOpenAsync();
    }

    private async Task VerifyAndOpenAsync()
    {
        if (Interlocked.CompareExchange(ref _verificationActive, 1, 0) != 0)
            return;

        SecureString? password = null;
        try
        {
            if (_failedAttempts >= MaximumAttempts)
            {
                StatusText.Text = "LIMITE DE TENTATIVAS ATINGIDO";
                Close();
                return;
            }

            password = PasswordInput.SecurePassword.Copy();
            password.MakeReadOnly();
            PasswordInput.Clear();
            EnterButton.IsEnabled = false;
            PasswordInput.IsEnabled = false;
            StatusText.Text = "VALIDANDO ACESSO LOCAL...";

            var verification = await Task.Run(() =>
                PreviewCredentialVerifier.VerifyFile(
                    _credentialPath,
                    password,
                    _sourceRevision,
                    DateTimeOffset.UtcNow));
            if (!verification.IsValid)
            {
                _failedAttempts++;
                if (_failedAttempts >= MaximumAttempts)
                {
                    StatusText.Text = "ACESSO NEGADO; REABRA A FERRAMENTA";
                    await Task.Delay(TimeSpan.FromSeconds(1));
                    Close();
                    return;
                }

                StatusText.Text = "SENHA OU CREDENCIAL INVÁLIDA";
                var delaySeconds = Math.Min(8, 1 << (_failedAttempts - 1));
                await Task.Delay(TimeSpan.FromSeconds(delaySeconds));
                return;
            }

            StatusText.Text = "VALIDANDO INTEGRIDADE DO PACOTE...";
            await PreviewPackageIntegrity.VerifyAsync(
                AppContext.BaseDirectory,
                verification.ManifestSha256,
                _sourceRevision,
                verification.ExpiresAtUtc);
            StatusText.Text = "CARREGANDO CATÁLOGO LOCAL...";
            var catalog = await Task.Run(() =>
                PreviewCatalog.Load(AppContext.BaseDirectory));
            if (DateTimeOffset.UtcNow >= verification.ExpiresAtUtc)
                throw new InvalidOperationException("Preview credential expired.");

            var preview = new PreviewCatalogWindow(
                catalog,
                verification.ExpiresAtUtc);
            Application.Current.MainWindow = preview;
            preview.Show();
            _previewOpened = true;
            Close();
        }
        catch (Exception exception) when (exception is SecurityException
                                           or CryptographicException
                                           or InvalidOperationException
                                           or IOException
                                           or UnauthorizedAccessException
                                           or JsonException
                                           or FormatException
                                           or OverflowException)
        {
            StatusText.Text = "NÃO FOI POSSÍVEL ABRIR A PRÉVIA LOCAL";
        }
        finally
        {
            password?.Dispose();
            if (!_previewOpened && IsLoaded && _failedAttempts < MaximumAttempts)
            {
                EnterButton.IsEnabled = true;
                PasswordInput.IsEnabled = true;
                PasswordInput.Focus();
            }
            Volatile.Write(ref _verificationActive, 0);
        }
    }

    private void PreviewLoginWindow_Closed(object? sender, EventArgs e)
    {
        Closed -= PreviewLoginWindow_Closed;
        PasswordInput.Clear();
        if (!_previewOpened) Application.Current.Shutdown();
    }
}
