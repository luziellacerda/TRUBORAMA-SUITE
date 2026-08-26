using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json;
using System.Xml.Linq;
using TurboBoxManager.Catalog;
using TurboBoxManager.CatalogVerifier;

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
        Assert(item.Description.Length >= 120, $"Texto próprio ausente ou curto em {item.Id}.");
    }
}
Assert(seenIds.Count == 850, "Nem todos os IDs foram percorridos.");

var descriptionDirectory = Path.Combine(Path.GetDirectoryName(manifestPath)!, "GameDescriptions");
var descriptionFiles = Directory.EnumerateFiles(descriptionDirectory, "*.xml").ToArray();
Assert(descriptionFiles.Length == 22, "Cada sistema precisa de seu próprio XML de textos.");
var descriptionIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
foreach (var descriptionFile in descriptionFiles)
{
    var document = XDocument.Load(descriptionFile);
    foreach (var game in document.Root?.Elements("game") ?? [])
    {
        var id = ((string?)game.Attribute("id") ?? string.Empty).Trim();
        var text = ((string?)game.Element("description") ?? string.Empty).Trim();
        Assert(seenIds.Contains(id), $"Texto associado a jogo inexistente: {id}.");
        Assert(descriptionIds.Add(id), $"Texto duplicado para o jogo: {id}.");
        Assert(text.Length >= 120, $"Texto muito curto para o jogo: {id}.");
    }
}
Assert(descriptionIds.SetEquals(seenIds), "Os XML precisam cobrir individualmente os 850 jogos.");

var retroItemIds = new HashSet<string>(StringComparer.Ordinal);
using (var manifestDocument = JsonDocument.Parse(await File.ReadAllTextAsync(manifestPath)))
{
    var verticalPosterCount = 0;
    var retroPosterCount = 0;
    var retroSystemIconCount = 0;
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

        var relativeImagePath = itemElement.GetProperty("image").GetString()!;
        var imagePath = Path.Combine(
            Path.GetDirectoryName(manifestPath)!,
            relativeImagePath.Replace('/', Path.DirectorySeparatorChar));
        Assert(File.Exists(imagePath), $"Capa ausente: {relativeImagePath}.");
        using var imageStream = File.OpenRead(imagePath);
        var (posterWidth, posterHeight) = ReadImageDimensions(imageStream);
        Assert(posterWidth == 1024 && posterHeight == 1536,
            $"Capa fora do padrão vertical 1024x1536: {relativeImagePath}.");
        Assert(imageStream.Length >= 100 * 1024,
            $"Capa foi comprimida em excesso: {relativeImagePath}.");
        verticalPosterCount++;

        if (itemElement.GetProperty("categoryId").GetString() == "retro-games")
        {
            Assert(imageStream.Length >= 180 * 1024,
                $"Pôster retrô foi comprimido em excesso: {relativeImagePath}.");
            retroPosterCount++;

            var itemId = itemElement.GetProperty("id").GetString()!;
            Assert(retroItemIds.Add(itemId), $"ID retro duplicado no catálogo: {itemId}.");
            var iconPath = Path.Combine(
                Path.GetDirectoryName(manifestPath)!,
                "SystemIcons",
                $"{itemId}.png");
            Assert(File.Exists(iconPath), $"Icone do sistema retro ausente: {itemId}.");

            var iconHeader = new byte[24];
            using var iconStream = File.OpenRead(iconPath);
            Assert(iconStream.Read(iconHeader) == iconHeader.Length,
                $"Icone do sistema retro truncado: {itemId}.");
            Assert(iconHeader.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }),
                $"Icone do sistema retro precisa ser PNG: {itemId}.");
            Assert(BinaryPrimitives.ReadInt32BigEndian(iconHeader.AsSpan(16, 4)) > 0 &&
                   BinaryPrimitives.ReadInt32BigEndian(iconHeader.AsSpan(20, 4)) > 0,
                $"Icone do sistema retro tem dimensoes invalidas: {itemId}.");
            retroSystemIconCount++;
        }
    }
    Assert(verticalPosterCount == 850, "As 850 capas precisam usar o padrão vertical 1024x1536.");
    Assert(retroPosterCount == 45, "A coleção retrô precisa ter 45 pôsteres verticais validados.");
    Assert(retroSystemIconCount == 45, "O carrossel retro precisa ter 45 icones de sistema validados.");
}

