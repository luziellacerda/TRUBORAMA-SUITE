using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using TurboBoxManager;
using TurboBoxManager.Catalog;
using TurboBoxManager.CatalogVerifier;
using TurboBoxManager.Licensing;

if (args is ["--verify-suite-protocol"])
{
    SuiteProtocolVerifier.Run();
    Console.WriteLine(
        "PASS: licensing v1, machine proof e assertions de sessao permanecem compativeis.");
    return;
}

if (args is ["--verify-download-resume"])
{
    var resumeRoot = Path.Combine(
        Path.GetTempPath(),
        "TurboramaResumeVerifier-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(resumeRoot);
    try
    {
        await DownloadResumeVerifier.RunAsync(resumeRoot);
        Console.WriteLine(
            "PASS: autorizacao efemera, retomada e fail-closed de downloads verificados.");
    }
    finally
    {
        if (Directory.Exists(resumeRoot))
            Directory.Delete(resumeRoot, recursive: true);
    }
    return;
}

if (args is ["--verify-content"])
{
    SuiteContentVerifier.Run();
    Console.WriteLine(
        "PASS: protocolo, autoridade separada, snapshot atomico e grants de conteudo verificados.");
    return;
}

if (args.Length == 4
    && args[0].Equals(
        "--verify-content-authority-base64",
        StringComparison.Ordinal))
{
    SuiteContentVerifier.VerifyAuthorityBase64(args[1], args[2], args[3]);
    Console.WriteLine(
        "PASS: autoridade de conteudo separada, assinada, HTTPS e vigente.");
    return;
}

if (args.Length == 4
    && args[0].Equals("--verify-authority-base64", StringComparison.Ordinal))
{
    var envelopeBytes = Convert.FromBase64String(args[1]);
    var issuerSpki = Convert.FromBase64String(args[2]);
    try
    {
        Assert(envelopeBytes.Length is >= 64 and <= 8 * 1024,
            "Envelope assinado da autoridade ausente ou fora do limite de build.");
        Assert(issuerSpki.Length is >= 256 and <= 1024,
            "SPKI da autoridade ausente ou fora do limite de build.");
        var verified = SuiteAuthorityConfigurationVerifier.VerifyPinnedEnvelope(
            envelopeBytes,
            issuerSpki,
            args[3],
            TimeProvider.System);
        Console.WriteLine(
            $"PASS: bytes exatos e ancorados da autoridade Suite são válidos, HTTPS e vigentes ({verified.BaseUri.Host}).");
    }
    finally
    {
        CryptographicOperations.ZeroMemory(envelopeBytes);
        CryptographicOperations.ZeroMemory(issuerSpki);
    }
    return;
}

if (args.Length == 3
    && args[0].Equals("--verify-authority", StringComparison.Ordinal))
{
    var envelopeInfo = new FileInfo(args[1]);
    var issuerInfo = new FileInfo(args[2]);
    Assert(envelopeInfo.Exists && envelopeInfo.Length is >= 64 and <= 32 * 1024,
        "Envelope assinado da autoridade ausente ou fora do limite.");
    Assert(issuerInfo.Exists && issuerInfo.Length is >= 256 and <= 4096,
        "SPKI da autoridade ausente ou fora do limite.");

    var envelopeBytes = await File.ReadAllBytesAsync(envelopeInfo.FullName);
    var issuerSpki = await File.ReadAllBytesAsync(issuerInfo.FullName);
    try
    {
        var verified = SuiteAuthorityConfigurationVerifier.VerifyEnvelope(
            envelopeBytes,
            issuerSpki,
            TimeProvider.System);
        Console.WriteLine(
            $"PASS: autoridade Suite assinada válida, HTTPS e vigente ({verified.BaseUri.Host}).");
    }
    finally
    {
        CryptographicOperations.ZeroMemory(envelopeBytes);
        CryptographicOperations.ZeroMemory(issuerSpki);
    }
    return;
}

if (args is ["--verify-wpf"])
{
    WpfTemplateVerifier.Run("xbox-series");
    Console.WriteLine(
        "PASS: templates WPF, DPI/work area, mídia assíncrona e descarte obsoleto verificados.");
    return;
}

if (args is ["--verify-device-inventory"])
{
    await SuiteDeviceInventoryVerifier.RunAsync();
    Console.WriteLine(
        "PASS: inventario auxiliar de placa-mae, canonicalizacao, provas e cache verificados.");
    return;
}

if (args is ["--verify-local-library"])
{
    var localLibraryRoot = Path.Combine(
        Path.GetTempPath(),
        "TurboramaLocalLibraryVerifier-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(localLibraryRoot);
    try
    {
        await CatalogLocalLibraryVerifier.RunAsync(localLibraryRoot);
        Console.WriteLine(
            "PASS: detecção e exclusão confinada dos jogos locais verificadas.");
    }
    finally
    {
        if (Directory.Exists(localLibraryRoot))
            Directory.Delete(localLibraryRoot, recursive: true);
    }
    return;
}

if (args is ["--verify-archive-extraction"])
{
    var root = Path.Combine(Path.GetTempPath(), "TurboramaArchiveVerifier-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(root);
    try
    {
        await ArchiveExtractionVerifier.RunAsync(root);
        Console.WriteLine("PASS: extração e correção de documentos Sambox verificadas.");
    }
    finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    return;
}

if (args is ["--verify-music"])
{
    var tracks = EmbeddedMusicLibrary.PreparePlaylist(CancellationToken.None);
    Assert(tracks.Count == 9, "A playlist interna deve possuir nove faixas.");
    var newTrack = tracks.Single(path => Path.GetFileName(path).Equals("Aperta Start.m4a", StringComparison.Ordinal));
    Assert(new FileInfo(newTrack).Length == 3_274_433, "A faixa Aperta Start possui tamanho inesperado.");
    Assert(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(newTrack))).Equals(
        "0ECA8F163386DC3590BBDA679F7998892406BB89A0FDF41C2398BFB8C8FE7A2C", StringComparison.Ordinal),
        "A faixa Aperta Start falhou na verificação SHA-256.");
    Console.WriteLine("PASS: Aperta Start incorporada e validada na playlist interna.");
    return;
}

if (args.Length != 1)
    throw new ArgumentException("Informe o caminho para catalog.json.");

var manifestPath = Path.GetFullPath(args[0]);
var repository = CatalogRepository.Load(manifestPath);
Assert(repository.Categories.Count == 22, "O catálogo deve ter 22 categorias.");
Assert(repository.ItemCount == 902, "O catálogo deve materializar 902 itens explícitos.");
AssertReadOnlyList(repository.Categories, "As categorias não podem expor um array mutável.");
AssertReadOnlyList(repository.Items, "Os itens não podem expor um array mutável.");
ExpectInvalidCatalog(
    "{\"schemaVersion\":3,\"schemaVersion\":3}",
    manifestPath,
    "O catálogo deve rejeitar propriedades JSON duplicadas.");
ExpectInvalidCatalog(
    "{\"SchemaVersion\":3}",
    manifestPath,
    "O catálogo deve rejeitar casing não canônico.");
var minimalCatalog = CreateMinimalCatalogJson();
using (var minimalCatalogStream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(minimalCatalog)))
{
    var minimalRepository = CatalogRepository.Load(minimalCatalogStream, manifestPath);
    Assert(minimalRepository.ItemCount == 1,
        "O manifesto mínimo de controle deveria ser válido.");
}
ExpectInvalidCatalog(
    MutateCatalog(minimalCatalog, root => { _ = root.Remove("schemaVersion"); }),
    manifestPath,
    "O catálogo deve rejeitar schemaVersion ausente.");
