using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace TurboBoxManager;

internal static class LocalDataPaths
{
    private const int MaximumConfigurationBytes = 64 * 1024;
    private static readonly object ConfigurationGate = new();
    private static readonly JsonSerializerOptions ConfigurationJsonOptions = new()
    {
        WriteIndented = true
    };
    private static readonly string[] ConfigurationPropertyNames =
        ["InstallFolder", "GameLibraryFolder"];
    private static readonly HashSet<string> AllowedProperties =
        new(ConfigurationPropertyNames, StringComparer.Ordinal);

    public static string Root { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Turborama");
    public static string ConfigFile => Path.Combine(Root, "suite-config.json");
    private static string LegacyConfigFile => Path.Combine(Root, "config.json");
    private static string HistoricLegacyRoot { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "SamboxManagerBase");
    private static string HistoricLegacyConfigFile =>
        Path.Combine(HistoricLegacyRoot, "config.json");

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
            lock (ConfigurationGate)
            {
                string? path = null;
                if (File.Exists(ConfigFile))
                {
                    using var rootLease = OpenConfigurationRoot(createIfMissing: false);
                    path = ReadSuiteFolderSetting(propertyName, rootLease);
                }
                path ??= ReadLegacyFolderSetting(propertyName);
                return string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path)
                    ? null
                    : Path.GetFullPath(path);
            }
        }
        catch (Exception exception) when (exception is IOException
                                           or UnauthorizedAccessException
                                           or InvalidDataException
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

            lock (ConfigurationGate)
            {
                using var configurationRoot = OpenConfigurationRoot(createIfMissing: true);
                var lockPath = Path.Combine(Root, ".config.lock");
                using var rootLease = configurationRoot.OpenFile(
                    lockPath,
                    FileMode.OpenOrCreate,
                    FileAccess.ReadWrite,
                    FileShare.None,
                    1,
                    FileOptions.WriteThrough);
                _ = PathIdentity.CaptureFileIdentity(rootLease.SafeFileHandle, lockPath);
                configurationRoot.Revalidate();

                JsonObject config;
                if (File.Exists(ConfigFile))
                {
                    using var document = ReadConfigurationDocument(
                        ConfigFile,
                        configurationRoot);
                    ValidateConfigurationShape(document.RootElement);
                    config = new JsonObject();
                    foreach (var property in document.RootElement.EnumerateObject())
                        config[property.Name] = property.Value.GetString();
                }
                else
                {
                    config = new JsonObject();
                }

                MergeMissingLegacySettings(config);
                config[propertyName] = canonicalFolder;
                var bytes = JsonSerializer.SerializeToUtf8Bytes(config, ConfigurationJsonOptions);
                if (bytes.Length > MaximumConfigurationBytes)
                    throw new InvalidDataException("O arquivo de configuração excedeu o limite seguro.");

                var temporaryPath = ConfigFile + ".tmp-" + Guid.NewGuid().ToString("N");
                try
                {
                    using (var output = configurationRoot.OpenFile(
                               temporaryPath,
                               FileMode.CreateNew,
                               FileAccess.ReadWrite,
                               FileShare.None,
                               4 * 1024,
                               FileOptions.WriteThrough,
                               deleteAccess: true))
                    {
                        var temporaryIdentity = PathIdentity.CaptureFileIdentity(
                            output.SafeFileHandle,
                            temporaryPath);
                        output.Write(bytes);
                        output.Flush(flushToDisk: true);
                        _ = PathIdentity.RevalidateFile(
                            output.SafeFileHandle,
                            temporaryPath,
                            temporaryIdentity);
                        configurationRoot.Revalidate();
                        _ = PathIdentity.RenameByHandle(
                            output.SafeFileHandle,
                            temporaryIdentity,
                            configurationRoot.AnchorHandle,
                            Root,
                            Path.GetFileName(ConfigFile),
                            replaceIfExists: true);
                    }
                    configurationRoot.Revalidate();
                    return true;
                }
                finally
                {
                    try { _ = PathIdentity.DeleteFileExact(temporaryPath, Root); }
                    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                    {
                    }
                }
            }
        }
        catch (Exception exception) when (exception is IOException
                                           or UnauthorizedAccessException
                                           or InvalidDataException
                                           or JsonException
                                           or ArgumentException
                                           or NotSupportedException)
        {
            return false;
        }
    }

    private static string? ReadSuiteFolderSetting(
        string propertyName,
        PathIdentity.DirectoryTreeLease rootLease)
    {
        using var document = ReadConfigurationDocument(ConfigFile, rootLease);
        ValidateConfigurationShape(document.RootElement);
        return document.RootElement.TryGetProperty(propertyName, out var value)
            ? value.GetString()
            : null;
    }

    private static string? ReadLegacyFolderSetting(string propertyName)
    {
        foreach (var legacyPath in new[] { LegacyConfigFile, HistoricLegacyConfigFile })
        {
            if (!File.Exists(legacyPath)) continue;
            using var rootLease = OpenLegacyConfigurationRoot(
                Path.GetDirectoryName(legacyPath)!);
            using var document = ReadConfigurationDocument(legacyPath, rootLease);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                throw new JsonException("O arquivo de configuração legado não é um objeto JSON.");

            string? value = null;
            var found = false;
            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (!property.NameEquals(propertyName)) continue;
                if (found || property.Value.ValueKind != JsonValueKind.String)
                    throw new JsonException(
                        "A configuração legada contém campo duplicado ou inválido.");
                value = property.Value.GetString();
                found = true;
            }
            if (found) return value;
        }
        return null;
    }

    private static void MergeMissingLegacySettings(JsonObject config)
    {
        foreach (var propertyName in ConfigurationPropertyNames)
        {
            if (config.ContainsKey(propertyName)) continue;
            var legacyValue = ReadLegacyFolderSetting(propertyName);
            if (string.IsNullOrWhiteSpace(legacyValue)
                || !Path.IsPathFullyQualified(legacyValue))
                continue;
            config[propertyName] = Path.GetFullPath(legacyValue);
        }
    }

    private static JsonDocument ReadConfigurationDocument(
        string path,
        PathIdentity.DirectoryTreeLease rootLease)
    {
        rootLease.Revalidate();
        using var stream = rootLease.OpenFile(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            4 * 1024,
            FileOptions.SequentialScan);
        var identity = PathIdentity.CaptureFileIdentity(stream.SafeFileHandle, path);
        if (stream.Length is <= 0 or > MaximumConfigurationBytes)
            throw new InvalidDataException("O arquivo de configuração possui tamanho inválido.");
        var document = JsonDocument.Parse(stream, new JsonDocumentOptions
        {
            AllowTrailingCommas = false,
            CommentHandling = JsonCommentHandling.Disallow,
            MaxDepth = 4
        });
        _ = PathIdentity.RevalidateFile(stream.SafeFileHandle, path, identity);
        rootLease.Revalidate();
        return document;
    }

    private static void ValidateConfigurationShape(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
            throw new JsonException("O arquivo de configuração não é um objeto JSON.");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in root.EnumerateObject())
        {
            if (!AllowedProperties.Contains(property.Name)
                || !seen.Add(property.Name)
                || property.Value.ValueKind != JsonValueKind.String)
                throw new JsonException(
                    "O arquivo de configuração contém campo desconhecido, duplicado ou inválido.");
        }
    }

    private static PathIdentity.DirectoryTreeLease OpenConfigurationRoot(bool createIfMissing)
    {
        var applicationData = Path.GetFullPath(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData));
        var canonicalRoot = Path.GetFullPath(Root);
        var prefix = Path.TrimEndingDirectorySeparator(applicationData)
                     + Path.DirectorySeparatorChar;
        if (!canonicalRoot.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("A raiz de configuração saiu do perfil do usuário.");
        if (!Directory.Exists(applicationData))
            throw new DirectoryNotFoundException("A raiz de configuração não está disponível.");
        using var applicationDataLease = PathIdentity.OpenDirectoryTree(applicationData);
        applicationDataLease.Revalidate();
        return PathIdentity.OpenDirectoryTree(canonicalRoot, createIfMissing);
    }

    private static PathIdentity.DirectoryTreeLease OpenLegacyConfigurationRoot(string root)
    {
        var applicationData = Path.GetFullPath(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData));
        var canonicalRoot = Path.GetFullPath(root);
        var prefix = Path.TrimEndingDirectorySeparator(applicationData)
                     + Path.DirectorySeparatorChar;
        if (!canonicalRoot.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            || !Directory.Exists(applicationData)
            || !Directory.Exists(canonicalRoot))
            throw new InvalidDataException("A configuração legada saiu do perfil do usuário.");
        using var applicationDataLease = PathIdentity.OpenDirectoryTree(applicationData);
        applicationDataLease.Revalidate();
        return PathIdentity.OpenDirectoryTree(canonicalRoot);
    }

    private static void EnsurePlainDirectory(string path)
    {
        var attributes = File.GetAttributes(path);
        if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint))
            != FileAttributes.Directory)
            throw new InvalidDataException("A configuração não pode usar diretórios redirecionados.");
    }

    private static void EnsurePlainFile(string path)
    {
        var attributes = File.GetAttributes(path);
        if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
            throw new InvalidDataException("A configuração não pode usar links ou diretórios.");
    }
}
