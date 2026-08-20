using TurboBoxManager.Catalog;
using System.IO;

if (args.Length != 1)
{
    Console.Error.WriteLine("Uso: CatalogVerifier <catalog.json>");
    return 2;
}

var repository = CatalogRepository.Load(Path.GetFullPath(args[0]));
var expected = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
{
    ["system-tools"] = 2,
    ["emulators"] = 17,
    ["retro-games"] = 45,
    ["nintendo-3ds"] = 23,
    ["gamecube"] = 32,
    ["playstation-1"] = 119,
    ["playstation-2"] = 57,
    ["playstation-2-br"] = 50,
    ["playstation-3"] = 29,
    ["playstation-4"] = 24,
    ["playstation-5"] = 55,
    ["psp"] = 21,
    ["ps-vita"] = 23,
    ["sega-saturn"] = 31,
    ["nintendo-switch"] = 83,
    ["nintendo-wii"] = 9,
    ["nintendo-wii-u"] = 9,
    ["windows"] = 98,
    ["xbox"] = 54,
    ["xbox-360"] = 24,
    ["xbox-one"] = 18,
    ["xbox-series"] = 27
};

Assert(repository.Categories.Count == expected.Count, $"esperadas {expected.Count} categorias");
foreach (var category in repository.Categories)
{
    Assert(expected.TryGetValue(category.Id, out var sourceCount), $"categoria inesperada: {category.Id}");
    Assert(category.SourceItemCount == sourceCount, $"contagem auditada incorreta em {category.Id}");

    var firstPage = repository.Query(category.Id, string.Empty, 1, 4);
    Assert(firstPage.Items.Count == 4, $"primeira página de {category.Id} deve ter 4 cards");
    Assert(firstPage.TotalItems == 8, $"{category.Id} deve expor 8 módulos Turborama");
    Assert(firstPage.TotalPages == 2, $"{category.Id} deve ter 2 páginas");

    var lastPage = repository.Query(category.Id, string.Empty, 99, 4);
    Assert(lastPage.CurrentPage == 2 && lastPage.Items.Count == 4, $"clamp de página falhou em {category.Id}");
}

var portugueseSearch = repository.Query("playstation-2-br", "PORTUGUES", 1, 4);
Assert(portugueseSearch.TotalItems == 1, "a busca sem acentos deve localizar Brasil Pack");

var noResults = repository.Query("xbox", "termo inexistente", 1, 4);
Assert(noResults.TotalItems == 0 && noResults.Items.Count == 0 && noResults.TotalPages == 1, "estado vazio inválido");

Console.WriteLine($"PASS: {repository.Categories.Count} sistemas, contagens auditadas, busca e paginação validadas.");
return 0;

static void Assert(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}