ExpectInvalidCatalog(
    "null",
    manifestPath,
    "O catálogo deve rejeitar raiz nula.");
ExpectInvalidCatalog(
    MutateCatalog(minimalCatalog, root => { root["categories"] = null; }),
    manifestPath,
    "O catálogo deve rejeitar uma coleção nula.");
ExpectInvalidCatalog(
    MutateCatalog(minimalCatalog, root =>
    {
        root["categories"]!.AsArray()[0] = null;
    }),
    manifestPath,
    "O catálogo deve rejeitar um elemento nulo.");
ExpectInvalidCatalog(
    MutateCatalog(minimalCatalog, root =>
    {
        root["items"]!.AsArray()[0]!.AsObject()["title"] = null;
    }),
    manifestPath,
    "O catálogo deve rejeitar uma string nula.");
ExpectInvalidCatalog(
    MutateCatalog(minimalCatalog, root =>
    {
        root["items"]!.AsArray()[0]!.AsObject()["title"] = " Teste";
    }),
    manifestPath,
    "O catálogo deve rejeitar texto com espaço externo.");
ExpectInvalidCatalog(
    MutateCatalog(minimalCatalog, root =>
    {
        root["items"]!.AsArray()[0]!.AsObject()["title"] = "Tes\u0001te";
    }),
    manifestPath,
    "O catálogo deve rejeitar controles em texto.");
