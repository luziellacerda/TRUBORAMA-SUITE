using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using TurboBoxManager.Licensing;

namespace TurboBoxManager.Catalog;

public sealed class CatalogRepository
{
    private const int MaximumManifestBytes = 8 * 1024 * 1024;
    private const int MaximumCategoryCount = 256;
    private const int MaximumItemCount = 100_000;
    private const int MaximumPackageTemplateCount = 10_000;

    private static readonly JsonSerializerOptions ManifestJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        ReadCommentHandling = JsonCommentHandling.Disallow,
        AllowTrailingCommas = false,
        MaxDepth = 32,
        NumberHandling = JsonNumberHandling.Strict,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        WriteIndented = false
    };

    private readonly ReadOnlyCollection<CatalogCategory> _categories;
    private readonly ReadOnlyCollection<CatalogItem> _items;
    private readonly IReadOnlyDictionary<string, CatalogItem> _itemsById;

    private CatalogRepository(
        CatalogManifest manifest,
        string manifestPath,
        bool usePackResources,
        IReadOnlyDictionary<string, CatalogArtifactDescriptor> authorizedArtifacts,
        IReadOnlyDictionary<string, string> maintenanceItems,
        bool requireCompleteCoverage)
    {
        Validate(manifest);
        ValidateAuthorizedArtifacts(
            manifest, authorizedArtifacts, maintenanceItems,
            requireCompleteCoverage);
        _categories = Array.AsReadOnly(
            manifest.Categories.OrderBy(category => category.Order).ToArray());

        var imageResolver = new CatalogImageResolver(
            manifestPath,
            manifest.DefaultImage,
            usePackResources);
        var descriptions = CatalogGameDescriptionStore.Load(manifestPath);
        _items = Array.AsReadOnly(MaterializeItems(
            manifest, imageResolver, descriptions, authorizedArtifacts,
            maintenanceItems));
        _itemsById = _items.ToDictionary(item => item.Id, StringComparer.OrdinalIgnoreCase);
    }

    public IReadOnlyList<CatalogCategory> Categories => _categories;

    public int ItemCount => _items.Count;

    public IReadOnlyList<CatalogItem> Items => _items;

    public static CatalogRepository Load(string manifestPath)
        => Load(manifestPath,
            new Dictionary<string, CatalogArtifactDescriptor>(StringComparer.Ordinal),
            requireCompleteCoverage: false);

    internal static CatalogRepository Load(
        string manifestPath,
        IReadOnlyDictionary<string, CatalogArtifactDescriptor> authorizedArtifacts,
        bool requireCompleteCoverage = true)
        => Load(
            manifestPath,
            authorizedArtifacts,
            new Dictionary<string, string>(StringComparer.Ordinal),
            requireCompleteCoverage);

    internal static CatalogRepository Load(
        string manifestPath,
        IReadOnlyDictionary<string, CatalogArtifactDescriptor> authorizedArtifacts,
        IReadOnlyDictionary<string, string> maintenanceItems,
        bool requireCompleteCoverage = true)
    {
        if (string.IsNullOrWhiteSpace(manifestPath))
            throw new ArgumentException("Informe o manifesto do catálogo.", nameof(manifestPath));
        var canonicalPath = Path.GetFullPath(manifestPath);
        if (HasReparsePointInPath(canonicalPath))
            throw new InvalidDataException(
                "O caminho do manifesto do catálogo não pode atravessar links ou reparse points.");

        var info = new FileInfo(canonicalPath);
        info.Refresh();
        if (!info.Exists)
            throw new FileNotFoundException("O manifesto do catálogo não foi encontrado.", canonicalPath);
        if (info.Length is <= 0 or > MaximumManifestBytes)
            throw new InvalidDataException("O manifesto do catálogo possui tamanho inválido.");

        using var stream = new FileStream(
            canonicalPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            128 * 1024,
            FileOptions.SequentialScan);
        if (HasReparsePointInPath(canonicalPath))
            throw new InvalidDataException(
                "O caminho do manifesto do catálogo mudou durante a abertura.");
        return Load(stream, canonicalPath, usePackResources: false,
            authorizedArtifacts, maintenanceItems, requireCompleteCoverage);
    }

    public static CatalogRepository Load(
        Stream manifestStream,
        string resourceBaseManifestPath,
        bool usePackResources = false)
        => Load(manifestStream, resourceBaseManifestPath, usePackResources,
            new Dictionary<string, CatalogArtifactDescriptor>(StringComparer.Ordinal),
            new Dictionary<string, string>(StringComparer.Ordinal),
            requireCompleteCoverage: false);

    internal static CatalogRepository Load(
        Stream manifestStream,
        string resourceBaseManifestPath,
        bool usePackResources,
        IReadOnlyDictionary<string, CatalogArtifactDescriptor> authorizedArtifacts,
        IReadOnlyDictionary<string, string> maintenanceItems,
        bool requireCompleteCoverage = true)
    {
        ArgumentNullException.ThrowIfNull(manifestStream);
        ArgumentNullException.ThrowIfNull(authorizedArtifacts);
        ArgumentNullException.ThrowIfNull(maintenanceItems);
        var bytes = ReadBoundedManifest(manifestStream);
        CatalogManifest manifest;
        try
        {
            ValidateJsonStructure(bytes);
            manifest = JsonSerializer.Deserialize<CatalogManifest>(
                           bytes,
                           ManifestJsonOptions)
                       ?? throw new InvalidDataException("O manifesto do catálogo está vazio.");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("O manifesto do catálogo não usa JSON estrito.", exception);
        }

        return new CatalogRepository(manifest, resourceBaseManifestPath,
            usePackResources, authorizedArtifacts, maintenanceItems,
            requireCompleteCoverage);
    }

    private static byte[] ReadBoundedManifest(Stream input)
    {
        if (!input.CanRead)
            throw new InvalidDataException("O manifesto do catálogo não pode ser lido.");
        if (input.CanSeek && input.Length - input.Position is <= 0 or > MaximumManifestBytes)
            throw new InvalidDataException("O manifesto do catálogo possui tamanho inválido.");

        using var output = new MemoryStream();
        var buffer = new byte[128 * 1024];
        while (true)
        {
            var read = input.Read(buffer, 0, buffer.Length);
            if (read == 0) break;
            if (output.Length + read > MaximumManifestBytes)
                throw new InvalidDataException("O manifesto do catálogo excede o limite permitido.");
            output.Write(buffer, 0, read);
        }
        if (output.Length == 0)
            throw new InvalidDataException("O manifesto do catálogo está vazio.");
        return output.ToArray();
    }

    private static void ValidateJsonStructure(ReadOnlySpan<byte> utf8)
    {
        using var document = JsonDocument.Parse(utf8.ToArray(), new JsonDocumentOptions
        {
            AllowTrailingCommas = false,
            CommentHandling = JsonCommentHandling.Disallow,
            MaxDepth = 32
        });
        if (document.RootElement.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("A raiz do manifesto do catálogo precisa ser um objeto.");
        if (!document.RootElement.TryGetProperty("schemaVersion", out var schemaVersion)
            || schemaVersion.ValueKind != JsonValueKind.Number
            || !schemaVersion.TryGetInt32(out _))
            throw new InvalidDataException(
                "O manifesto do catálogo precisa declarar schemaVersion como inteiro.");

        ValidateJsonElement(document.RootElement);
    }

    private static void ValidateJsonElement(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Null)
            throw new InvalidDataException(
                "O manifesto do catálogo não pode conter valores nulos.");

        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name))
                    throw new InvalidDataException(
                        $"O manifesto contém a propriedade duplicada '{property.Name}'.");
                ValidateJsonElement(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var child in element.EnumerateArray())
                ValidateJsonElement(child);
        }
    }

    public CatalogItem? FindById(string itemId) =>
        _itemsById.GetValueOrDefault(itemId);

    public CatalogQueryResult Query(string categoryId, string? searchText, int requestedPage, int pageSize)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(pageSize, 1);

        var normalizedSearch = Normalize(searchText ?? string.Empty);
        var filtered = _items
            .Where(item => item.CategoryId.Equals(categoryId, StringComparison.OrdinalIgnoreCase))
            .Where(item => normalizedSearch.Length == 0
                           || Normalize($"{item.Title} {item.Subtitle} {item.Category} {item.Keywords} {item.Version} {item.Size}")
                               .Contains(normalizedSearch, StringComparison.Ordinal))
            .OrderBy(item => item.Order)
            .ThenBy(item => item.Title, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();

        var totalPages = Math.Max(1, (int)Math.Ceiling(filtered.Length / (double)pageSize));
        var currentPage = Math.Clamp(requestedPage, 1, totalPages);
        var pageItems = Array.AsReadOnly(
            filtered.Skip((currentPage - 1) * pageSize).Take(pageSize).ToArray());

        return new CatalogQueryResult(pageItems, filtered.Length, currentPage, totalPages);
    }

    private static CatalogItem[] MaterializeItems(
        CatalogManifest manifest,
        CatalogImageResolver imageResolver,
        IReadOnlyDictionary<string, string> descriptions,
        IReadOnlyDictionary<string, CatalogArtifactDescriptor> authorizedArtifacts,
        IReadOnlyDictionary<string, string> maintenanceItems)
    {
        var definitions = manifest.Items.Count > 0
            ? manifest.Items
            : ExpandLegacyItems(manifest);
        var categories = manifest.Categories.ToDictionary(category => category.Id, StringComparer.OrdinalIgnoreCase);

        return definitions.Select(definition =>
        {
            var category = categories[definition.CategoryId];
            var displayName = FirstNonEmpty(definition.DisplayName, definition.Title);
            var imagePath = FirstNonEmpty(definition.ImagePath, definition.Image);
            var imageSource = imageResolver.Resolve(imagePath);

            var isMaintenance = maintenanceItems.TryGetValue(
                definition.Id, out var maintenanceReasonCode);
            var item = new CatalogItem
            {
                Id = definition.Id,
                CategoryId = category.Id,
                Title = displayName,
                Subtitle = definition.Subtitle,
                Category = definition.Category.Length > 0 ? definition.Category : category.DisplayName,
                Image = imagePath,
                ImageSource = imageSource,
                FallbackImage = imageResolver.FallbackImageSource,
                ImageAltText = definition.ImageAltText.Length > 0
                    ? definition.ImageAltText
                    : $"Arte Turborama para {category.DisplayName}",
                Badge = definition.Badge,
                Size = definition.Size,
                Version = definition.Version,
                Keywords = $"{category.DisplayName} {category.ShortCode} {definition.Keywords}",
                Description = FirstNonEmpty(
                    definition.Description,
                    descriptions.GetValueOrDefault(definition.Id) ?? string.Empty),
                SystemCode = category.ShortCode,
                SystemGlyph = category.Glyph,
                Order = definition.Order,
                AccentBrush = category.AccentBrush,
                Extract = definition.Extract,
                Artifact = authorizedArtifacts.GetValueOrDefault(definition.Id),
                IsMaintenance = isMaintenance,
                MaintenanceReasonCode = maintenanceReasonCode ?? string.Empty
            };
            if (isMaintenance)
                item.SetDownloadState(
                    CatalogDownloadState.Idle,
                    "Conteúdo temporariamente em manutenção");
            return item;
        }).ToArray();
    }

    private static List<CatalogItemDefinition> ExpandLegacyItems(CatalogManifest manifest)
    {
        var definitions = new List<CatalogItemDefinition>(
            manifest.Categories.Sum(category => Math.Max(0, category.SourceItemCount)));

        foreach (var category in manifest.Categories.OrderBy(category => category.Order))
        {
            var itemCount = category.SourceItemCount > 0
                ? category.SourceItemCount
                : manifest.PackageTemplates.Count;
            var includeSequence = itemCount > manifest.PackageTemplates.Count;

            for (var index = 0; index < itemCount; index++)
            {
                var template = manifest.PackageTemplates[index % manifest.PackageTemplates.Count];
                var sequence = index + 1;
                var sequenceLabel = includeSequence ? $" {sequence:000}" : string.Empty;

                definitions.Add(new CatalogItemDefinition
                {
                    Id = $"{category.Id}-{sequence:0000}",
                    CategoryId = category.Id,
                    Title = $"{category.DisplayName} • {template.Name}{sequenceLabel}",
                    Subtitle = template.Subtitle,
                    Category = category.DisplayName,
                    Image = template.Image,
                    ImageAltText = template.ImageAltText,
                    Badge = template.Badge,
                    Size = template.Size,
                    Version = template.Version,
                    Keywords = $"{template.Keywords} {sequence:000}",
                    Order = category.Order * 10_000 + sequence
                });
            }
        }

        return definitions;
    }

    private static void Validate(CatalogManifest manifest)
    {
        if (manifest.Categories is null
            || manifest.Items is null
            || manifest.PackageTemplates is null
            || manifest.Categories.Any(category => category is null)
            || manifest.Items.Any(item => item is null)
            || manifest.PackageTemplates.Any(template => template is null))
            throw new InvalidDataException(
                "O manifesto do catálogo contém uma coleção ou elemento nulo.");

        if (manifest.SchemaVersion is < 1 or > 3)
            throw new InvalidDataException($"Versão de manifesto não suportada: {manifest.SchemaVersion}.");
        if (manifest.Categories.Count is 0 or > MaximumCategoryCount)
            throw new InvalidDataException("O catálogo não possui sistemas.");
        if (manifest.Items.Count > MaximumItemCount)
            throw new InvalidDataException("O catálogo excede o limite de itens.");
        if (manifest.PackageTemplates.Count > MaximumPackageTemplateCount)
            throw new InvalidDataException("O catálogo excede o limite de modelos de pacote.");
        if (manifest.Items.Count == 0 && manifest.PackageTemplates.Count == 0)
            throw new InvalidDataException("O catálogo não possui itens nem modelos de pacote.");
        if (manifest.Categories.Sum(category =>
                (long)(category.SourceItemCount > 0
                    ? category.SourceItemCount
                    : manifest.PackageTemplates.Count)) > MaximumItemCount)
            throw new InvalidDataException("A expansão do catálogo excede o limite de itens.");

        var duplicateCategories = manifest.Categories
            .GroupBy(category => category.Id, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToArray();
        if (duplicateCategories.Length > 0)
            throw new InvalidDataException($"IDs de sistemas duplicados: {string.Join(", ", duplicateCategories)}");

        if (!string.Equals(
                manifest.DefaultImage,
                "Images/_turborama-fallback.jpg",
                StringComparison.Ordinal))
            throw new InvalidDataException("A imagem de fallback do catálogo não é canônica.");

        if (manifest.Categories.Any(category =>
                !IsSafeCatalogToken(category.Id, 64)
                || !IsCanonicalText(category.DisplayName, 128, allowEmpty: false)
                || !IsUpperAlphaNumeric(category.ShortCode, 16)
                || !IsCanonicalText(category.Glyph, 16, allowEmpty: false)
                || !IsCanonicalText(category.Description, 1_024)
                || !IsCanonicalColor(category.Accent)
                || category.Order is < 0 or > 100_000
                || category.SourceItemCount is < 0 or > 10_000))
            throw new InvalidDataException("Todo sistema precisa de ID, nome e contagem não negativa.");

        var categoryIds = manifest.Categories
            .Select(category => category.Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (manifest.Items.Count > 0)
        {
            var invalidItem = manifest.Items.FirstOrDefault(item =>
                !IsLowerHex(item.Id, 32)
                || !IsCanonicalText(item.DisplayName, 256)
                || !IsCanonicalText(item.Title, 256)
                || !AliasesAgree(item.DisplayName, item.Title)
                || !IsCanonicalText(FirstNonEmpty(item.DisplayName, item.Title), 256, allowEmpty: false)
                || !IsSafeCatalogToken(item.CategoryId, 64)
                || !categoryIds.Contains(item.CategoryId)
                || !IsCanonicalText(item.Subtitle, 256)
                || !IsCanonicalText(item.Category, 128)
                || !IsCanonicalText(item.ImagePath, 128)
                || !IsCanonicalText(item.Image, 128)
                || !AliasesAgree(item.ImagePath, item.Image)
                || !FirstNonEmpty(item.ImagePath, item.Image).Equals(
                    $"Images/{item.Id}.jpg",
                    StringComparison.Ordinal)
                || !IsCanonicalText(item.ImageAltText, 256)
                || !IsCanonicalText(item.Badge, 32)
                || !IsCanonicalText(item.Size, 64)
                || !IsCanonicalText(item.Version, 64)
                || !IsCanonicalText(item.Keywords, 512)
                || !IsCanonicalText(item.Description, 32_768)
                || item.DownloadUrl.Length != 0
                || item.Sha256.Length != 0
                || item.DownloadFileExtension.Length != 0
                || item.Order is < 0 or > 100_000);
            if (invalidItem is not null)
                throw new InvalidDataException("Todo item precisa de ID, título e uma categoria existente.");

            var duplicateItems = manifest.Items
                .GroupBy(item => item.Id, StringComparer.OrdinalIgnoreCase)
                .Where(group => group.Count() > 1)
                .Select(group => group.Key)
                .ToArray();
            if (duplicateItems.Length > 0)
                throw new InvalidDataException($"IDs de itens duplicados: {string.Join(", ", duplicateItems)}");

            if (manifest.PackageTemplates.Count != 0)
                throw new InvalidDataException(
                    "Um catálogo materializado não pode misturar modelos legados.");

            var mismatchedCount = manifest.Categories.FirstOrDefault(category =>
                manifest.Items.Count(item => item.CategoryId.Equals(
                    category.Id,
                    StringComparison.Ordinal)) != category.SourceItemCount);
            if (mismatchedCount is not null)
                throw new InvalidDataException(
                    $"A contagem declarada da categoria '{mismatchedCount.Id}' não confere.");
        }
        else
        {
            var duplicateTemplates = manifest.PackageTemplates
                .GroupBy(template => template.Id, StringComparer.OrdinalIgnoreCase)
                .Where(group => group.Count() > 1)
                .Select(group => group.Key)
                .ToArray();
            if (duplicateTemplates.Length > 0)
                throw new InvalidDataException($"IDs de modelos duplicados: {string.Join(", ", duplicateTemplates)}");

            if (manifest.PackageTemplates.Any(template =>
                    !IsSafeCatalogToken(template.Id, 64)
                    || !IsCanonicalText(template.Name, 256, allowEmpty: false)
                    || !IsCanonicalText(template.Subtitle, 256)
                    || !IsCanonicalText(template.Image, 256)
                    || !IsCanonicalText(template.ImageAltText, 256)
                    || !IsCanonicalText(template.Badge, 32)
                    || !IsCanonicalText(template.Size, 64)
                    || !IsCanonicalText(template.Version, 64)
                    || !IsCanonicalText(template.Keywords, 512)
                    || template.Order is < 0 or > 100_000))
                throw new InvalidDataException("Todo modelo de pacote precisa de ID e nome.");
        }
    }

    private static void ValidateAuthorizedArtifacts(
        CatalogManifest manifest,
        IReadOnlyDictionary<string, CatalogArtifactDescriptor> authorizedArtifacts,
        IReadOnlyDictionary<string, string> maintenanceItems,
        bool requireCompleteCoverage)
    {
        ArgumentNullException.ThrowIfNull(authorizedArtifacts);
        ArgumentNullException.ThrowIfNull(maintenanceItems);
        var itemIds = manifest.Items.Count > 0
            ? manifest.Items.Select(item => item.Id).ToHashSet(StringComparer.Ordinal)
            : ExpandLegacyItems(manifest).Select(item => item.Id)
                .ToHashSet(StringComparer.Ordinal);
        if (authorizedArtifacts.Keys.Any(maintenanceItems.ContainsKey))
            throw new InvalidDataException(
                "Um item não pode estar READY e em manutenção.");
        var authorizedUnionCount = checked(
            authorizedArtifacts.Count + maintenanceItems.Count);
        if (authorizedUnionCount > itemIds.Count)
            throw new InvalidDataException(
                "O catálogo autorizado excede o catálogo público.");
        if (requireCompleteCoverage
            && (itemIds.Count != SuiteContentProtocol.ExpectedCatalogItemCount
                || authorizedUnionCount
                != SuiteContentProtocol.ExpectedCatalogItemCount))
            throw new InvalidDataException(
                "O catálogo autorizado não cobre os 902 itens públicos.");
        foreach (var pair in authorizedArtifacts)
        {
            if (!itemIds.Contains(pair.Key)
                || !IsLowerHex(pair.Key, 32)
                || pair.Value is null
                || !string.Equals(pair.Value.ProductId,
                    CatalogArtifactDescriptor.TurboramaSuiteProductId,
                    StringComparison.Ordinal))
                throw new InvalidDataException(
                    "O catálogo autorizado contém um item desconhecido.");
        }
        foreach (var pair in maintenanceItems)
        {
            if (!itemIds.Contains(pair.Key)
                || !IsLowerHex(pair.Key, 32)
                || !string.Equals(pair.Value,
                    SuiteContentProtocol.MaintenanceReasonCode,
                    StringComparison.Ordinal))
                throw new InvalidDataException(
                    "O catálogo em manutenção contém um item inválido.");
        }
    }

    private static string FirstNonEmpty(string? preferred, string? fallback) =>
        string.IsNullOrWhiteSpace(preferred)
            ? fallback?.Trim() ?? string.Empty
            : preferred.Trim();

    private static bool IsSafeCatalogToken(string? value, int maximumLength)
        => value is not null
            && value.Length is > 0
            && value.Length <= maximumLength
            && value[0] is >= 'a' and <= 'z' or >= '0' and <= '9'
            && value[^1] is >= 'a' and <= 'z' or >= '0' and <= '9'
            && value.All(character => character is >= 'a' and <= 'z'
                or >= '0' and <= '9' or '-');

    private static bool IsUpperAlphaNumeric(string? value, int maximumLength)
        => value is not null
            && value.Length is > 0
            && value.Length <= maximumLength
            && value.All(character => character is >= 'A' and <= 'Z'
                or >= '0' and <= '9');

    private static bool IsCanonicalText(
        string? value,
        int maximumLength,
        bool allowEmpty = true)
        => value is not null
            && value.Length <= maximumLength
            && (allowEmpty || value.Length > 0)
            && value.Equals(value.Trim(), StringComparison.Ordinal)
            && !value.Any(char.IsControl);

    private static bool AliasesAgree(string? preferred, string? fallback)
        => preferred is not null
            && fallback is not null
            && (preferred.Length == 0
            || fallback.Length == 0
            || preferred.Equals(fallback, StringComparison.Ordinal));

    private static bool IsLowerHex(string? value, int exactLength)
        => value is not null
            && value.Length == exactLength
            && value.All(character => character is >= '0' and <= '9'
                or >= 'a' and <= 'f');

    private static bool IsCanonicalColor(string? value)
        => value is not null
            && value.Length is 7 or 9
            && value[0] == '#'
            && value.AsSpan(1).ToArray().All(character => character is >= '0' and <= '9'
                or >= 'A' and <= 'F');

    private static bool HasReparsePointInPath(string path)
    {
        var candidate = Path.GetFullPath(path);
        var root = Path.GetPathRoot(candidate);
        if (string.IsNullOrEmpty(root)) return true;

        var current = root;
        if (GetAttributesOrMissing(current, out var attributes)
            && (attributes & FileAttributes.ReparsePoint) != 0)
            return true;
        foreach (var segment in Path.GetRelativePath(root, candidate).Split(
                     [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                     StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            if (!GetAttributesOrMissing(current, out attributes)) return false;
            if ((attributes & FileAttributes.ReparsePoint) != 0) return true;
        }
        return false;
    }

    private static bool GetAttributesOrMissing(string path, out FileAttributes attributes)
    {
        try
        {
            attributes = File.GetAttributes(path);
            return true;
        }
        catch (Exception exception) when (exception is FileNotFoundException
                                           or DirectoryNotFoundException)
        {
            attributes = default;
            return false;
        }
    }

    private static string Normalize(string value)
    {
        var decomposed = value.Trim().ToUpperInvariant().Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(decomposed.Length);
        foreach (var character in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(character) != UnicodeCategory.NonSpacingMark)
                builder.Append(character);
        }
        return builder.ToString().Normalize(NormalizationForm.FormC);
    }
}
