using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using TurboBoxManager.Catalog;

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
    public static string? ReadKey()
    {
        try
        {
            if (File.Exists(KeyFile)) return File.ReadAllText(KeyFile).Trim();
            if (File.Exists(LegacyKeyFile)) return File.ReadAllText(LegacyKeyFile).Trim();
            return PrivateCatalogResource.TryReadPackagedKey();
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    public static string? ReadInstallFolder()
    {
        var folder = ReadFolderSetting("InstallFolder");
        return folder is not null && Directory.Exists(folder) ? folder : null;
    }

    public static string? ReadGameLibraryFolder() => ReadFolderSetting("GameLibraryFolder");

    private static string? ReadFolderSetting(string propertyName)
    {
        try
        {
            var configPath = File.Exists(ConfigFile) ? ConfigFile : LegacyConfigFile;
            if (!File.Exists(configPath)) return null;
            using var document = JsonDocument.Parse(File.ReadAllText(configPath));
            if (!document.RootElement.TryGetProperty(propertyName, out var value)) return null;
            var path = value.GetString();
            return string.IsNullOrWhiteSpace(path) ? null : Path.GetFullPath(path);
        }
        catch (Exception exception) when (exception is IOException
                                           or UnauthorizedAccessException
                                           or JsonException
                                           or ArgumentException
                                           or NotSupportedException)
        {
            return null;
        }
    }

    public static bool WriteInstallFolder(string installFolder) =>
        WriteFolderSetting("InstallFolder", installFolder);

    public static bool WriteGameLibraryFolder(string gameLibraryFolder) =>
        Catalog.CatalogArchiveExtractor.IsGameLibraryRoot(gameLibraryFolder)
            && WriteFolderSetting("GameLibraryFolder", gameLibraryFolder);

    private static bool WriteFolderSetting(
        string propertyName,
        string folder,
        string? expectedFolderName = null)
    {
        try
        {
            var canonicalFolder = Path.GetFullPath(folder);
            if (!Directory.Exists(canonicalFolder)) return false;
            if (expectedFolderName is not null
                && !Path.GetFileName(Path.TrimEndingDirectorySeparator(canonicalFolder)).Equals(
                    expectedFolderName,
                    StringComparison.OrdinalIgnoreCase))
                return false;

            Directory.CreateDirectory(Root);
            var sourcePath = File.Exists(ConfigFile) ? ConfigFile : LegacyConfigFile;
            JsonObject config;
            if (File.Exists(sourcePath))
            {
                config = JsonNode.Parse(File.ReadAllText(sourcePath)) as JsonObject
                         ?? throw new JsonException("O arquivo de configuração não é um objeto JSON.");
            }
            else
            {
                config = new JsonObject();
            }

            config[propertyName] = canonicalFolder;
            var temporaryPath = ConfigFile + ".tmp-" + Guid.NewGuid().ToString("N");
            File.WriteAllText(temporaryPath, config.ToJsonString(new JsonSerializerOptions
            {
                WriteIndented = true
            }));
            File.Move(temporaryPath, ConfigFile, overwrite: true);
            return true;
        }
        catch (Exception exception) when (exception is IOException
                                           or UnauthorizedAccessException
                                           or JsonException
                                           or ArgumentException
                                           or NotSupportedException)
        {
            return false;
        }
    }
}
