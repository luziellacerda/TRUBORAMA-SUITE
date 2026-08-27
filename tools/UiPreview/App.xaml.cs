using System.IO;
using System.Reflection;
using System.Security;
using System.Windows;

namespace Turborama.UiPreview;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        if (e.Args.Length != 0)
        {
            MessageBox.Show(
                "Este visualizador não aceita parâmetros de linha de comando.",
                "Turborama UI Preview",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            Shutdown(2);
            return;
        }

        var sourceRevision = ReadRequiredMetadata("Turborama.UiPreview.SourceRevision");
        if (!PreviewBuildInfo.IsCanonicalCommit(sourceRevision))
        {
            MessageBox.Show(
                "A prévia não possui uma identidade de build válida.",
                "Turborama UI Preview",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Shutdown(3);
            return;
        }

        try
        {
            var baseDirectory = LocalAssetPolicy.NormalizeBaseDirectory(
                AppContext.BaseDirectory);
            var credentialPath = Path.Combine(
                baseDirectory,
                PreviewCredentialVerifier.CredentialFileName);
            var login = new PreviewLoginWindow(credentialPath, sourceRevision);
            MainWindow = login;
            login.Show();
        }
        catch (Exception exception) when (exception is IOException
                                           or UnauthorizedAccessException
                                           or SecurityException
                                           or ArgumentException)
        {
            MessageBox.Show(
                "A prévia precisa estar em uma pasta física de um disco local.",
                "Turborama UI Preview",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Shutdown(4);
        }
    }

    private static string ReadRequiredMetadata(string key)
    {
        var value = typeof(App).Assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .SingleOrDefault(attribute =>
                string.Equals(attribute.Key, key, StringComparison.Ordinal))
            ?.Value;
        return value ?? string.Empty;
    }
}