var menuIconDirectory = Path.Combine(Path.GetDirectoryName(manifestPath)!, "MenuIcons");
Assert(Directory.Exists(menuIconDirectory)
       && Directory.EnumerateFiles(menuIconDirectory, "*.png").Count() == 22,
    "O menu Sistemas e coleções precisa ter 22 ícones próprios.");

var catalogAssetDirectory = Path.GetDirectoryName(manifestPath)!;
var systemVideoDirectory = Path.Combine(catalogAssetDirectory, "SystemVideos");
var systemVideoMap = JsonSerializer.Deserialize<Dictionary<string, string>>(
    await File.ReadAllTextAsync(Path.Combine(systemVideoDirectory, "system-videos.json")))!;
var systemVideoIntegrity = JsonSerializer.Deserialize<Dictionary<string, VideoIntegrity>>(
    await File.ReadAllTextAsync(Path.Combine(systemVideoDirectory, "system-video-integrity.json")),
    new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
var platformDescriptions = JsonSerializer.Deserialize<Dictionary<string, string>>(
    await File.ReadAllTextAsync(Path.Combine(catalogAssetDirectory, "platform-descriptions.json")))!;
Assert(systemVideoMap.Count == 45, "Cada sistema retrô precisa de um vínculo de vídeo.");
Assert(systemVideoIntegrity.Count == 38, "Os 45 sistemas devem reutilizar exatamente 38 vídeos otimizados.");
Assert(platformDescriptions.Count == 45, "Cada sistema retrô precisa de uma descrição incorporada.");
foreach (var (itemId, fileName) in systemVideoMap)
{
    Assert(retroItemIds.Contains(itemId), $"Vídeo associado a um sistema inexistente: {itemId}.");
    Assert(Path.GetFileName(fileName) == fileName && Path.GetExtension(fileName) == ".mp4",
        $"Nome de vídeo inseguro: {fileName}.");
    var videoPath = Path.Combine(systemVideoDirectory, fileName);
    Assert(File.Exists(videoPath), $"Vídeo demonstrativo ausente: {fileName}.");
    Assert(systemVideoIntegrity.TryGetValue(fileName, out var expected),
        $"Integridade ausente: {fileName}.");
    var videoBytes = await File.ReadAllBytesAsync(videoPath);
    Assert(videoBytes.Length == expected!.Length, $"Tamanho alterado: {fileName}.");
    Assert(videoBytes.Length >= 12
           && videoBytes.AsSpan(4, 4).SequenceEqual("ftyp"u8),
        $"MP4 inválido: {fileName}.");
    Assert(Convert.ToHexString(SHA256.HashData(videoBytes)).Equals(expected.Sha256, StringComparison.OrdinalIgnoreCase),
        $"SHA-256 alterado: {fileName}.");
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
    using var service = new CatalogDownloadService();
    if (!string.Equals(
            Environment.GetEnvironmentVariable("TURBORAMA_SKIP_NETWORK_TESTS"),
            "1",
            StringComparison.Ordinal))
    {
        var item = repository.Query("system-tools", string.Empty, 1, 4).Items[0];
        var result = await service.DownloadAsync(item, temporaryRoot);
        Assert(result.Succeeded, result.Message);
        Assert(File.Exists(result.LocalFilePath), "Arquivo final não foi criado.");
        var downloadedName = Path.GetFileNameWithoutExtension(result.LocalFilePath);
        Assert(!downloadedName.Equals(item.Id, StringComparison.OrdinalIgnoreCase),
            "O arquivo baixado deve usar um nome legível, não apenas o ID interno.");
        Assert(downloadedName.Contains(item.Id, StringComparison.OrdinalIgnoreCase),
            "O arquivo baixado deve manter o ID estável para evitar colisões.");
        Assert(new FileInfo(result.LocalFilePath).Length == 92, "O asset público deveria ter 92 bytes.");
        var actualHash = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(result.LocalFilePath)));
        Assert(actualHash.Equals(item.Sha256, StringComparison.OrdinalIgnoreCase), "Hash final divergente.");
        Assert(item.DownloadState == CatalogDownloadState.Completed && item.ProgressPercentage == 100,
            "Estado/progresso final incorreto.");
        Assert(!Directory.EnumerateFiles(temporaryRoot, "*.part", SearchOption.AllDirectories).Any(),
            "Arquivo parcial permaneceu após sucesso.");
    }

    using var preCanceled = new CancellationTokenSource();
    preCanceled.Cancel();
    var canceledItem = repository.Query("system-tools", string.Empty, 1, 4).Items[1];
    var canceled = await service.DownloadAsync(canceledItem, temporaryRoot, preCanceled.Token);
    Assert(canceled.WasCanceled && canceledItem.DownloadState == CatalogDownloadState.Paused,
        "Uma interrupção precisa pausar sem apagar o progresso.");

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
    var driveRoot = Path.GetPathRoot(temporaryRoot)!;
    var safeDriveRootPath = service.BuildSafeDestinationPath(
        driveRoot,
        unsafeItem,
        new Uri("https://github.com/file.txt"));
    Assert(safeDriveRootPath.StartsWith(driveRoot, StringComparison.OrdinalIgnoreCase),
        "A raiz de uma unidade válida deveria aceitar a pasta Turborama.");

    var gameLibraryRoot = Path.Combine(
        temporaryRoot,
        CatalogArchiveExtractor.GameLibraryFolderName);
    Directory.CreateDirectory(gameLibraryRoot);
    var gameLibraryItem = new CatalogItem
    {
        Id = "game-library-test",
        CategoryId = "retro-games",
        Title = "Super Nintendo",
        DownloadFileExtension = ".zip"
    };
    var gameLibraryPath = service.BuildSafeDestinationPath(
        gameLibraryRoot,
        gameLibraryItem,
        new Uri("https://github.com/game.zip"));
    var gameRelativePath = Path.GetRelativePath(gameLibraryRoot, gameLibraryPath);
    Assert(gameRelativePath.Split(Path.DirectorySeparatorChar).Length == 3
           && gameRelativePath.StartsWith("retro-games" + Path.DirectorySeparatorChar),
        "O download do jogo deveria preservar categoria/item dentro de Turborama Roms.");

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

    await DownloadResumeVerifier.RunAsync(Path.Combine(temporaryRoot, "resume-tests"));
    await ArchiveExtractionVerifier.RunAsync(Path.Combine(temporaryRoot, "extraction-tests"));
    GameLibraryLocatorVerifier.Run(Path.Combine(temporaryRoot, "library-locator-tests"));
}
finally
{
    if (Directory.Exists(temporaryRoot)) Directory.Delete(temporaryRoot, recursive: true);
}

