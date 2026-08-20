using System.Security.Cryptography;
using System.Text.Json;
using TurboBoxManager.Catalog;

if (args.Length != 1)
    throw new ArgumentException("Informe o caminho para catalog.json.");

var manifestPath = Path.GetFullPath(args[0]);
var repository = CatalogRepository.Load(manifestPath);
Assert(repository.Categories.Count == 22, "O catálogo deve ter 22 categorias.");
Assert(repository.ItemCount == 850, "O catálogo deve materializar 850 itens explícitos.");

var seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
foreach (var category in repository.Categories)
{
    var result = repository.Query(category.Id, string.Empty, 1, Math.Max(1, category.SourceItemCount));
    Assert(result.TotalItems == category.SourceItemCount,
        $"Contagem divergente em {category.Id}: {result.TotalItems}/{category.SourceItemCount}.");
    foreach (var item in result.Items)
    {
        Assert(seenIds.Add(item.Id), $"ID duplicado: {item.Id}.");
        Assert(item.Category == category.DisplayName, $"Categoria ausente em {item.Id}.");
        Assert(item.ImageSource.Length > 0, $"Imagem resolvida ausente em {item.Id}.");
        Assert(item.FallbackImage.Length > 0, $"Fallback ausente em {item.Id}.");
        Assert(item.Size == "92 B", $"Tamanho de teste incorreto em {item.Id}.");
        Assert(item.DownloadUrl.StartsWith("https://github.com/luziellacerda/TRUBORAMA-SUITE/", StringComparison.Ordinal),
            $"URL pública de teste ausente em {item.Id}.");
        Assert(item.Sha256.Length == 64, $"SHA-256 ausente em {item.Id}.");
    }
}
Assert(seenIds.Count == 850, "Nem todos os IDs foram percorridos.");

using (var manifestDocument = JsonDocument.Parse(await File.ReadAllTextAsync(manifestPath)))
{
    foreach (var itemElement in manifestDocument.RootElement.GetProperty("items").EnumerateArray())
    {
        Assert(itemElement.GetProperty("downloadUrl").GetString() == string.Empty,
            "O manifesto público não pode conter URL de download por item.");
        Assert(itemElement.GetProperty("sha256").GetString() == string.Empty,
            "O manifesto público não pode inventar checksum por item.");
        Assert(itemElement.GetProperty("downloadFileExtension").GetString() == string.Empty,
            "O manifesto público não pode conter extensão de download por item.");
        Assert(itemElement.GetProperty("image").GetString()?.StartsWith("Images/", StringComparison.Ordinal) == true,
            "Cada item precisa apontar para uma imagem local sanitizada.");
    }
}

var accentSearch = repository.Query("system-tools", "utilitarios", 1, 100);
Assert(accentSearch.TotalItems > 0, "Busca sem acento deve encontrar a categoria 'utilitários'.");
var page = repository.Query("playstation-1", string.Empty, 99, 4);
Assert(page.TotalItems == 119, "PlayStation 1 deve ter 119 itens.");
Assert(page.TotalPages == 30 && page.CurrentPage == 30 && page.Items.Count == 3,
    "Paginação de 119 itens deve terminar na página 30 com 3 itens.");

var temporaryRoot = Path.Combine(Path.GetTempPath(), "TurboramaCatalogVerifier", Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(temporaryRoot);
try
{
    var item = repository.Query("system-tools", string.Empty, 1, 4).Items[0];
    using var service = new CatalogDownloadService();
    var result = await service.DownloadAsync(item, temporaryRoot);
    Assert(result.Succeeded, result.Message);
    Assert(File.Exists(result.LocalFilePath), "Arquivo final não foi criado.");
    var downloadedName = Path.GetFileNameWithoutExtension(result.LocalFilePath);
    Assert(!downloadedName.Equals(item.Id, StringComparison.OrdinalIgnoreCase),
        "O arquivo baixado deve usar um nome legível, não apenas o ID interno.");
    Assert(downloadedName.EndsWith(item.Id[..8], StringComparison.OrdinalIgnoreCase),
        "O arquivo baixado deve manter um sufixo curto do ID para evitar colisões.");
    Assert(new FileInfo(result.LocalFilePath).Length == 92, "O asset público deveria ter 92 bytes.");
    var actualHash = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(result.LocalFilePath)));
    Assert(actualHash.Equals(item.Sha256, StringComparison.OrdinalIgnoreCase), "Hash final divergente.");
    Assert(item.DownloadState == CatalogDownloadState.Completed && item.ProgressPercentage == 100,
        "Estado/progresso final incorreto.");
    Assert(!Directory.EnumerateFiles(temporaryRoot, "*.part", SearchOption.AllDirectories).Any(),
        "Arquivo parcial permaneceu após sucesso.");

    using var preCanceled = new CancellationTokenSource();
    preCanceled.Cancel();
    var canceledItem = repository.Query("system-tools", string.Empty, 1, 4).Items[1];
    var canceled = await service.DownloadAsync(canceledItem, temporaryRoot, preCanceled.Token);
    Assert(canceled.WasCanceled && canceledItem.DownloadState == CatalogDownloadState.Canceled,
        "Cancelamento antes da fila não foi refletido.");

    var unsafeItem = new CatalogItem
    {
        Id = "../../escape",
        CategoryId = "../outside",
        DownloadFileExtension = ".txt"
    };
    var safePath = service.BuildSafeDestinationPath(
        temporaryRoot,
        unsafeItem,
        new Uri("https://github.com/file.txt"));
    var canonicalRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(temporaryRoot)) + Path.DirectorySeparatorChar;
    Assert(safePath.StartsWith(canonicalRoot, StringComparison.OrdinalIgnoreCase), "Destino escapou da raiz.");

    var blockedItem = new CatalogItem
    {
        Id = "blocked",
        CategoryId = "test",
        DownloadUrl = "https://example.com/file.txt",
        DownloadFileExtension = ".txt"
    };
    var blocked = await service.DownloadAsync(blockedItem, temporaryRoot);
    Assert(blocked.State == CatalogDownloadState.Failed && !service.IsActive(blockedItem.Id),
        "Host fora da allowlist deveria falhar antes da rede.");
}
finally
{
    if (Directory.Exists(temporaryRoot)) Directory.Delete(temporaryRoot, recursive: true);
}

Console.WriteLine("PASS: 22 categorias, 850 itens, busca, paginação, imagens e download seguro verificados.");

static void Assert(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}
