using System.IO;
using System.Text.Json;

namespace TurboBoxManager;

internal static class LocalDataPaths
{
    public static string Root { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "SamboxManagerBase");

    public static string KeyFile => Path.Combine(Root, "key.txt");
    public static string ConfigFile => Path.Combine(Root, "config.json");

    public static string? ReadKey()
    {
        try
        {
            return File.Exists(KeyFile) ? File.ReadAllText(KeyFile).Trim() : null;
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    public static string? ReadInstallFolder()
    {
        try
        {
            if (!File.Exists(ConfigFile)) return null;
            using var document = JsonDocument.Parse(File.ReadAllText(ConfigFile));
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