ExpectInvalidCatalog(
    MutateCatalog(minimalCatalog, root =>
    {
        root["items"]!.AsArray()[0]!.AsObject()["title"] = new string('A', 257);
    }),
    manifestPath,
    "O catálogo deve rejeitar texto acima do limite.");
ExpectInvalidCatalog(
    MutateCatalog(minimalCatalog, root =>
    {
        root["items"]!.AsArray()[0]!.AsObject()["downloadUrl"] = " ";
    }),
    manifestPath,
    "O catálogo visual deve exigir campos de download exatamente vazios.");
ExpectInvalidCatalog(
    MutateCatalog(minimalCatalog, root =>
    {
        root["items"]!.AsArray()[0]!.AsObject()["displayName"] = "Outro título";
    }),
    manifestPath,
    "O catálogo deve rejeitar aliases de título divergentes.");
var localImageResolver = new CatalogImageResolver(
    manifestPath,
    "Images/_turborama-fallback.jpg");
Assert(localImageResolver.Resolve("https://example.invalid/tracker.jpg")
       == localImageResolver.FallbackImageSource,
    "O catálogo visual não pode transformar uma imagem HTTPS em requisição de rede.");

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
        Assert(item.Size.Length > 0, $"Tamanho comercial ausente em {item.Id}.");
        Assert(item.Artifact is null,
            $"O catálogo visual não pode simular autoridade de artefato em {item.Id}.");
        Assert(item.Description.Length >= 120, $"Texto próprio ausente ou curto em {item.Id}.");
    }
}
Assert(seenIds.Count == 902, "Nem todos os IDs foram percorridos.");

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
Assert(descriptionIds.SetEquals(seenIds), "Os XML precisam cobrir individualmente os 902 jogos.");

