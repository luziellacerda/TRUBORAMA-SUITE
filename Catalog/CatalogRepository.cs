using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;

namespace TurboBoxManager.Catalog;

public sealed class CatalogRepository
{
    private readonly CatalogManifest _manifest;
    private readonly IReadOnlyList<CatalogItem> _items;

    private CatalogRepository(CatalogManifest manifest)
    {
        _manifest = manifest;
        Validate(manifest);
        _items = ExpandItems(manifest);
    }

    public IReadOnlyList<CatalogCategory> Categories =>
        _manifest.Categories.OrderBy(category => category.Order).ToArray();

    public static CatalogRepository Load(string manifestPath)
    {
        using var stream = File.OpenRead(manifestPath);
        var manifest = JsonSerializer.Deserialize<CatalogManifest>(stream, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            ReadCommentHandling = JsonCommentHandling.Skip
        }) ?? throw new InvalidDataException("O manifesto do catálogo está vazio.");

        return new CatalogRepository(manifest);
    }

    public CatalogQueryResult Query(string categoryId, string? searchText, int requestedPage, int pageSize)
    {
        if (pageSize < 1) throw new ArgumentOutOfRangeException(nameof(pageSize));

        var normalizedSearch = Normalize(searchText ?? string.Empty);
        var filtered = _items
            .Where(item => item.CategoryId.Equals(categoryId, StringComparison.OrdinalIgnoreCase))
            .Where(item => normalizedSearch.Length == 0 || Normalize($"{item.Title} {item.Subtitle} {item.Keywords} {item.Version}").Contains(normalizedSearch, StringComparison.Ordinal))
            .OrderBy(item => item.Order)
            .ThenBy(item => item.Title, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();

        var totalPages = Math.Max(1, (int)Math.Ceiling(filtered.Length / (double)pageSize));
        var currentPage = Math.Clamp(requestedPage, 1, totalPages);
        var pageItems = filtered.Skip((currentPage - 1) * pageSize).Take(pageSize).ToArray();

        return new CatalogQueryResult(pageItems, filtered.Length, currentPage, totalPages);
    }

    private static IReadOnlyList<CatalogItem> ExpandItems(CatalogManifest manifest)
    {
        return manifest.Categories
            .SelectMany(category => manifest.PackageTemplates.Select(template => new CatalogItem
            {
                Id = $"{category.Id}-{template.Id}",
                CategoryId = category.Id,
                Title = $"{category.DisplayName} • {template.Name}",
                Subtitle = template.Subtitle,
                Badge = template.Badge,
                Size = template.Size,
                Version = template.Version,
                Keywords = $"{category.DisplayName} {category.ShortCode} {template.Keywords}",
                SystemCode = category.ShortCode,
                SystemGlyph = category.Glyph,
                Order = template.Order,
                AccentBrush = category.AccentBrush
            }))
            .ToArray();
    }

    private static void Validate(CatalogManifest manifest)
    {
        if (manifest.Categories.Count == 0) throw new InvalidDataException("O catálogo não possui sistemas.");
        if (manifest.PackageTemplates.Count == 0) throw new InvalidDataException("O catálogo não possui pacotes.");

        var duplicateCategories = manifest.Categories
            .GroupBy(category => category.Id, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToArray();
        if (duplicateCategories.Length > 0)
            throw new InvalidDataException($"IDs de sistemas duplicados: {string.Join(", ", duplicateCategories)}");

        var duplicateTemplates = manifest.PackageTemplates
            .GroupBy(template => template.Id, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToArray();
        if (duplicateTemplates.Length > 0)
            throw new InvalidDataException($"IDs de pacotes duplicados: {string.Join(", ", duplicateTemplates)}");

        if (manifest.Categories.Any(category => string.IsNullOrWhiteSpace(category.Id) || string.IsNullOrWhiteSpace(category.DisplayName)))
            throw new InvalidDataException("Todo sistema precisa de ID e nome.");
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
