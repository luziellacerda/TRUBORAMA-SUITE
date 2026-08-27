using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;

namespace Turborama.UiPreview;

internal sealed record PreviewCatalogData(
    IReadOnlyList<PreviewCatalogCategory> Categories,
    IReadOnlyList<PreviewCatalogItem> Items,
    string DefaultImagePath);

internal sealed record PreviewCatalogCategory(
    string Id,
    string DisplayName,
    string ShortCode,
    string Glyph,
    string Description,
    string Accent,
    int Order,
    string IconPath,
    string BackgroundVideoPath,
    IReadOnlyList<PreviewCatalogItem> Items);

internal sealed record PreviewCatalogItem(
    string Id,
    string CategoryId,
    string Title,
    string Subtitle,
    string Badge,
    string Size,
    string Version,
    string Keywords,
    int Order,
    string ImagePath,
    string Description,
    string? VideoPath);

internal static class PreviewCatalog
{
    private const int ExpectedCategories = 22;
    private const int ExpectedItems = 850;
    private const int ExpectedDescriptions = 45;
    private const int ExpectedSystemVideos = 38;
    private const int ExpectedVideoMappings = 45;
    private const int ExpectedBackgroundVideos = 15;
    private const int MaximumCatalogBytes = 16 * 1024 * 1024;
    private const int MaximumDescriptionBytes = 4 * 1024 * 1024;
    private const int MaximumMapBytes = 512 * 1024;
    private const int MaximumImageBytes = 16 * 1024 * 1024;
    private const int MaximumIconBytes = 4 * 1024 * 1024;
    private const int MaximumVideoBytes = 128 * 1024 * 1024;
    private static readonly IReadOnlySet<string> JsonExtension =
        new HashSet<string>(StringComparer.Ordinal) { ".json" };
    private static readonly IReadOnlySet<string> JpegExtension =
        new HashSet<string>(StringComparer.Ordinal) { ".jpg" };
    private static readonly IReadOnlySet<string> PngExtension =
        new HashSet<string>(StringComparer.Ordinal) { ".png" };
    private static readonly IReadOnlySet<string> VideoExtension =
        new HashSet<string>(StringComparer.Ordinal) { ".mp4" };
    private static readonly IReadOnlyDictionary<string, string> BackgroundByCategory =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["system-tools"] = "Turborama-background-system-tools.mp4",
            ["emulators"] = "Turborama-background.mp4",
            ["retro-games"] = "Turborama-background-retro.mp4",
            ["nintendo-3ds"] = "Turborama-background-nintendo-generic.mp4",
            ["gamecube"] = "Turborama-background-nintendo-generic.mp4",
            ["nintendo-switch"] = "Turborama-background-nintendo-switch.mp4",
            ["nintendo-wii"] = "Turborama-background-nintendo-wii.mp4",
            ["nintendo-wii-u"] = "Turborama-background-nintendo-generic.mp4",
            ["playstation-1"] = "Turborama-background-playstation.mp4",
            ["playstation-2"] = "Turborama-background-ps2.mp4",
            ["playstation-2-br"] = "Turborama-background-ps2.mp4",
            ["playstation-3"] = "Turborama-background-playstation.mp4",
            ["playstation-4"] = "Turborama-background-ps4.mp4",
            ["playstation-5"] = "Turborama-background-ps5.mp4",
            ["ps-vita"] = "Turborama-background-ps-vita.mp4",
            ["psp"] = "Turborama-background-psp.mp4",
            ["sega-saturn"] = "Turborama-background-sega-saturn.mp4",
            ["windows"] = "Turborama-background-windows.mp4",
            ["xbox"] = "Turborama-background-xbox-one-x.mp4",
            ["xbox-360"] = "Turborama-background-xbox-one-x.mp4",
            ["xbox-one"] = "Turborama-background-xbox-one-x.mp4",
            ["xbox-series"] = "Turborama-background-xbox-one-x.mp4"
        };

    public static PreviewCatalogData Load(string packageDirectory)
    {
        var baseDirectory = LocalAssetPolicy.NormalizeBaseDirectory(packageDirectory);
        var descriptions = ReadStringMap(
            baseDirectory,
            "Assets/Catalog/platform-descriptions.json",
            MaximumDescriptionBytes,
            ExpectedDescriptions,
            requireHexKeys: true,
            maximumValueLength: 12_000);
        var systemVideoMap = ReadStringMap(
            baseDirectory,
            "Assets/Catalog/SystemVideos/system-videos.json",
            MaximumMapBytes,
            ExpectedVideoMappings,
            requireHexKeys: true,
            maximumValueLength: 80);

        var systemVideoNames = VerifyVideoIntegrity(
            baseDirectory,
            "Assets/Catalog/SystemVideos/system-video-integrity.json",
            "Assets/Catalog/SystemVideos/",
            ExpectedSystemVideos);
        if (!systemVideoMap.Values.ToHashSet(StringComparer.Ordinal)
                .SetEquals(systemVideoNames))
            throw new InvalidDataException("System video map is incomplete.");

        var backgroundVideoNames = VerifyVideoIntegrity(
            baseDirectory,
            "Assets/BackgroundVideos/background-video-integrity.json",
            "Assets/BackgroundVideos/",
            ExpectedBackgroundVideos);
        if (!BackgroundByCategory.Values.ToHashSet(StringComparer.Ordinal)
                .SetEquals(backgroundVideoNames))
            throw new InvalidDataException("Background video map is incomplete.");

        var catalogPath = LocalAssetPolicy.ResolveAssetFile(
            baseDirectory,
            "Assets/Catalog/catalog.json",
            "Assets/Catalog/",
            JsonExtension,
            MaximumCatalogBytes);
        var catalogBytes = LocalAssetPolicy.ReadBoundedFile(
            catalogPath,
            MaximumCatalogBytes);
        try
        {
            using var document = StrictJson.Parse(catalogBytes, maximumDepth: 12);
            var root = document.RootElement;
            StrictJson.RequireExactMembers(
                root,
                "schemaVersion",
                "defaultImage",
                "categories",
                "items",
                "packageTemplates");
            if (root.GetProperty("schemaVersion").GetInt32() != 3)
                throw new InvalidDataException("Unsupported catalog schema.");

            var packageTemplates = root.GetProperty("packageTemplates");
            if (packageTemplates.ValueKind != JsonValueKind.Array
                || packageTemplates.GetArrayLength() != 0)
                throw new InvalidDataException("Package templates are not allowed.");

            var defaultImageRelative = ReadText(
                root.GetProperty("defaultImage"),
                80);
            if (!defaultImageRelative.Equals(
                    "Images/_turborama-fallback.jpg",
                    StringComparison.Ordinal))
                throw new InvalidDataException("Unexpected default image.");
            var defaultImagePath = LocalAssetPolicy.ResolveAssetFile(
                baseDirectory,
                "Assets/Catalog/" + defaultImageRelative,
                "Assets/Catalog/Images/",
                JpegExtension,
                MaximumImageBytes);

            var categoriesElement = root.GetProperty("categories");
            var itemsElement = root.GetProperty("items");
            if (categoriesElement.ValueKind != JsonValueKind.Array
                || categoriesElement.GetArrayLength() != ExpectedCategories
                || itemsElement.ValueKind != JsonValueKind.Array
                || itemsElement.GetArrayLength() != ExpectedItems)
                throw new InvalidDataException("Catalog cardinality mismatch.");

            var categorySeeds = new List<CategorySeed>(ExpectedCategories);
            var categoryIds = new HashSet<string>(StringComparer.Ordinal);
            var categoryOrders = new HashSet<int>();
            foreach (var categoryElement in categoriesElement.EnumerateArray())
            {
                StrictJson.RequireExactMembers(
                    categoryElement,
                    "id",
                    "displayName",
                    "shortCode",
                    "glyph",
                    "description",
                    "accent",
                    "order",
                    "sourceItemCount");
                var id = ReadText(categoryElement.GetProperty("id"), 48);
                var order = categoryElement.GetProperty("order").GetInt32();
                var sourceItemCount = categoryElement
                    .GetProperty("sourceItemCount")
                    .GetInt32();
                if (!IsSlug(id)
                    || !categoryIds.Add(id)
                    || order is < 0 or > 100
                    || !categoryOrders.Add(order)
                    || sourceItemCount is < 1 or > ExpectedItems)
                    throw new InvalidDataException("Invalid category identity.");

                var accent = ReadText(categoryElement.GetProperty("accent"), 7);
                if (!IsAccent(accent))
                    throw new InvalidDataException("Invalid category accent.");
                if (!BackgroundByCategory.TryGetValue(id, out var backgroundName))
                    throw new InvalidDataException("Missing category background.");

                var iconPath = LocalAssetPolicy.ResolveAssetFile(
                    baseDirectory,
                    $"Assets/Catalog/MenuIcons/{id}.png",
                    "Assets/Catalog/MenuIcons/",
                    PngExtension,
                    MaximumIconBytes);
                var backgroundPath = LocalAssetPolicy.ResolveAssetFile(
                    baseDirectory,
                    "Assets/BackgroundVideos/" + backgroundName,
                    "Assets/BackgroundVideos/",
                    VideoExtension,
                    MaximumVideoBytes);
                categorySeeds.Add(new CategorySeed(
                    id,
                    ReadText(categoryElement.GetProperty("displayName"), 80),
                    ReadText(categoryElement.GetProperty("shortCode"), 8),
                    ReadText(categoryElement.GetProperty("glyph"), 4),
                    ReadText(categoryElement.GetProperty("description"), 500),
                    accent,
                    order,
                    sourceItemCount,
                    iconPath,
                    backgroundPath));
            }

            if (!categoryIds.SetEquals(BackgroundByCategory.Keys))
                throw new InvalidDataException("Category map mismatch.");

            var items = new List<PreviewCatalogItem>(ExpectedItems);
            var itemIds = new HashSet<string>(StringComparer.Ordinal);
            var itemOrders = new HashSet<string>(StringComparer.Ordinal);
            var categoryNames = categorySeeds.ToDictionary(
                category => category.Id,
                category => category.DisplayName,
                StringComparer.Ordinal);
            foreach (var itemElement in itemsElement.EnumerateArray())
            {
                StrictJson.RequireExactMembers(
                    itemElement,
                    "id",
                    "categoryId",
                    "title",
                    "subtitle",
                    "category",
                    "image",
                    "imageAltText",
                    "badge",
                    "size",
                    "version",
                    "keywords",
                    "downloadUrl",
                    "sha256",
                    "downloadFileExtension",
                    "order");
                var id = ReadText(itemElement.GetProperty("id"), 32);
                var categoryId = ReadText(
                    itemElement.GetProperty("categoryId"),
                    48);
                var order = itemElement.GetProperty("order").GetInt32();
                if (!IsLowerHex(id, 32)
                    || !itemIds.Add(id)
                    || !categoryNames.TryGetValue(categoryId, out var categoryName)
                    || order is < 0 or >= ExpectedItems
                    || !itemOrders.Add(categoryId + ":" + order.ToString(
                        CultureInfo.InvariantCulture)))
                    throw new InvalidDataException("Invalid catalog item identity.");

                if (!ReadText(itemElement.GetProperty("category"), 80)
                        .Equals(categoryName, StringComparison.Ordinal)
                    || ReadText(itemElement.GetProperty("downloadUrl"), 1).Length != 0
                    || ReadText(itemElement.GetProperty("sha256"), 1).Length != 0
                    || ReadText(itemElement.GetProperty("downloadFileExtension"), 1)
                        .Length != 0)
                    throw new InvalidDataException("Mutable catalog fields are not allowed.");
                _ = ReadText(itemElement.GetProperty("imageAltText"), 300);

                var imageRelative = ReadText(
                    itemElement.GetProperty("image"),
                    80);
                if (!imageRelative.Equals($"Images/{id}.jpg", StringComparison.Ordinal))
                    throw new InvalidDataException("Item image name is not canonical.");
                var imagePath = LocalAssetPolicy.ResolveAssetFile(
                    baseDirectory,
                    "Assets/Catalog/" + imageRelative,
                    "Assets/Catalog/Images/",
                    JpegExtension,
                    MaximumImageBytes);

                string? videoPath = null;
                if (systemVideoMap.TryGetValue(id, out var videoName))
                {
                    if (!IsMediaFileName(videoName))
                        throw new InvalidDataException("Invalid system video name.");
                    videoPath = LocalAssetPolicy.ResolveAssetFile(
                        baseDirectory,
                        "Assets/Catalog/SystemVideos/" + videoName,
                        "Assets/Catalog/SystemVideos/",
                        VideoExtension,
                        MaximumVideoBytes);
                }

                items.Add(new PreviewCatalogItem(
                    id,
                    categoryId,
                    ReadText(itemElement.GetProperty("title"), 180),
                    ReadText(itemElement.GetProperty("subtitle"), 240),
                    ReadText(itemElement.GetProperty("badge"), 12),
                    ReadText(itemElement.GetProperty("size"), 80),
                    ReadText(itemElement.GetProperty("version"), 80),
                    ReadText(itemElement.GetProperty("keywords"), 800),
                    order,
                    imagePath,
                    descriptions.GetValueOrDefault(id, string.Empty),
                    videoPath));
            }

            if (!descriptions.Keys.ToHashSet(StringComparer.Ordinal).IsSubsetOf(itemIds)
                || !systemVideoMap.Keys.ToHashSet(StringComparer.Ordinal).IsSubsetOf(itemIds))
                throw new InvalidDataException("Auxiliary map references an unknown item.");

            var expectedImages = itemIds
                .Select(id => id + ".jpg")
                .Append("_turborama-fallback.jpg")
                .ToHashSet(StringComparer.Ordinal);
            VerifyExactFiles(
                Path.Combine(baseDirectory, "Assets", "Catalog", "Images"),
                expectedImages,
                ".jpg");

            var categories = categorySeeds
                .OrderBy(category => category.Order)
                .Select(category =>
                {
                    var categoryItems = items
                        .Where(item => item.CategoryId.Equals(
                            category.Id,
                            StringComparison.Ordinal))
                        .OrderBy(item => item.Order)
                        .ToArray();
                    if (categoryItems.Length != category.SourceItemCount)
                        throw new InvalidDataException("Category item count mismatch.");
                    return new PreviewCatalogCategory(
                        category.Id,
                        category.DisplayName,
                        category.ShortCode,
                        category.Glyph,
                        category.Description,
                        category.Accent,
                        category.Order,
                        category.IconPath,
                        category.BackgroundVideoPath,
                        categoryItems);
                })
                .ToArray();
            return new PreviewCatalogData(categories, items, defaultImagePath);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(catalogBytes);
        }
    }

    private static Dictionary<string, string> ReadStringMap(
        string baseDirectory,
        string relativePath,
        int maximumBytes,
        int expectedCount,
        bool requireHexKeys,
        int maximumValueLength)
    {
        var path = LocalAssetPolicy.ResolveAssetFile(
            baseDirectory,
            relativePath,
            relativePath[..(relativePath.LastIndexOf('/') + 1)],
            JsonExtension,
            maximumBytes);
        var bytes = LocalAssetPolicy.ReadBoundedFile(path, maximumBytes);
        try
        {
            using var document = StrictJson.Parse(bytes, maximumDepth: 4);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || root.GetPropertyCount() != expectedCount)
                throw new InvalidDataException("Auxiliary map cardinality mismatch.");
            var map = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var property in root.EnumerateObject())
            {
                if (requireHexKeys && !IsLowerHex(property.Name, 32))
                    throw new InvalidDataException("Auxiliary map key is invalid.");
                var value = ReadText(property.Value, maximumValueLength);
                if (!map.TryAdd(property.Name, value))
                    throw new InvalidDataException("Duplicate auxiliary map key.");
            }
            return map;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    private static HashSet<string> VerifyVideoIntegrity(
        string baseDirectory,
        string manifestRelativePath,
        string videoPrefix,
        int expectedCount)
    {
        var manifestPath = LocalAssetPolicy.ResolveAssetFile(
            baseDirectory,
            manifestRelativePath,
            videoPrefix,
            JsonExtension,
            MaximumMapBytes);
        var bytes = LocalAssetPolicy.ReadBoundedFile(manifestPath, MaximumMapBytes);
        try
        {
            using var document = StrictJson.Parse(bytes, maximumDepth: 5);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || root.GetPropertyCount() != expectedCount)
                throw new InvalidDataException("Video integrity map cardinality mismatch.");

            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in root.EnumerateObject())
            {
                if (!IsMediaFileName(property.Name) || !names.Add(property.Name))
                    throw new InvalidDataException("Video integrity name is invalid.");
                StrictJson.RequireExactMembers(property.Value, "sha256", "length");
                var expectedLength = property.Value.GetProperty("length").GetInt64();
                var expectedHash = property.Value.GetProperty("sha256").GetString();
                if (expectedLength is <= 0 or > MaximumVideoBytes
                    || !IsLowerHex(expectedHash, 64))
                    throw new InvalidDataException("Video integrity value is invalid.");

                var videoPath = LocalAssetPolicy.ResolveAssetFile(
                    baseDirectory,
                    videoPrefix + property.Name,
                    videoPrefix,
                    VideoExtension,
                    MaximumVideoBytes);
                var info = new FileInfo(videoPath);
                if (info.Length != expectedLength)
                    throw new InvalidDataException("Video length mismatch.");
                using var stream = new FileStream(
                    videoPath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read,
                    bufferSize: 128 * 1024,
                    FileOptions.SequentialScan);
                var actualHash = SHA256.HashData(stream);
                var declaredHash = Convert.FromHexString(expectedHash!);
                try
                {
                    if (!CryptographicOperations.FixedTimeEquals(
                            actualHash,
                            declaredHash))
                        throw new InvalidDataException("Video hash mismatch.");
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(actualHash);
                    CryptographicOperations.ZeroMemory(declaredHash);
                }
            }
            return names;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    private static void VerifyExactFiles(
        string directoryPath,
        HashSet<string> expectedNames,
        string extension)
    {
        var normalized = LocalAssetPolicy.NormalizeBaseDirectory(directoryPath);
        var actualNames = LocalAssetPolicy.EnumeratePackageFiles(
                normalized,
                expectedNames.Count + 1)
            .Select(path =>
            {
                if (!Path.GetDirectoryName(path)!.Equals(
                        normalized,
                        StringComparison.OrdinalIgnoreCase)
                    || !Path.GetExtension(path).Equals(extension, StringComparison.Ordinal))
                    throw new InvalidDataException("Unexpected catalog image file.");
                return Path.GetFileName(path);
            })
            .ToHashSet(StringComparer.Ordinal);
        if (!actualNames.SetEquals(expectedNames))
            throw new InvalidDataException("Catalog image inventory mismatch.");
    }

    private static string ReadText(JsonElement element, int maximumLength)
    {
        if (element.ValueKind != JsonValueKind.String)
            throw new JsonException("String expected.");
        var value = element.GetString()
                    ?? throw new JsonException("Null string is not allowed.");
        if (value.Length > maximumLength || value.Any(char.IsControl))
            throw new JsonException("String is outside its limits.");
        return value;
    }

    private static bool IsSlug(string value)
        => value.Length is > 0 and <= 48
           && value[0] is >= 'a' and <= 'z'
           && value[^1] is >= 'a' and <= 'z' or >= '0' and <= '9'
           && value.All(character => character is >= 'a' and <= 'z'
                                      or >= '0' and <= '9'
                                      or '-');

    private static bool IsAccent(string value)
        => value.Length == 7
           && value[0] == '#'
           && value.Skip(1).All(character => character is >= '0' and <= '9'
                                          or >= 'A' and <= 'F');

    private static bool IsMediaFileName(string value)
        => value.Length is > 4 and <= 80
           && value.EndsWith(".mp4", StringComparison.Ordinal)
           && value[..^4].All(character => character is >= 'a' and <= 'z'
                                            or >= 'A' and <= 'Z'
                                            or >= '0' and <= '9'
                                            or '-');

    private static bool IsLowerHex(string? value, int length)
        => value is not null
           && value.Length == length
           && value.All(character => character is >= '0' and <= '9'
                                      or >= 'a' and <= 'f');

    private sealed record CategorySeed(
        string Id,
        string DisplayName,
        string ShortCode,
        string Glyph,
        string Description,
        string Accent,
        int Order,
        int SourceItemCount,
        string IconPath,
        string BackgroundVideoPath);
}