var retroItemIds = new HashSet<string>(StringComparer.Ordinal);
using (var manifestDocument = JsonDocument.Parse(await File.ReadAllTextAsync(manifestPath)))
{
    Assert(!manifestDocument.RootElement.TryGetProperty("enableTestDownloads", out _)
           && !manifestDocument.RootElement.TryGetProperty("testDownload", out _),
        "O manifesto público não pode manter contrato legado de download de teste.");
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
    Assert(verticalPosterCount == 902, "As 902 capas precisam usar o padrão vertical 1024x1536.");
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

var backgroundVideoDirectory = Path.Combine(
    Directory.GetParent(catalogAssetDirectory)!.FullName,
    "BackgroundVideos");
var backgroundVideoIntegrity = JsonSerializer.Deserialize<Dictionary<string, VideoIntegrity>>(
    await File.ReadAllTextAsync(Path.Combine(backgroundVideoDirectory, "background-video-integrity.json")),
    new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
var expectedBackgroundVideos = new HashSet<string>(StringComparer.Ordinal)
{
    "Turborama-background.mp4",
    "Turborama-background-nintendo-generic.mp4",
    "Turborama-background-nintendo-switch.mp4",
    "Turborama-background-nintendo-wii.mp4",
    "Turborama-background-playstation.mp4",
    "Turborama-background-ps-vita.mp4",
    "Turborama-background-ps2.mp4",
    "Turborama-background-ps4.mp4",
    "Turborama-background-ps5.mp4",
    "Turborama-background-psp.mp4",
    "Turborama-background-retro.mp4",
    "Turborama-background-sega-saturn.mp4",
    "Turborama-background-system-tools.mp4",
    "Turborama-background-windows.mp4",
    "Turborama-background-xbox-one-x.mp4"
};
Assert(backgroundVideoIntegrity.Keys.ToHashSet(StringComparer.Ordinal)
           .SetEquals(expectedBackgroundVideos),
    "O inventário precisa conter exatamente os 15 vídeos de fundo aprovados.");
foreach (var (fileName, expected) in backgroundVideoIntegrity)
{
    Assert(Path.GetFileName(fileName) == fileName && Path.GetExtension(fileName) == ".mp4",
        $"Nome de vídeo de fundo inseguro: {fileName}.");
    var videoPath = Path.Combine(backgroundVideoDirectory, fileName);
    Assert(File.Exists(videoPath), $"Vídeo de fundo ausente: {fileName}.");
    var videoBytes = await File.ReadAllBytesAsync(videoPath);
    Assert(videoBytes.LongLength == expected.Length, $"Tamanho de vídeo de fundo alterado: {fileName}.");
    Assert(videoBytes.Length >= 12 && videoBytes.AsSpan(4, 4).SequenceEqual("ftyp"u8),
        $"Contêiner MP4 de fundo inválido: {fileName}.");
    Assert(videoBytes.AsSpan().IndexOf("avc1"u8) >= 0,
        $"O vídeo de fundo precisa usar H.264 compatível com o Windows: {fileName}.");
    var hasAacTrack = videoBytes.AsSpan().IndexOf("mp4a"u8) >= 0;
    Assert(hasAacTrack, $"A faixa AAC esperada está ausente: {fileName}.");
    Assert(Convert.ToHexString(SHA256.HashData(videoBytes)).Equals(
            expected.Sha256,
            StringComparison.OrdinalIgnoreCase),
        $"SHA-256 do vídeo de fundo foi alterado: {fileName}.");
}

var accentSearch = repository.Query("system-tools", "utilitarios", 1, 100);
Assert(accentSearch.TotalItems > 0, "Busca sem acento deve encontrar a categoria 'utilitários'.");
var page = repository.Query("playstation-1", string.Empty, 99, 4);
Assert(page.TotalItems == 119, "PlayStation 1 deve ter 119 itens.");
Assert(page.TotalPages == 30 && page.CurrentPage == 30 && page.Items.Count == 3,
    "Paginação de 119 itens deve terminar na página 30 com 3 itens.");
AssertReadOnlyList(page.Items, "A página consultada não pode expor um array mutável.");

var temporaryRoot = Path.Combine(Path.GetTempPath(), "TurboramaCatalogVerifier", Guid.NewGuid().ToString("N"));
var suiteConfigExistedBeforeTests = File.Exists(TurboBoxManager.LocalDataPaths.ConfigFile);
Directory.CreateDirectory(temporaryRoot);
Exception? temporaryRootFailure = null;
try
{
    VerifyImageResolverRevalidation(
        Path.Combine(temporaryRoot, "image-resolver-tests"),
        minimalCatalog);

    using var service = new CatalogDownloadService();

    var visualOnlyItem = repository.Query("system-tools", string.Empty, 1, 4).Items[0];
    var failClosed = await service.DownloadAsync(visualOnlyItem, temporaryRoot);
    Assert(failClosed.State == CatalogDownloadState.Failed,
        "Um item visual sem descritor autorizado deve falhar fechado.");

    using var preCanceled = new CancellationTokenSource();
    preCanceled.Cancel();
    var canceledItem = CreateOfflineArtifactItem("pre-canceled", ".bin");
    var canceled = await service.DownloadAsync(canceledItem, temporaryRoot, preCanceled.Token);
    Assert(canceled.WasCanceled && canceledItem.DownloadState == CatalogDownloadState.Paused,
        "Uma interrupção precisa pausar sem apagar o progresso.");

    var unsafeItem = new CatalogItem
    {
        Id = "../../escape",
        CategoryId = "../outside",
        Artifact = CreateOfflineArtifact("safe-path", ".txt")
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
        Artifact = CreateOfflineArtifact("game-library-test", ".zip")
    };
    var gameLibraryPath = service.BuildSafeDestinationPath(
        gameLibraryRoot,
        gameLibraryItem,
        new Uri("https://github.com/game.zip"));
    var gameRelativePath = Path.GetRelativePath(gameLibraryRoot, gameLibraryPath);
    Assert(gameLibraryPath.StartsWith(
               Path.TrimEndingDirectorySeparator(Path.GetFullPath(gameLibraryRoot))
               + Path.DirectorySeparatorChar,
               StringComparison.OrdinalIgnoreCase)
           && gameRelativePath.StartsWith("artifacts" + Path.DirectorySeparatorChar),
        "O download do jogo deve permanecer sob a raiz autorizada e identidade do artefato.");

    var blockedItem = new CatalogItem
    {
        Id = "blocked",
        CategoryId = "test",
        Artifact = CreateOfflineArtifact("blocked", ".txt")
    };
    var blocked = await service.DownloadAsync(blockedItem, temporaryRoot);
    Assert(blocked.State == CatalogDownloadState.Failed && !service.IsActive(blockedItem.Id),
        "Sem provedor de sessão o serviço deve falhar fechado antes da rede.");
    Assert(TurboBoxManager.PathIdentity.OutstandingDirectoryHandles == 0,
        $"O downloader deixou {TurboBoxManager.PathIdentity.OutstandingDirectoryHandles} handles de diretório após finalizar.");
    service.Dispose();
    Assert(TurboBoxManager.PathIdentity.OutstandingDirectoryHandles == 0,
        "Dispose do serviço reabriu um lease de diretório.");

    PathIdentityVerifier.Run(Path.Combine(temporaryRoot, "path-identity-tests"));
    Assert(TurboBoxManager.PathIdentity.OutstandingDirectoryHandles == 0,
        "PathIdentityVerifier deixou leases de diretório ativos.");
    await CrossVolumeMoveVerifier.RunAsync();
    Assert(TurboBoxManager.PathIdentity.OutstandingDirectoryHandles == 0,
        "CrossVolumeMoveVerifier deixou leases de diretório ativos.");
    await DownloadResumeVerifier.RunAsync(Path.Combine(temporaryRoot, "resume-tests"));
    Assert(TurboBoxManager.PathIdentity.OutstandingDirectoryHandles == 0,
        "DownloadResumeVerifier deixou leases de diretório ativos.");
    await ArchiveExtractionVerifier.RunAsync(Path.Combine(temporaryRoot, "extraction-tests"));
    Assert(TurboBoxManager.PathIdentity.OutstandingDirectoryHandles == 0,
        "ArchiveExtractionVerifier deixou leases de diretório ativos.");
    GameLibraryLocatorVerifier.Run(Path.Combine(temporaryRoot, "library-locator-tests"));
    Assert(TurboBoxManager.PathIdentity.OutstandingDirectoryHandles == 0,
        "GameLibraryLocatorVerifier deixou leases de diretório ativos.");
    await CatalogLocalLibraryVerifier.RunAsync(
        Path.Combine(temporaryRoot, "local-library-tests"));
    Assert(TurboBoxManager.PathIdentity.OutstandingDirectoryHandles == 0,
        "CatalogLocalLibraryVerifier deixou leases de diretório ativos.");
}
catch (Exception exception)
{
    temporaryRootFailure = exception;
    throw;
}
finally
{
    if (TurboBoxManager.PathIdentity.OutstandingDirectoryHandles != 0)
    {
        var leak = $"Os verificadores deixaram {TurboBoxManager.PathIdentity.OutstandingDirectoryHandles} handles de diretório antes do cleanup final: {TurboBoxManager.PathIdentity.OutstandingDirectoryHandlePaths}";
        if (temporaryRootFailure is null) throw new InvalidOperationException(leak);
        Console.Error.WriteLine(leak);
    }
    else if (Directory.Exists(temporaryRoot))
    {
        Directory.Delete(temporaryRoot, recursive: true);
    }
}

WpfTemplateVerifier.Run("xbox-series");
SuiteProtocolVerifier.Run();
await SuiteDeviceInventoryVerifier.RunAsync();
Assert(suiteConfigExistedBeforeTests || !File.Exists(TurboBoxManager.LocalDataPaths.ConfigFile),
    "Os verificadores não podem criar suite-config.json como efeito colateral.");

Console.WriteLine("PASS: catálogo, carrossel universal, templates WPF reais e responsivos, Biblioteca 22/902, 902 capas, 902 textos XML, 45 descrições retrô, 38 vídeos de sistema e 15 vídeos de fundo íntegros, 45 pôsteres, 45 ícones retrô, 22 ícones de menu, pasta TruboRoms\\roms, protocolo de licença fail-closed, inventário auxiliar assinado, retomada, pausa, descarte e extração segura verificados.");

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

static void ExpectInvalidCatalog(string json, string manifestPath, string message)
{
    using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(json));
    try
    {
        _ = CatalogRepository.Load(stream, manifestPath);
    }
    catch (InvalidDataException)
    {
        return;
    }
    throw new InvalidOperationException(message);
}

static void AssertReadOnlyList<T>(IReadOnlyList<T> values, string message)
{
    Assert(values is not T[], message);
    if (values is not IList<T> list) return;
    Assert(list.IsReadOnly, message);
    if (list.Count == 0) return;

    try
    {
        list[0] = list[0];
    }
    catch (NotSupportedException)
    {
        return;
    }
    throw new InvalidOperationException(message);
}

static string MutateCatalog(string json, Action<JsonObject> mutation)
{
    var root = JsonNode.Parse(json)?.AsObject()
        ?? throw new InvalidOperationException("Manifesto mínimo inválido no verifier.");
    mutation(root);
    return root.ToJsonString();
}

static string CreateMinimalCatalogJson() =>
    """
    {
      "schemaVersion": 3,
      "defaultImage": "Images/_turborama-fallback.jpg",
      "categories": [
        {
          "id": "tests",
          "displayName": "Testes",
          "shortCode": "TST",
          "glyph": "T",
          "description": "Catálogo mínimo do verificador.",
          "accent": "#9DFF00",
          "order": 0,
          "sourceItemCount": 1
        }
      ],
      "items": [
        {
          "id": "00000000000000000000000000000001",
          "categoryId": "tests",
          "displayName": "",
          "title": "Teste",
          "subtitle": "",
          "category": "",
          "imagePath": "",
          "image": "Images/00000000000000000000000000000001.jpg",
          "imageAltText": "",
          "badge": "",
          "size": "",
          "version": "",
          "keywords": "",
          "description": "",
          "downloadUrl": "",
          "sha256": "",
          "downloadFileExtension": "",
          "extract": false,
          "order": 0
        }
      ],
      "packageTemplates": []
    }
    """;

static void VerifyImageResolverRevalidation(string testRoot, string validManifestJson)
{
    var catalogDirectory = Path.Combine(testRoot, "Assets", "Catalog");
    var imageDirectory = Path.Combine(catalogDirectory, "Images");
    Directory.CreateDirectory(imageDirectory);
    var manifestPath = Path.Combine(catalogDirectory, "catalog.json");
    var fallbackPath = Path.Combine(imageDirectory, "_turborama-fallback.jpg");
    var imagePath = Path.Combine(imageDirectory, "cover.jpg");
    File.WriteAllText(manifestPath, validManifestJson);
    File.WriteAllBytes(fallbackPath, [0x01]);
    File.WriteAllBytes(imagePath, [0x02]);

    var resolver = new CatalogImageResolver(
        manifestPath,
        "Images/_turborama-fallback.jpg");
    var firstResolution = resolver.Resolve("Images/cover.jpg");
    Assert(firstResolution.Length > 0 && firstResolution != resolver.FallbackImageSource,
        "A imagem local de controle deveria ser resolvida.");
    File.Delete(imagePath);
    Assert(resolver.Resolve("Images/cover.jpg") == resolver.FallbackImageSource,
        "Uma URI antiga não pode sobreviver à remoção da imagem.");

    var actualInstall = Path.Combine(testRoot, "actual-install");
    var actualCatalog = Path.Combine(actualInstall, "Assets", "Catalog");
    var actualImages = Path.Combine(actualCatalog, "Images");
    Directory.CreateDirectory(actualImages);
    File.WriteAllText(Path.Combine(actualCatalog, "catalog.json"), validManifestJson);
    File.WriteAllBytes(Path.Combine(actualImages, "_turborama-fallback.jpg"), [0x03]);
    var linkedInstall = Path.Combine(testRoot, "linked-install");
    try
    {
        _ = Directory.CreateSymbolicLink(linkedInstall, actualInstall);
    }
    catch (Exception exception) when (exception is IOException
                                       or UnauthorizedAccessException
                                       or NotSupportedException)
    {
        return;
    }

    var linkedManifest = Path.Combine(linkedInstall, "Assets", "Catalog", "catalog.json");
    try
    {
        _ = CatalogRepository.Load(linkedManifest);
    }
    catch (InvalidDataException)
    {
        var linkedResolver = new CatalogImageResolver(
            linkedManifest,
            "Images/_turborama-fallback.jpg");
        Assert(linkedResolver.FallbackImageSource.Length == 0,
            "O resolvedor deve rejeitar reparse point acima da raiz Assets.");
        return;
    }
    throw new InvalidOperationException(
        "O repositório deve rejeitar reparse point ancestral ao manifesto.");
}

static CatalogItem CreateOfflineArtifactItem(string id, string extension) => new()
{
    Id = id,
    CategoryId = "tests",
    Title = id,
    Category = "Testes",
    Artifact = CreateOfflineArtifact(id, extension)
};

static CatalogArtifactDescriptor CreateOfflineArtifact(string id, string extension)
{
    var content = new byte[] { 0x54 };
    return new CatalogArtifactDescriptor
    {
        ArtifactId = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(id)))
            .ToLowerInvariant()[..32],
        ArtifactVersion = 1,
        ContentLength = content.LongLength,
        Sha256 = Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant(),
        SafeFileName = id + extension,
        FileExtension = extension,
        ExtractPolicy = CatalogExtractPolicy.None,
        ManifestIdentity = Convert.ToHexString(
                SHA256.HashData("catalog-verifier-offline-v1"u8))
            .ToLowerInvariant()
    };
}

sealed class VideoIntegrity
{
    public string Sha256 { get; init; } = string.Empty;
    public long Length { get; init; }
}
