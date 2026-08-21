using System.IO;
using System.Text.Json;

namespace TurboBoxManager;

internal static class LocalDataPaths
{
    public static string Root { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Turborama");
    private static string LegacyRoot { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "SamboxManagerBase");

    public static string KeyFile => Path.Combine(Root, "key.txt");
    public static string ConfigFile => Path.Combine(Root, "config.json");
    private static string LegacyKeyFile => Path.Combine(LegacyRoot, "key.txt");
    private static string LegacyConfigFile => Path.Combine(LegacyRoot, "config.json");
    private static string PackagedKeyFile => Path.Combine(AppContext.BaseDirectory, "Data", "key.txt");
    private static string PackagedConfigFile => Path.Combine(AppContext.BaseDirectory, "Data", "config.json");

    public static string? ReadKey()
    {
        try
        {
            var path = File.Exists(KeyFile)
                ? KeyFile
                : File.Exists(LegacyKeyFile) ? LegacyKeyFile : PackagedKeyFile;
            return File.Exists(path) ? File.ReadAllText(path).Trim() : null;
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    public static string? ReadInstallFolder()
    {
        try
        {
            var configPath = File.Exists(ConfigFile)
                ? ConfigFile
                : File.Exists(LegacyConfigFile) ? LegacyConfigFile : PackagedConfigFile;
            if (!File.Exists(configPath)) return null;
            using var document = JsonDocument.Parse(File.ReadAllText(configPath));
            if (!document.RootElement.TryGetProperty("InstallFolder", out var value)) return null;
            var path = value.GetString();
            return !string.IsNullOrWhiteSpace(path) && Directory.Exists(path) ? path : null;
        }
        catch (Exception exception) when (exception is IOException
                                           or UnauthorizedAccessException
                                           or JsonException)
        {
            return null;
        }
    }
}