WpfTemplateVerifier.Run("xbox-series");

Console.WriteLine("PASS: catálogo, carrossel universal, templates WPF reais, Biblioteca 22/850, 850 capas, 850 textos XML, 45 descrições retrô, 38 vídeos íntegros, 45 pôsteres, 45 ícones retrô, 22 ícones de menu, pasta TruboRoms\\roms, retomada, pausa, descarte e extração segura verificados.");

static (int Width, int Height) ReadImageDimensions(Stream stream)
{
    Span<byte> header = stackalloc byte[24];
    stream.Position = 0;
    if (stream.Read(header) == header.Length
        && header[..8].SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }))
        return (
            BinaryPrimitives.ReadInt32BigEndian(header.Slice(16, 4)),
            BinaryPrimitives.ReadInt32BigEndian(header.Slice(20, 4)));

    stream.Position = 0;
    if (stream.ReadByte() != 0xFF || stream.ReadByte() != 0xD8)
        throw new InvalidDataException("Formato de imagem não reconhecido.");
    while (stream.Position < stream.Length)
    {
        int markerStart;
        do markerStart = stream.ReadByte(); while (markerStart >= 0 && markerStart != 0xFF);
        if (markerStart < 0) break;

        int marker;
        do marker = stream.ReadByte(); while (marker == 0xFF);
        if (marker < 0 || marker is 0xD9 or 0xDA) break;
        if (marker is 0xD8 or 0x01 || marker is >= 0xD0 and <= 0xD7) continue;

        var lengthHigh = stream.ReadByte();
        var lengthLow = stream.ReadByte();
        if (lengthHigh < 0 || lengthLow < 0) break;
        var segmentLength = (lengthHigh << 8) | lengthLow;
        if (segmentLength < 2) break;
        if (marker is 0xC0 or 0xC1 or 0xC2 or 0xC3 or 0xC5 or 0xC6 or 0xC7
            or 0xC9 or 0xCA or 0xCB or 0xCD or 0xCE or 0xCF)
        {
            _ = stream.ReadByte();
            var height = (stream.ReadByte() << 8) | stream.ReadByte();
            var width = (stream.ReadByte() << 8) | stream.ReadByte();
            return (width, height);
        }
        stream.Seek(segmentLength - 2, SeekOrigin.Current);
    }
    throw new InvalidDataException("Dimensões JPEG não encontradas.");
}

static void Assert(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

sealed class VideoIntegrity
{
    public string Sha256 { get; init; } = string.Empty;
    public long Length { get; init; }
}
