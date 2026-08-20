using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;

namespace TurboBoxManager.Catalog;

public sealed class CatalogRepository
{
    private readonly CatalogManifest _manifest;
    private readonly IReadOnlyList<CatalogItem> _items;
    private readonly IReadOnlyDictionary<string, CatalogItem> _itemsById;

    private CatalogRepository(CatalogManifest manifest, string manifestPath)
    {
        _manifest = manifest;
        Validate(manifest);

        var imageResolver = new CatalogImageResolver(manifestPath, manifest.DefaultImage);
        _items = MaterializeItems(manifest, imageResolver);
        _itemsById = _items.ToDictionary(item => item.Id, StringComparer.OrdinalIgnoreCase);
    }

    public IReadOnlyList<CatalogCategory> Categories =>
        _manifest.Categories.OrderBy(category => category.Order).ToArray();

    public int ItemCount => _items.Count;

    public static CatalogRepository Load(string manifestPath)
    {
        using var stream = File.OpenRead(manifestPath);
        var manifest = JsonSerializer.Deserialize<CatalogManifest>(stream, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            ReadCommentHandling = JsonCommentHandling.Skip
        }) ?? throw new InvalidDataException("O manifesto do catálogo está vazio.");

        return new CatalogRepository(manifest, manifestPath);
    }

    public CatalogItem? FindById(string itemId) =>
        _itemsById.GetValueOrDefault(itemId);

    public CatalogQueryResult Query(string categoryId, string? searchText, int requestedPage, int pageSize)
    {
        if (pageSize < 1) throw new ArgumentOutOfRangeException(nameof(pageSize));

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
        var pageItems = filtered.Skip((currentPage - 1) * pageSize).Take(pageSize).ToArray();

        return new CatalogQueryResult(pageItems, filtered.Length, currentPage, totalPages);
    }

    private static IReadOnlyList<CatalogItem> MaterializeItems(
        CatalogManifest manifest,
        CatalogImageResolver imageResolver)
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
            var useTestDownload = manifest.EnableTestDownloads && string.IsNullOrWhiteSpace(definition.DownloadUrl);
            var downloadUrl = useTestDownload ? manifest.TestDownload.Url : definition.DownloadUrl;
            var checksum = useTestDownload ? manifest.TestDownload.Sha256 : definition.Sha256;
            var fileExtension = useTestDownload
                ? manifest.TestDownload.FileExtension
                : definition.DownloadFileExtension;
            var displaySize = useTestDownload && manifest.TestDownload.Size.Length > 0
                ? manifest.TestDownload.Size
                : definition.Size;
            var imageSource = imageResolver.Resolve(imagePath);

            return new CatalogItem
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
                Size = displaySize,
                Version = definition.Version,
                Keywords = $"{category.DisplayName} {category.ShortCode} {definition.Keywords}",
                SystemCode = category.ShortCode,
                SystemGlyph = category.Glyph,
                Order = definition.Order,
                AccentBrush = category.AccentBrush,
                DownloadUrl = downloadUrl,
                Sha256 = checksum,
                DownloadFileExtension = fileExtension
            };
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
        if (manifest.SchemaVersion is < 1 or > 3)
            throw new InvalidDataException($"Versão de manifesto não suportada: {manifest.SchemaVersion}.");
        if (manifest.Categories.Count == 0)
            throw new InvalidDataException("O catálogo não possui sistemas.");
        if (manifest.Items.Count == 0 && manifest.PackageTemplates.Count == 0)
            throw new InvalidDataException("O catálogo não possui itens nem modelos de pacote.");

        var duplicateCategories = manifest.Categories
            .GroupBy(category => category.Id, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToArray();
        if (duplicateCategories.Length > 0)
            throw new InvalidDataException($"IDs de sistemas duplicados: {string.Join(", ", duplicateCategories)}");

        if (manifest.Categories.Any(category =>
                string.IsNullOrWhiteSpace(category.Id)
                || string.IsNullOrWhiteSpace(category.DisplayName)
                || category.SourceItemCount < 0))
            throw new InvalidDataException("Todo sistema precisa de ID, nome e contagem não negativa.");

        var categoryIds = manifest.Categories
            .Select(category => category.Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (manifest.Items.Count > 0)
        {
            var invalidItem = manifest.Items.FirstOrDefault(item =>
                string.IsNullOrWhiteSpace(item.Id)
                || string.IsNullOrWhiteSpace(FirstNonEmpty(item.DisplayName, item.Title))
                || !categoryIds.Contains(item.CategoryId));
            if (invalidItem is not null)
                throw new InvalidDataException("Todo item precisa de ID, título e uma categoria existente.");

            var duplicateItems = manifest.Items
                .GroupBy(item => item.Id, StringComparer.OrdinalIgnoreCase)
                .Where(group => group.Count() > 1)
                .Select(group => group.Key)
                .ToArray();
            if (duplicateItems.Length > 0)
                throw new InvalidDataException($"IDs de itens duplicados: {string.Join(", ", duplicateItems)}");
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
                    string.IsNullOrWhiteSpace(template.Id) || string.IsNullOrWhiteSpace(template.Name)))
                throw new InvalidDataException("Todo modelo de pacote precisa de ID e nome.");
        }

        if (!manifest.EnableTestDownloads) return;

        if (!Uri.TryCreate(manifest.TestDownload.Url, UriKind.Absolute, out var testUri)
            || !testUri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("O download de teste precisa usar uma URL HTTPS absoluta.");
        if (!IsSha256(manifest.TestDownload.Sha256))
            throw new InvalidDataException("O download de teste precisa de um SHA-256 válido.");
        if (!IsSafeExtension(manifest.TestDownload.FileExtension))
            throw new InvalidDataException("A extensão do download de teste é inválida.");
    }

    private static bool IsSha256(string value) =>
        value.Length == 64 && value.All(Uri.IsHexDigit);

    private static string FirstNonEmpty(string preferred, string fallback) =>
        string.IsNullOrWhiteSpace(preferred) ? fallback.Trim() : preferred.Trim();

    private static bool IsSafeExtension(string extension) =>
        extension.Length is >= 2 and <= 10
        && extension[0] == '.'
        && extension.Skip(1).All(character => char.IsAsciiLetterOrDigit(character));

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
