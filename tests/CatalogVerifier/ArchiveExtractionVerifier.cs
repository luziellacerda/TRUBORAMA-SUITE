using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using TurboBoxManager.Catalog;

internal static class ArchiveExtractionVerifier
{
    public static async Task RunAsync(string root)
    {
        Directory.CreateDirectory(root);
        VerifyRecognizedArchiveOverride();
        await VerifySuccessfulZipAsync(root);
        await VerifySamboxTextCleanupAsync(root);
        await VerifyGamePackageLayoutIsOrganizedAsync(root);
        await VerifyGamePackageCollisionIsPreservedAsync(root);
        await VerifyPartialGamePackageOrganizationRetryAsync(root);
        await VerifyOrganizerRejectsUnexpectedExtractionPathAsync(root);
        await VerifyOrganizerRejectsDestinationWithinWrapperAsync(root);
        await VerifyAuthenticatedReextractionAfterPartialOrganizationAsync(root);
        await VerifyCorruptArchivePreservesRecoveryBackupAsync(root);
        await VerifyRestartAndRedownloadRecoversWithRetainedMarkerAsync(root);
        await VerifyNamedGameLibraryAsync(root);
        await VerifyTraversalIsBlockedAsync(root);
        await VerifyInsufficientSpacePreservesPackageAsync(root);
        await VerifyCompressionRatioIsBlockedAsync(root);
        await VerifyUnknownCompressedSizeIsBlockedAsync(root);
        await VerifySolidArchiveRatioIsEnforcedAsync(root);
        await VerifyTotalSizeIsBlockedAsync(root);
        await VerifyEntryCountIsBlockedAsync(root);
        await VerifyOversizedEntryIsBlockedAsync(root);
        await VerifyPathDepthIsBlockedAsync(root);
        await VerifyDuplicateAndCaseCollisionsAreBlockedAsync(root);
        await VerifyLinkEntryIsBlockedAsync(root);
        await VerifyCancellationDuringCopyAsync(root);
        await VerifyMonotonicTimeoutDuringCopyAsync(root);
        await VerifyArchiveCannotBeSwappedAsync(root);
        await VerifyAuthorizedHashMismatchIsBlockedBeforeWriteAsync(root);
        await VerifyExtractPolicyNoneIsRejectedAsync(root);
        await VerifyForgedRecoveryMarkerIsRejectedAsync(root);
        await VerifyLongPathSegmentsAreBlockedAsync(root);
    }

    private static void VerifyRecognizedArchiveOverride()
    {
        var directRar = new CatalogArtifactDescriptor
        {
            ArtifactId = new string('a', 32),
            ArtifactVersion = 1,
            ContentLength = 123,
            Sha256 = new string('b', 64),
            SafeFileName = "jogo.rar",
            FileExtension = ".rar",
            ExtractPolicy = CatalogExtractPolicy.None,
            ManifestIdentity = new string('c', 64)
        };
        Check(CatalogArchivePolicy.IsRecognizedArchive(directRar),
            "Um RAR autorizado como download direto não foi reconhecido como pacote.");
        Check(CatalogArchivePolicy.ForExtraction(directRar).ExtractPolicy
              == CatalogExtractPolicy.ExtractArchive,
            "O RAR reconhecido não recebeu a política local de extração.");

        var executable = directRar with
        {
            SafeFileName = "jogo.exe",
            FileExtension = ".exe"
        };
        Check(!CatalogArchivePolicy.IsRecognizedArchive(executable),
            "Um download executável foi tratado indevidamente como pacote compactado.");
    }

    private static async Task VerifySamboxTextCleanupAsync(string root)
    {
        var archivePath = Path.Combine(root, "sambox-cleanup.zip");
        using (var archive = ZipFile.Open(archivePath, ZipArchiveMode.Create))
        {
            var readme = archive.CreateEntry("docs/Sambox-leia-me.txt");
            await using (var stream = readme.Open())
            await using (var writer = new StreamWriter(stream, new UTF8Encoding(false)))
                await writer.WriteAsync("""
Basta descompactar dentro da pasta do Turbobox, se pedir para substituir arquivos pode confirmar

Pacote Sambox pronto para uso.
site: https://Turbobox.club
whatsapp: (21) 99795-8935
""");
            var game = archive.CreateEntry("roms/game.iso");
            await using var gameStream = game.Open();
            await gameStream.WriteAsync("ROM-DATA"u8.ToArray());
        }

        var destinationBase = Path.Combine(root, "sambox-cleanup-base");
        Directory.CreateDirectory(destinationBase);
        var result = await new CatalogArchiveExtractor().ExtractAsync(
            archivePath, destinationBase, "PlayStation 2", "Limpeza de documento",
            itemId: "sambox-cleanup");

        Check(result.Succeeded, result.Message);
        var correctedPath = Path.Combine(result.DestinationPath, "docs", "Turbobox-leia-me.txt");
        Check(File.Exists(correctedPath), "O documento Sambox não foi renomeado.");
        Check(File.ReadAllText(correctedPath).Contains("Turbobox", StringComparison.Ordinal),
            "O conteúdo Sambox não foi corrigido para Turbobox.");
        var correctedText = File.ReadAllText(correctedPath);
        Check(correctedText.Contains("COMO USAR A PASTA TRUBOROMS", StringComparison.Ordinal)
              && correctedText.Contains("https://turbobox.lzgames.com.br/", StringComparison.Ordinal)
              && correctedText.Contains("82993474007", StringComparison.Ordinal),
            "O tutorial ou os contatos novos não foram aplicados ao documento.");
        Check(!Directory.EnumerateFiles(result.DestinationPath, "*.txt", SearchOption.AllDirectories)
                .Any(path => File.ReadAllText(path).Contains("Sambox", StringComparison.OrdinalIgnoreCase)),
            "Permaneceu referência Sambox nos documentos extraídos.");
        Check(File.Exists(Path.Combine(result.DestinationPath, "roms", "game.iso")),
            "O arquivo do jogo foi alterado pela limpeza de documentos.");
    }

    private static async Task VerifyGamePackageLayoutIsOrganizedAsync(string root)
    {
        var archivePath = Path.Combine(root, "organized-layout.zip");
        using (var archive = ZipFile.Open(archivePath, ZipArchiveMode.Create))
        {
            CreateEntry(archive, "sistema/roms/xboxone/images/game.jpg", "IMAGE");
            CreateEntry(archive, "sistema/roms/xboxone/videos/trailer.mp4", "VIDEO");
            CreateEntry(archive, "sistema/roms/xboxinstallers/Game/Game.exe", "GAME");
            CreateEntry(archive, "leia-me.txt", "site: https://Turbobox.club");
            CreateEntry(archive, "docs/cache.txt", "NAO PUBLICAR");
        }

        var gameLibrary = Path.Combine(root, "organized-base", "TruboRoms", "roms");
        Directory.CreateDirectory(gameLibrary);
        var artifact = CatalogArchiveExtractorTestExtensions.CreateAuthorizedArtifact(archivePath);
        var extracted = await new CatalogArchiveExtractor().ExtractAsync(
            archivePath,
            gameLibrary,
            "Xbox One",
            "Game",
            artifact,
            baseDirectoryIsGameLibrary: true,
            itemId: "organized-game");
        Check(extracted.Succeeded, extracted.Message);

        var organized = CatalogGamePackageOrganizer.Organize(
            extracted.DestinationPath,
            gameLibrary,
            artifact,
            "Xbox One",
            "organized-game");
        Check(organized.InstalledPath.Equals(
                  Path.GetFullPath(gameLibrary),
                  StringComparison.OrdinalIgnoreCase),
            "O pacote organizado não apontou para a raiz TruboRoms\\roms.");
        Check(File.Exists(Path.Combine(gameLibrary, "xboxone", "images", "game.jpg"))
              && File.Exists(Path.Combine(gameLibrary, "xboxinstallers", "Game", "Game.exe")),
            "Os arquivos úteis não foram publicados diretamente na pasta de ROMs.");
        Check(!Directory.Exists(Path.Combine(gameLibrary, "xboxone", "videos")),
            "A pasta videos do pacote deveria ter sido excluída.");
        Check(!Directory.Exists(extracted.DestinationPath)
              && File.Exists(Path.Combine(
                  organized.MarkerDirectory,
                  CatalogArchiveExtractor.CompletionMarkerFileName)),
            "O invólucro intermediário não foi removido ou o comprovante foi perdido.");
        Check(!File.Exists(Path.Combine(gameLibrary, "docs", "cache.txt")),
            "Arquivos fora de sistema\\roms não podem vazar para a biblioteca.");

        using var downloadService = new CatalogDownloadService();
        var localService = new CatalogLocalLibraryService(downloadService);
        var item = new CatalogItem
        {
            Id = "organized-game",
            CategoryId = "xbox-one",
            Category = "Xbox One",
            Title = "Game",
            Extract = true,
            Artifact = artifact
        };
        var inspection = (await localService.InspectAsync(
            gameLibrary,
            [item],
            CancellationToken.None))[0];
        Check(inspection.Status == CatalogLocalGameStatus.Downloaded,
            $"O pacote achatado precisa aparecer instalado. Estado: {inspection.Status}; {inspection.Detail}");
    }

    private static async Task VerifyGamePackageCollisionIsPreservedAsync(string root)
    {
        var archivePath = Path.Combine(root, "organized-collision.zip");
        using (var archive = ZipFile.Open(archivePath, ZipArchiveMode.Create))
            CreateEntry(archive, "sistema/roms/xboxone/Game/data.bin", "NOVO");

        var gameLibrary = Path.Combine(root, "collision-base", "TruboRoms", "roms");
        var occupied = Path.Combine(gameLibrary, "xboxone", "Game", "data.bin");
        Directory.CreateDirectory(Path.GetDirectoryName(occupied)!);
        await File.WriteAllTextAsync(occupied, "ANTIGO");
        var artifact = CatalogArchiveExtractorTestExtensions.CreateAuthorizedArtifact(archivePath);
        var extracted = await new CatalogArchiveExtractor().ExtractAsync(
            archivePath,
            gameLibrary,
            "Xbox One",
            "Game",
            artifact,
            baseDirectoryIsGameLibrary: true,
            itemId: "collision-game");
        Check(extracted.Succeeded, extracted.Message);

        try
        {
            _ = CatalogGamePackageOrganizer.Organize(
                extracted.DestinationPath,
                gameLibrary,
                artifact,
                "Xbox One",
                "collision-game");
            throw new InvalidDataException("Uma colisão diferente deveria interromper a organização.");
        }
        catch (IOException)
        {
        }
        Check(await File.ReadAllTextAsync(occupied) == "ANTIGO"
              && Directory.Exists(extracted.DestinationPath),
            "A colisão alterou o arquivo anterior ou destruiu a cópia recuperável.");
    }

    private static async Task VerifyPartialGamePackageOrganizationRetryAsync(string root)
    {
        const string category = "Xbox One";
        const string itemId = "partial-organization-retry";
        var archivePath = Path.Combine(root, "partial-organization-retry.zip");
        using (var archive = ZipFile.Open(archivePath, ZipArchiveMode.Create))
        {
            CreateEntry(archive, "sistema/roms/xboxone/Retry/first.bin", "FIRST");
            CreateEntry(archive, "sistema/roms/xboxone/Retry/second.bin", "SECOND");
        }

        var gameLibrary = Path.Combine(root, "partial-retry-base", "TruboRoms", "roms");
        Directory.CreateDirectory(gameLibrary);
        var artifact = CatalogArchiveExtractorTestExtensions.CreateAuthorizedArtifact(archivePath);
        var extracted = await new CatalogArchiveExtractor().ExtractAsync(
            archivePath,
            gameLibrary,
            category,
            "Retry parcial",
            artifact,
            baseDirectoryIsGameLibrary: true,
            itemId: itemId);
        Check(extracted.Succeeded, extracted.Message);

        var firstSource = Path.Combine(
            extracted.DestinationPath,
            "sistema",
            "roms",
            "xboxone",
            "Retry",
            "first.bin");
        var firstDestination = Path.Combine(
            gameLibrary,
            "xboxone",
            "Retry",
            "first.bin");
        Directory.CreateDirectory(Path.GetDirectoryName(firstDestination)!);
        File.Move(firstSource, firstDestination, overwrite: false);

        var organized = CatalogGamePackageOrganizer.Organize(
            extracted.DestinationPath,
            gameLibrary,
            artifact,
            category,
            itemId);
        Check(File.Exists(firstDestination)
              && File.Exists(Path.Combine(
                  gameLibrary,
                  "xboxone",
                  "Retry",
                  "second.bin"))
              && !Directory.Exists(extracted.DestinationPath)
              && File.Exists(Path.Combine(
                  organized.MarkerDirectory,
                  CatalogArchiveExtractor.CompletionMarkerFileName)),
            "A retomada não concluiu uma organização parcialmente publicada.");

        const string markerItemId = "foreign-marker-source";
        var foreignArchivePath = Path.Combine(root, "foreign-marker.zip");
        using (var archive = ZipFile.Open(foreignArchivePath, ZipArchiveMode.Create))
            CreateEntry(archive, "sistema/roms/xboxone/Foreign/data.bin", "FOREIGN");
        var foreignLibrary = Path.Combine(root, "foreign-marker-base", "TruboRoms", "roms");
        Directory.CreateDirectory(foreignLibrary);
        var foreignArtifact = CatalogArchiveExtractorTestExtensions.CreateAuthorizedArtifact(
            foreignArchivePath);
        var foreignExtraction = await new CatalogArchiveExtractor().ExtractAsync(
            foreignArchivePath,
            foreignLibrary,
            category,
            "Marker estrangeiro",
            foreignArtifact,
            baseDirectoryIsGameLibrary: true,
            itemId: markerItemId);
        Check(foreignExtraction.Succeeded, foreignExtraction.Message);

        var differentArtifactId = new string(
            foreignArtifact.ArtifactId[0] == '0' ? '1' : '0',
            foreignArtifact.ArtifactId.Length);
        var wrongArtifactRejected = false;
        try
        {
            _ = CatalogGamePackageOrganizer.Organize(
                foreignExtraction.DestinationPath,
                foreignLibrary,
                foreignArtifact with { ArtifactId = differentArtifactId },
                category,
                markerItemId);
        }
        catch (InvalidDataException)
        {
            wrongArtifactRejected = true;
        }
        Check(wrongArtifactRejected,
            "Um marker de outro artefato precisa ser recusado antes da publicação.");

        var wrongItemRejected = false;
        try
        {
            _ = CatalogGamePackageOrganizer.Organize(
                foreignExtraction.DestinationPath,
                foreignLibrary,
                foreignArtifact,
                category,
                "foreign-marker-other-item");
        }
        catch (InvalidDataException)
        {
            wrongItemRejected = true;
        }
        Check(wrongItemRejected
              && Directory.Exists(foreignExtraction.DestinationPath)
              && !File.Exists(Path.Combine(
                  foreignLibrary,
                  "xboxone",
                  "Foreign",
                  "data.bin")),
            "Um marker de outro item foi aceito ou alterou a biblioteca antes de ser recusado.");

        _ = CatalogGamePackageOrganizer.Organize(
            foreignExtraction.DestinationPath,
            foreignLibrary,
            foreignArtifact,
            category,
            markerItemId);
    }

    private static async Task VerifyOrganizerRejectsUnexpectedExtractionPathAsync(string root)
    {
        const string category = "Path Check";
        const string itemId = "expected-wrapper";
        var archivePath = Path.Combine(root, "unexpected-wrapper.zip");
        using (var archive = ZipFile.Open(archivePath, ZipArchiveMode.Create))
            CreateEntry(archive, "sistema/roms/Path Check/Game/data.bin", "DATA");

        var gameLibrary = Path.Combine(root, "unexpected-wrapper-base", "TruboRoms", "roms");
        Directory.CreateDirectory(gameLibrary);
        var artifact = CatalogArchiveExtractorTestExtensions.CreateAuthorizedArtifact(archivePath);
        var extracted = await new CatalogArchiveExtractor().ExtractAsync(
            archivePath,
            gameLibrary,
            category,
            "Wrapper esperado",
            artifact,
            baseDirectoryIsGameLibrary: true,
            itemId: itemId);
        Check(extracted.Succeeded, extracted.Message);

        var divergentPath = Path.Combine(
            Path.GetDirectoryName(extracted.DestinationPath)!,
            "unexpected-wrapper");
        Directory.Move(extracted.DestinationPath, divergentPath);
        var rejected = false;
        try
        {
            _ = CatalogGamePackageOrganizer.Organize(
                divergentPath,
                gameLibrary,
                artifact,
                category,
                itemId);
        }
        catch (InvalidDataException)
        {
            rejected = true;
        }
        Check(rejected && Directory.Exists(divergentPath),
            "O Organizer aceitou ou removeu um wrapper fora do destino determinístico do item.");
    }

    private static async Task VerifyOrganizerRejectsDestinationWithinWrapperAsync(string root)
    {
        const string category = "Nested Destination";
        const string itemId = "nested-destination";
        var gameLibrary = Path.Combine(root, "nested-destination-base", "TruboRoms", "roms");
        Directory.CreateDirectory(gameLibrary);
        var stableDirectoryName = Path.GetFileName(
            CatalogArchiveExtractor.BuildGameDestinationPath(
                gameLibrary,
                category,
                itemId));
        var archivePath = Path.Combine(root, "nested-destination.zip");
        using (var archive = ZipFile.Open(archivePath, ZipArchiveMode.Create))
            CreateEntry(
                archive,
                $"sistema/roms/{category}/{stableDirectoryName}/data.bin",
                "NESTED");

        var artifact = CatalogArchiveExtractorTestExtensions.CreateAuthorizedArtifact(archivePath);
        var extracted = await new CatalogArchiveExtractor().ExtractAsync(
            archivePath,
            gameLibrary,
            category,
            "Destino interno",
            artifact,
            baseDirectoryIsGameLibrary: true,
            itemId: itemId);
        Check(extracted.Succeeded, extracted.Message);

        var rejected = false;
        try
        {
            _ = CatalogGamePackageOrganizer.Organize(
                extracted.DestinationPath,
                gameLibrary,
                artifact,
                category,
                itemId);
        }
        catch (InvalidDataException)
        {
            rejected = true;
        }
        Check(rejected
              && Directory.Exists(extracted.DestinationPath)
              && !Directory.Exists(CatalogGamePackageOrganizer.BuildMarkerDirectory(
                  gameLibrary,
                  stableDirectoryName)),
            "O Organizer publicou dentro do próprio wrapper ou removeu o pacote rejeitado.");
    }

    private static async Task VerifyAuthenticatedReextractionAfterPartialOrganizationAsync(string root)
    {
        const string category = "Authenticated Retry";
        const string itemId = "authenticated-retry";
        var archivePath = Path.Combine(root, "authenticated-retry.zip");
        using (var archive = ZipFile.Open(archivePath, ZipArchiveMode.Create))
        {
            CreateEntry(archive, "sistema/roms/Authenticated Retry/Published/first.bin", "FIRST");
            CreateEntry(archive, "sistema/roms/Authenticated Retry/Published/second.bin", "SECOND");
        }

        var gameLibrary = Path.Combine(root, "authenticated-retry-base", "TruboRoms", "roms");
        Directory.CreateDirectory(gameLibrary);
        var artifact = CatalogArchiveExtractorTestExtensions.CreateAuthorizedArtifact(archivePath);
        var extractor = new CatalogArchiveExtractor();
        var firstExtraction = await extractor.ExtractAsync(
            archivePath,
            gameLibrary,
            category,
            "Retry autenticado",
            artifact,
            baseDirectoryIsGameLibrary: true,
            itemId: itemId);
        Check(firstExtraction.Succeeded, firstExtraction.Message);

        var firstSource = Path.Combine(
            firstExtraction.DestinationPath,
            "sistema",
            "roms",
            category,
            "Published",
            "first.bin");
        var firstDestination = Path.Combine(
            gameLibrary,
            category,
            "Published",
            "first.bin");
        Directory.CreateDirectory(Path.GetDirectoryName(firstDestination)!);
        File.Move(firstSource, firstDestination, overwrite: false);

        var markerPath = Path.Combine(
            firstExtraction.DestinationPath,
            CatalogArchiveExtractor.CompletionMarkerFileName);
        var forgedMarker = JsonNode.Parse(await File.ReadAllTextAsync(markerPath))!.AsObject();
        forgedMarker["ArtifactId"] = new string('0', 32);
        await File.WriteAllTextAsync(markerPath, forgedMarker.ToJsonString());

        var recoveryPath = await Task.Run(() =>
            CatalogGamePackageOrganizer.MovePartialExtractionToRecovery(
                firstExtraction.DestinationPath,
                gameLibrary,
                category,
                itemId));
        Check(!Directory.Exists(firstExtraction.DestinationPath)
              && Directory.Exists(recoveryPath)
              && File.Exists(firstDestination),
            "O backup confinado do wrapper parcial não foi preservado ou atingiu um arquivo já publicado.");

        var authenticatedExtraction = await extractor.ExtractAsync(
            archivePath,
            gameLibrary,
            category,
            "Retry autenticado",
            artifact,
            baseDirectoryIsGameLibrary: true,
            itemId: itemId);
        Check(authenticatedExtraction.Succeeded, authenticatedExtraction.Message);
        var organized = CatalogGamePackageOrganizer.Organize(
            authenticatedExtraction.DestinationPath,
            gameLibrary,
            artifact,
            category,
            itemId);
        Check(File.Exists(firstDestination)
              && File.Exists(Path.Combine(
                  gameLibrary,
                  category,
                  "Published",
                  "second.bin"))
              && !Directory.Exists(authenticatedExtraction.DestinationPath)
              && File.Exists(Path.Combine(
                  organized.MarkerDirectory,
                  CatalogArchiveExtractor.CompletionMarkerFileName)),
            "A reextração autenticada não concluiu a publicação idempotente após o retry.");
        Check(CatalogGamePackageOrganizer.DeleteRecoveryBackup(
                  recoveryPath,
                  gameLibrary)
              && !Directory.Exists(recoveryPath),
            "O backup de recuperação não foi removido depois da conclusão autenticada.");
    }

    private static async Task VerifyCorruptArchivePreservesRecoveryBackupAsync(string root)
    {
        const string category = "Corrupt Retry";
        const string itemId = "corrupt-retry";
        var archivePath = Path.Combine(root, "corrupt-retry.zip");
        using (var archive = ZipFile.Open(archivePath, ZipArchiveMode.Create))
            CreateEntry(archive, "sistema/roms/Corrupt Retry/Published/game.bin", "RECOVER");

        var gameLibrary = Path.Combine(root, "corrupt-retry-base", "TruboRoms", "roms");
        Directory.CreateDirectory(gameLibrary);
        var artifact = CatalogArchiveExtractorTestExtensions.CreateAuthorizedArtifact(archivePath);
        var extractor = new CatalogArchiveExtractor();
        var firstExtraction = await extractor.ExtractAsync(
            archivePath,
            gameLibrary,
            category,
            "Archive corrompido no retry",
            artifact,
            baseDirectoryIsGameLibrary: true,
            itemId: itemId);
        Check(firstExtraction.Succeeded, firstExtraction.Message);
        var recoveryPath = CatalogGamePackageOrganizer.MovePartialExtractionToRecovery(
            firstExtraction.DestinationPath,
            gameLibrary,
            category,
            itemId);
        var preservedPayload = Path.Combine(
            recoveryPath,
            "sistema",
            "roms",
            category,
            "Published",
            "game.bin");
        var preservedMarker = Path.Combine(
            recoveryPath,
            CatalogArchiveExtractor.CompletionMarkerFileName);
        Check(Directory.Exists(recoveryPath)
              && File.Exists(preservedPayload)
              && File.Exists(preservedMarker),
            "O wrapper parcial não chegou íntegro à área de recuperação.");

        await File.WriteAllTextAsync(archivePath, "CORRUPTED");
        var failedRetry = await extractor.ExtractAsync(
            archivePath,
            gameLibrary,
            category,
            "Archive corrompido no retry",
            artifact,
            baseDirectoryIsGameLibrary: true,
            itemId: itemId);
        Check(failedRetry.Status == CatalogArchiveExtractionStatus.Failed
              && Directory.Exists(recoveryPath)
              && File.Exists(preservedPayload)
              && File.Exists(preservedMarker)
              && !Directory.Exists(firstExtraction.DestinationPath),
            "Uma reextração com archive corrompido removeu ou alterou o backup recuperável.");
    }

    private static void CreateEntry(ZipArchive archive, string path, string content)
    {
        var entry = archive.CreateEntry(path);
        using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
        writer.Write(content);
    }

    private static async Task VerifySuccessfulZipAsync(string root)
    {
        var archivePath = Path.Combine(root, "valid.zip");
        CreateZip(archivePath, "roms/game.txt", "conteúdo Turborama");
        var destinationBase = Path.Combine(root, "success-base");
        Directory.CreateDirectory(destinationBase);

        var result = await new CatalogArchiveExtractor().ExtractAsync(
            archivePath,
            destinationBase,
            "Nintendo",
            "Pacote NES",
            itemId: "nes-package-001");

        Check(result.Succeeded, result.Message);
        Check(Path.GetFileName(result.DestinationPath).StartsWith(
                "nes-package-001-",
                StringComparison.Ordinal),
            "O destino final não foi derivado do itemId estável.");
        Check(File.Exists(Path.Combine(result.DestinationPath, "roms", "game.txt")),
            "O arquivo do ZIP não foi extraído.");
        Check(File.Exists(archivePath) && result.ArchivePreserved,
            "O extrator não pode apagar o pacote antes da UI confirmar sucesso.");

        var recovered = await new CatalogArchiveExtractor().ExtractAsync(
            archivePath,
            destinationBase,
            "Nintendo",
            "Título alterado que não pode mover o destino",
            itemId: "nes-package-001");
        Check(recovered.Succeeded && recovered.DestinationPath == result.DestinationPath,
            "O itemId deveria recuperar a extração mesmo quando o título de exibição mudar.");

        var secondArchivePath = Path.Combine(root, "valid-second.zip");
        CreateZip(secondArchivePath, "roms/game.txt", "outro conteúdo");
        var second = await new CatalogArchiveExtractor().ExtractAsync(
            secondArchivePath,
            destinationBase,
            "Nintendo",
            "Pacote NES",
            itemId: "nes-package-002");
        Check(second.Succeeded && second.DestinationPath != result.DestinationPath,
            "Itens com o mesmo título e IDs distintos não podem colidir no destino.");
    }

    private static async Task VerifyRestartAndRedownloadRecoversWithRetainedMarkerAsync(string root)
    {
        var archivePath = Path.Combine(root, "restart-redownload.zip");
        var redownloadSourcePath = Path.Combine(root, "restart-redownload-source.zip");
        CreateZip(archivePath, "content/game.rom", "reinício Turborama");
        File.Copy(archivePath, redownloadSourcePath);
        var artifact = CatalogArchiveExtractorTestExtensions.CreateAuthorizedArtifact(archivePath);
        var destinationBase = CreateDestinationBase(root, "restart-redownload-base");

        var first = await new CatalogArchiveExtractor().ExtractAsync(
            archivePath,
            destinationBase,
            "Testes",
            "Reinício e redownload",
            artifact,
            itemId: "restart-redownload");
        Check(first.Succeeded, first.Message);

        var markerPath = Path.Combine(
            first.DestinationPath,
            CatalogArchiveExtractor.CompletionMarkerFileName);
        var payloadPath = Path.Combine(first.DestinationPath, "content", "game.rom");
        var markerBeforeCleanup = File.ReadAllBytes(markerPath);
        var payloadBeforeRestart = File.ReadAllBytes(payloadPath);
        try
        {
            var archiveDeleted =
                CatalogExtractionCompletionCleanup.DeleteArchivePreservingRecoveryMarker(
                    archivePath,
                    root,
                    first.DestinationPath);
            Check(archiveDeleted && !File.Exists(archivePath),
                "O cleanup pós-extração deveria apagar somente o pacote compactado.");
            Check(File.Exists(markerPath),
                "O cleanup pós-extração não pode apagar o marker necessário à recuperação.");
            Check(File.ReadAllBytes(markerPath).SequenceEqual(markerBeforeCleanup),
                "O cleanup pós-extração não pode alterar o marker de recuperação.");

            File.Copy(redownloadSourcePath, archivePath);
            var recoveredAfterRestart = await new CatalogArchiveExtractor().ExtractAsync(
                archivePath,
                destinationBase,
                "Testes",
                "Reinício e redownload",
                artifact,
                itemId: "restart-redownload");

            Check(recoveredAfterRestart.Succeeded
                  && recoveredAfterRestart.DestinationPath == first.DestinationPath,
                "Após reinício e redownload idêntico, a extração deveria ser recuperada de forma idempotente.");
            Check(File.Exists(markerPath),
                "A recuperação idempotente precisa manter o marker autenticado no destino.");
            Check(File.ReadAllBytes(payloadPath).SequenceEqual(payloadBeforeRestart),
                "A recuperação idempotente não pode substituir ou alterar o payload publicado.");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(markerBeforeCleanup);
            CryptographicOperations.ZeroMemory(payloadBeforeRestart);
        }
    }

    private static async Task VerifyNamedGameLibraryAsync(string root)
    {
        var archivePath = Path.Combine(root, "game-library.zip");
        CreateZip(archivePath, "content/game.rom", "rom Turborama");
        var libraryRoot = Path.Combine(root, CatalogArchiveExtractor.GameLibraryFolderName);
        Directory.CreateDirectory(libraryRoot);

        var result = await new CatalogArchiveExtractor().ExtractAsync(
            archivePath,
            libraryRoot,
            "Jogos retrô",
            "Pacote SNES",
            baseDirectoryIsGameLibrary: true,
            itemId: "snes-package-001");

        Check(result.Succeeded, result.Message);
        var relativeDestination = Path.GetRelativePath(libraryRoot, result.DestinationPath);
        Check(relativeDestination.Split(Path.DirectorySeparatorChar).Length == 2
              && relativeDestination.StartsWith(
                  "Jogos retrô" + Path.DirectorySeparatorChar + "snes-package-001-",
                  StringComparison.OrdinalIgnoreCase),
            "A extração não preservou categoria/itemId dentro da pasta mestre.");
        Check(File.Exists(Path.Combine(result.DestinationPath, "content", "game.rom")),
            "O jogo não foi extraído dentro da pasta mestre.");

        var wrongRoot = Path.Combine(root, "wrong-game-library-name");
        Directory.CreateDirectory(wrongRoot);
        var rejected = await new CatalogArchiveExtractor().ExtractAsync(
            archivePath,
            wrongRoot,
            "Jogos retrô",
            "Outro pacote",
            baseDirectoryIsGameLibrary: true);
        Check(rejected.Status == CatalogArchiveExtractionStatus.Failed,
            "Uma pasta mestre com nome incorreto deveria ser rejeitada.");
    }

    private static async Task VerifyTraversalIsBlockedAsync(string root)
    {
        var archivePath = Path.Combine(root, "traversal.zip");
        CreateZip(archivePath, "../fora.txt", "bloquear");
        var destinationBase = Path.Combine(root, "traversal-base");
        Directory.CreateDirectory(destinationBase);

        var result = await new CatalogArchiveExtractor().ExtractAsync(
            archivePath,
            destinationBase,
            "Testes",
            "Traversal");

        Check(result.Status == CatalogArchiveExtractionStatus.Failed,
            "Um caminho ../ dentro do pacote deveria ser bloqueado.");
        Check(File.Exists(archivePath), "O pacote bloqueado deveria permanecer para análise.");
        Check(!File.Exists(Path.Combine(destinationBase, "fora.txt")),
            "A entrada maliciosa escapou da pasta TruboRoms.");
    }

    private static async Task VerifyInsufficientSpacePreservesPackageAsync(string root)
    {
        var archivePath = Path.Combine(root, "space.zip");
        CreateZip(archivePath, "game.bin", "dados");
        var destinationBase = Path.Combine(root, "space-base");
        Directory.CreateDirectory(destinationBase);
        // Do not derive an impossible reserve from a live free-space snapshot:
        // another process can release space between this line and the extractor's
        // own check, making the test intermittently succeed. Keep enough headroom
        // for this tiny archive while choosing a reserve no real target can meet.
        const long unavailableReserve = long.MaxValue - (1024L * 1024L);
        var extractor = new CatalogArchiveExtractor(new CatalogArchiveExtractionOptions
        {
            MinimumFreeSpaceReserveBytes = unavailableReserve
        });

        var result = await extractor.ExtractAsync(
            archivePath,
            destinationBase,
            "Testes",
            "Sem espaço");

        Check(result.NeedsAnotherDrive, "A falta de espaço deveria pedir outro HD.");
        Check(File.Exists(archivePath) && result.ArchivePreserved,
            "A falta de espaço não pode apagar o pacote compactado.");
    }

    private static async Task VerifyCompressionRatioIsBlockedAsync(string root)
    {
        var archivePath = Path.Combine(root, "ratio-bomb.zip");
        CreateRepeatedByteZip(archivePath, "bomb.bin", 2 * 1024 * 1024, 0);
        var destinationBase = CreateDestinationBase(root, "ratio-base");
        var extractor = new CatalogArchiveExtractor(new CatalogArchiveExtractionOptions
        {
            MinimumFreeSpaceReserveBytes = 0,
            MaximumTotalUncompressedBytes = 4 * 1024 * 1024,
            MaximumEntryUncompressedBytes = 4 * 1024 * 1024,
            MaximumCompressionRatio = 2
        });

        var result = await extractor.ExtractAsync(
            archivePath,
            destinationBase,
            "Testes",
            "Bomba de razão",
            itemId: "ratio-bomb");

        Check(result.Status == CatalogArchiveExtractionStatus.Failed
              && result.Message.Contains("razão", StringComparison.OrdinalIgnoreCase),
            "Um ZIP com razão de compressão excessiva deveria falhar no planejamento.");
        Check(!Directory.Exists(result.DestinationPath) && File.Exists(archivePath),
            "A rejeição da razão não pode publicar conteúdo nem apagar o pacote.");
    }

    private static async Task VerifyTotalSizeIsBlockedAsync(string root)
    {
        var archivePath = Path.Combine(root, "total-limit.zip");
        CreateZipWithEntries(
            archivePath,
            ("one.bin", new string('a', 2_048)),
            ("two.bin", new string('b', 2_048)));
        var destinationBase = CreateDestinationBase(root, "total-limit-base");
        var extractor = new CatalogArchiveExtractor(new CatalogArchiveExtractionOptions
        {
            MinimumFreeSpaceReserveBytes = 0,
            MaximumTotalUncompressedBytes = 3_000,
            MaximumEntryUncompressedBytes = 3_000,
            MaximumCompressionRatio = 10_000
        });

        var result = await extractor.ExtractAsync(
            archivePath,
            destinationBase,
            "Testes",
            "Limite total",
            itemId: "total-limit");

        Check(result.Status == CatalogArchiveExtractionStatus.Failed
              && result.Message.Contains("limite total", StringComparison.OrdinalIgnoreCase),
            "A soma descompactada acima do teto deveria ser rejeitada.");
    }

    private static async Task VerifyUnknownCompressedSizeIsBlockedAsync(string root)
    {
        var archivePath = Path.Combine(root, "unknown-compressed-size.zip");
        CreateRepeatedByteZip(archivePath, "payload.bin", 4 * 1024, 0x44);
        ZeroZipCompressedSizes(archivePath);
        var destinationBase = CreateDestinationBase(root, "unknown-compressed-size-base");
        var extractor = new CatalogArchiveExtractor(new CatalogArchiveExtractionOptions
        {
            MinimumFreeSpaceReserveBytes = 0,
            MaximumTotalUncompressedBytes = 16 * 1024,
            MaximumEntryUncompressedBytes = 16 * 1024,
            MaximumCompressionRatio = 10_000
        });

        var result = await extractor.ExtractAsync(
            archivePath,
            destinationBase,
            "Testes",
            "Tamanho compactado desconhecido",
            itemId: "unknown-compressed-size");

        Check(result.Status == CatalogArchiveExtractionStatus.Failed
              && (result.Message.Contains("razão", StringComparison.OrdinalIgnoreCase)
                  || result.Message.Contains("compactado", StringComparison.OrdinalIgnoreCase)),
            "Uma entrada ZIP não vazia sem tamanho compactado verificável deveria falhar fechada.");
        Check(!Directory.Exists(result.DestinationPath),
            "Metadados compactados desconhecidos não podem publicar conteúdo.");
    }

    private static async Task VerifySolidArchiveRatioIsEnforcedAsync(string root)
    {
        var archivePath = Path.Combine(root, "solid.7z");
        CreateSolidSevenZip(archivePath);
        var acceptedBase = CreateDestinationBase(root, "solid-accepted-base");
        var permissiveExtractor = new CatalogArchiveExtractor(new CatalogArchiveExtractionOptions
        {
            MinimumFreeSpaceReserveBytes = 0,
            MaximumTotalUncompressedBytes = 1024,
            MaximumEntryUncompressedBytes = 1024,
            MaximumCompressionRatio = 10
        });

        var accepted = await permissiveExtractor.ExtractAsync(
            archivePath,
            acceptedBase,
            "Testes",
            "Sólido permitido",
            itemId: "solid-accepted");
        Check(accepted.Succeeded
              && File.Exists(Path.Combine(accepted.DestinationPath, "one.json"))
              && File.Exists(Path.Combine(accepted.DestinationPath, "two.json")),
            "O 7z sólido deveria usar o tamanho compactado agregado verificável.");

        var rejectedBase = CreateDestinationBase(root, "solid-rejected-base");
        var strictExtractor = new CatalogArchiveExtractor(new CatalogArchiveExtractionOptions
        {
            MinimumFreeSpaceReserveBytes = 0,
            MaximumTotalUncompressedBytes = 1024,
            MaximumEntryUncompressedBytes = 1024,
            MaximumCompressionRatio = 2
        });
        var rejected = await strictExtractor.ExtractAsync(
            archivePath,
            rejectedBase,
            "Testes",
            "Sólido bloqueado",
            itemId: "solid-rejected");

        Check(rejected.Status == CatalogArchiveExtractionStatus.Failed
              && rejected.Message.Contains("razão", StringComparison.OrdinalIgnoreCase),
            "A razão agregada de um arquivo sólido deveria ser aplicada antes da extração.");
        Check(!Directory.Exists(rejected.DestinationPath),
            "Um arquivo sólido acima da razão máxima não pode publicar conteúdo.");
    }

    private static async Task VerifyOversizedEntryIsBlockedAsync(string root)
    {
        var archivePath = Path.Combine(root, "oversized-entry.zip");
        CreateRepeatedByteZip(archivePath, "large.bin", 16 * 1024, 0x5A);
        var destinationBase = CreateDestinationBase(root, "oversized-base");
        var extractor = new CatalogArchiveExtractor(new CatalogArchiveExtractionOptions
        {
            MinimumFreeSpaceReserveBytes = 0,
            MaximumTotalUncompressedBytes = 64 * 1024,
            MaximumEntryUncompressedBytes = 4 * 1024,
            MaximumCompressionRatio = 10_000
        });

        var result = await extractor.ExtractAsync(
            archivePath,
            destinationBase,
            "Testes",
            "Entrada grande",
            itemId: "oversized-entry");

        Check(result.Status == CatalogArchiveExtractionStatus.Failed
              && result.Message.Contains("por arquivo", StringComparison.OrdinalIgnoreCase),
            "Uma entrada acima do teto individual deveria ser rejeitada.");
    }

    private static async Task VerifyEntryCountIsBlockedAsync(string root)
    {
        var archivePath = Path.Combine(root, "entry-count.zip");
        CreateZipWithEntries(
            archivePath,
            ("one.bin", "one"),
            ("two.bin", "two"),
            ("three.bin", "three"));
        var destinationBase = CreateDestinationBase(root, "entry-count-base");
        var extractor = new CatalogArchiveExtractor(new CatalogArchiveExtractionOptions
        {
            MinimumFreeSpaceReserveBytes = 0,
            MaximumEntryCount = 2
        });

        var result = await extractor.ExtractAsync(
            archivePath,
            destinationBase,
            "Testes",
            "Contagem",
            itemId: "entry-count");

        Check(result.Status == CatalogArchiveExtractionStatus.Failed
              && result.Message.Contains("limite de 2 entradas", StringComparison.OrdinalIgnoreCase),
            "Um pacote acima do teto de entradas deveria ser rejeitado.");
    }

    private static async Task VerifyPathDepthIsBlockedAsync(string root)
    {
        var archivePath = Path.Combine(root, "deep-path.zip");
        CreateZip(archivePath, "one/two/three/four/game.bin", "depth");
        var destinationBase = CreateDestinationBase(root, "deep-base");
        var extractor = new CatalogArchiveExtractor(new CatalogArchiveExtractionOptions
        {
            MinimumFreeSpaceReserveBytes = 0,
            MaximumPathDepth = 4
        });

        var result = await extractor.ExtractAsync(
            archivePath,
            destinationBase,
            "Testes",
            "Profundidade",
            itemId: "deep-path");

        Check(result.Status == CatalogArchiveExtractionStatus.Failed
              && result.Message.Contains("profundidade", StringComparison.OrdinalIgnoreCase),
            "Um caminho além da profundidade configurada deveria ser rejeitado.");
    }

    private static async Task VerifyDuplicateAndCaseCollisionsAreBlockedAsync(string root)
    {
        var caseArchive = Path.Combine(root, "case-collision.zip");
        CreateZipWithEntries(
            caseArchive,
            ("roms/Game.bin", "first"),
            ("ROMS/game.bin", "second"));
        var caseBase = CreateDestinationBase(root, "case-collision-base");
        var caseResult = await new CatalogArchiveExtractor().ExtractAsync(
            caseArchive,
            caseBase,
            "Testes",
            "Case collision",
            itemId: "case-collision");
        Check(caseResult.Status == CatalogArchiveExtractionStatus.Failed
              && caseResult.Message.Contains("maiúsculas", StringComparison.OrdinalIgnoreCase),
            "Caminhos que colidem apenas por caixa deveriam ser rejeitados em qualquer sistema.");

        var duplicateArchive = Path.Combine(root, "duplicate.zip");
        CreateZipWithEntries(
            duplicateArchive,
            ("roms/game.bin", "first"),
            ("roms/game.bin", "second"));
        var duplicateBase = CreateDestinationBase(root, "duplicate-base");
        var duplicateResult = await new CatalogArchiveExtractor().ExtractAsync(
            duplicateArchive,
            duplicateBase,
            "Testes",
            "Duplicata",
            itemId: "duplicate");
        Check(duplicateResult.Status == CatalogArchiveExtractionStatus.Failed
              && duplicateResult.Message.Contains("duplicados", StringComparison.OrdinalIgnoreCase),
            "Caminhos exatamente duplicados deveriam ser rejeitados.");
    }

    private static async Task VerifyLinkEntryIsBlockedAsync(string root)
    {
        var archivePath = Path.Combine(root, "link-entry.zip");
        using (var archive = ZipFile.Open(archivePath, ZipArchiveMode.Create))
        {
            var entry = archive.CreateEntry("link-to-outside", CompressionLevel.NoCompression);
            entry.ExternalAttributes = unchecked((int)0xA1FF0000); // Unix symbolic link, mode 0777.
            using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
            writer.Write("../outside");
        }
        var destinationBase = CreateDestinationBase(root, "link-base");

        var result = await new CatalogArchiveExtractor().ExtractAsync(
            archivePath,
            destinationBase,
            "Testes",
            "Link",
            itemId: "link-entry");

        Check(result.Status == CatalogArchiveExtractionStatus.Failed
              && (result.Message.Contains("link", StringComparison.OrdinalIgnoreCase)
                  || result.Message.Contains("especial", StringComparison.OrdinalIgnoreCase)),
            "Uma entrada de link simbólico deveria ser rejeitada.");
    }

    private static async Task VerifyCancellationDuringCopyAsync(string root)
    {
        var archivePath = Path.Combine(root, "copy-controls.zip");
        CreateRepeatedByteZip(archivePath, "large.bin", 16 * 1024 * 1024, 0x41);
        var destinationBase = CreateDestinationBase(root, "cancel-copy-base");
        using var cancellation = new CancellationTokenSource();
        var observedCopy = false;
        var progress = new InlineProgress(update =>
        {
            if (update.ExtractedBytes <= 0 || observedCopy) return;
            observedCopy = true;
            cancellation.Cancel();
        });
        var extractor = CreateLargeTestExtractor(TimeSpan.FromMinutes(1));

        var result = await extractor.ExtractAsync(
            archivePath,
            destinationBase,
            "Testes",
            "Cancelamento",
            progress,
            itemId: "cancel-during-copy",
            cancellationToken: cancellation.Token);

        Check(observedCopy && result.Status == CatalogArchiveExtractionStatus.Canceled,
            "O cancelamento disparado durante uma única entrada deveria interromper a cópia.");
        Check(!Directory.Exists(result.DestinationPath) && File.Exists(archivePath),
            "O cancelamento durante a cópia não pode publicar pasta parcial nem apagar o pacote.");
    }

    private static async Task VerifyMonotonicTimeoutDuringCopyAsync(string root)
    {
        var archivePath = Path.Combine(root, "copy-controls.zip");
        var destinationBase = CreateDestinationBase(root, "timeout-copy-base");
        var observedCopy = false;
        var progress = new InlineProgress(update =>
        {
            if (update.ExtractedBytes <= 0 || observedCopy) return;
            observedCopy = true;
            Thread.Sleep(TimeSpan.FromMilliseconds(1_200));
        });
        var extractor = CreateLargeTestExtractor(TimeSpan.FromSeconds(1));

        var result = await extractor.ExtractAsync(
            archivePath,
            destinationBase,
            "Testes",
            "Timeout",
            progress,
            itemId: "timeout-during-copy");

        Check(observedCopy
              && result.Status == CatalogArchiveExtractionStatus.Canceled
              && result.Message.Contains("tempo máximo", StringComparison.OrdinalIgnoreCase),
            "O prazo monotônico deveria interromper a cópia dentro de uma única entrada.");
        Check(!Directory.Exists(result.DestinationPath) && File.Exists(archivePath),
            "O timeout não pode publicar uma pasta parcial nem apagar o pacote.");
    }

    private static async Task VerifyArchiveCannotBeSwappedAsync(string root)
    {
        var archivePath = Path.Combine(root, "source-identity.zip");
        var replacementPath = Path.Combine(root, "source-replacement.zip");
        CreateRepeatedByteZip(archivePath, "payload.bin", 2 * 1024 * 1024, 0x31);
        CreateZip(replacementPath, "payload.bin", "replacement");
        var destinationBase = CreateDestinationBase(root, "source-identity-base");
        var replacementDenied = false;
        var replacementSucceeded = false;
        var attempted = false;
        var progress = new InlineProgress(update =>
        {
            if (update.ExtractedBytes <= 0 || attempted) return;
            attempted = true;
            try
            {
                File.Move(replacementPath, archivePath, overwrite: true);
                replacementSucceeded = true;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                replacementDenied = true;
            }
        });

        var result = await CreateLargeTestExtractor(TimeSpan.FromMinutes(1)).ExtractAsync(
            archivePath,
            destinationBase,
            "Testes",
            "Identidade da origem",
            progress,
            itemId: "source-identity");

        Check(attempted, "O teste de troca da origem não alcançou a fase de cópia.");
        Check(replacementDenied || (replacementSucceeded && result.Status == CatalogArchiveExtractionStatus.Failed),
            "Trocar o caminho da origem não pode redirecionar a leitura após o planejamento.");
        if (result.Succeeded)
        {
            Check(new FileInfo(Path.Combine(result.DestinationPath, "payload.bin")).Length == 2 * 1024 * 1024,
                "A extração leu o arquivo substituto em vez do handle planejado.");
        }
    }

    private static async Task VerifyAuthorizedHashMismatchIsBlockedBeforeWriteAsync(string root)
    {
        var archivePath = Path.Combine(root, "authorized-original.zip");
        var replacementPath = Path.Combine(root, "authorized-replacement.zip");
        CreateZip(archivePath, "payload.bin", "authorized payload");
        var authorizedArtifact = CatalogArchiveExtractorTestExtensions.CreateAuthorizedArtifact(
            archivePath);
        var tamperedArchive = File.ReadAllBytes(archivePath);
        tamperedArchive[tamperedArchive.Length / 2] ^= 0x01;
        File.WriteAllBytes(replacementPath, tamperedArchive);
        File.Move(replacementPath, archivePath, overwrite: true);
        var destinationBase = CreateDestinationBase(root, "authorized-swap-base");

        var result = await new CatalogArchiveExtractor().ExtractAsync(
            archivePath,
            destinationBase,
            "Testes",
            "Troca antes da abertura",
            authorizedArtifact,
            itemId: "authorized-swap");

        Check(result.Status == CatalogArchiveExtractionStatus.Failed
              && result.Message.Contains("SHA-256", StringComparison.OrdinalIgnoreCase),
            $"Uma troca depois da autorização e antes da extração deveria falhar pelo hash. Resultado: {result.Status} / {result.Message}");
        Check(!Directory.Exists(result.DestinationPath),
            "Bytes não autorizados não podem ser publicados antes da conferência no mesmo handle.");
    }

    private static async Task VerifyExtractPolicyNoneIsRejectedAsync(string root)
    {
        var archivePath = Path.Combine(root, "policy-none.zip");
        CreateZip(archivePath, "payload.bin", "policy");
        var artifact = CatalogArchiveExtractorTestExtensions.CreateAuthorizedArtifact(
            archivePath,
            CatalogExtractPolicy.None);
        var destinationBase = CreateDestinationBase(root, "policy-none-base");

        var result = await new CatalogArchiveExtractor().ExtractAsync(
            archivePath,
            destinationBase,
            "Testes",
            "Política none",
            artifact,
            itemId: "policy-none");

        Check(result.Status == CatalogArchiveExtractionStatus.Failed
              && result.Message.Contains("política", StringComparison.OrdinalIgnoreCase),
            "O extrator deve rejeitar um artefato cuja política autorizada seja None.");
        Check(!Directory.Exists(result.DestinationPath),
            "A política None não pode criar nem publicar a pasta final.");
    }

    private static async Task VerifyForgedRecoveryMarkerIsRejectedAsync(string root)
    {
        var archivePath = Path.Combine(root, "forged-marker.zip");
        CreateZip(archivePath, "payload.bin", "marker payload");
        var destinationBase = CreateDestinationBase(root, "forged-marker-base");
        var extractor = new CatalogArchiveExtractor();
        var first = await extractor.ExtractAsync(
            archivePath,
            destinationBase,
            "Testes",
            "Marker",
            itemId: "forged-marker");
        Check(first.Succeeded, first.Message);

        var markerPath = Path.Combine(
            first.DestinationPath,
            CatalogArchiveExtractor.CompletionMarkerFileName);
        var payloadPath = Path.Combine(first.DestinationPath, "payload.bin");
        var originalMarkerJson = File.ReadAllText(markerPath);
        File.WriteAllText(payloadPath, "conteúdo adulterado", new UTF8Encoding(false));
        var forgedMarker = JsonNode.Parse(originalMarkerJson)?.AsObject()
                           ?? throw new InvalidDataException("Marker de teste inválido.");
        var forgedInventory = forgedMarker["Inventory"]?.AsArray()
                              ?? throw new InvalidDataException("Inventário de teste inválido.");
        var forgedEntry = forgedInventory
            .Select(node => node?.AsObject())
            .Single(entry => string.Equals(
                entry?["RelativePath"]?.GetValue<string>(),
                "payload.bin",
                StringComparison.Ordinal));
        var forgedPayloadBytes = File.ReadAllBytes(payloadPath);
        try
        {
            forgedEntry!["Length"] = forgedPayloadBytes.LongLength;
            forgedEntry["Sha256"] = Convert.ToHexString(SHA256.HashData(forgedPayloadBytes))
                .ToLowerInvariant();
        }
        finally
        {
            CryptographicOperations.ZeroMemory(forgedPayloadBytes);
        }
        File.SetAttributes(markerPath, FileAttributes.Normal);
        File.WriteAllText(markerPath, forgedMarker.ToJsonString(), new UTF8Encoding(false));

        var forgedRecovery = await extractor.ExtractAsync(
            archivePath,
            destinationBase,
            "Testes",
            "Marker",
            itemId: "forged-marker");
        Check(forgedRecovery.Status == CatalogArchiveExtractionStatus.Failed,
            "Payload e marker recalculados pelo atacante não podem forjar uma recuperação.");

        File.WriteAllText(payloadPath, "marker payload", new UTF8Encoding(false));
        var marker = JsonNode.Parse(originalMarkerJson)?.AsObject()
                     ?? throw new InvalidDataException("Marker de teste inválido.");
        marker["SchemaVersion"] = 1;
        _ = marker.Remove("Inventory");
        File.SetAttributes(markerPath, FileAttributes.Normal);
        File.WriteAllText(markerPath, marker.ToJsonString());

        var recovered = await extractor.ExtractAsync(
            archivePath,
            destinationBase,
            "Testes",
            "Marker",
            itemId: "forged-marker");

        Check(recovered.Status == CatalogArchiveExtractionStatus.Failed,
            "Um marker antigo ou sem inventário não pode autorizar a recuperação.");
    }

    private static async Task VerifyLongPathSegmentsAreBlockedAsync(string root)
    {
        var archivePath = Path.Combine(root, "long-segment.zip");
        CreateZip(archivePath, new string('a', 181) + ".bin", "path");
        var destinationBase = CreateDestinationBase(root, "long-segment-base");

        var result = await new CatalogArchiveExtractor().ExtractAsync(
            archivePath,
            destinationBase,
            "Testes",
            "Segmento longo",
            itemId: "long-segment");

        Check(result.Status == CatalogArchiveExtractionStatus.Failed
              && result.Message.Contains("segmento", StringComparison.OrdinalIgnoreCase),
            "Um segmento além do limite configurado deveria ser bloqueado.");
    }

    private static CatalogArchiveExtractor CreateLargeTestExtractor(TimeSpan maximumDuration)
        => new(new CatalogArchiveExtractionOptions
        {
            MinimumFreeSpaceReserveBytes = 0,
            MaximumTotalUncompressedBytes = 32L * 1024L * 1024L,
            MaximumEntryUncompressedBytes = 32L * 1024L * 1024L,
            MaximumCompressionRatio = 1_000_000,
            MaximumExtractionDuration = maximumDuration,
            CopyBufferSize = 4 * 1024
        });

    private static string CreateDestinationBase(string root, string name)
    {
        var path = Path.Combine(root, name);
        Directory.CreateDirectory(path);
        return path;
    }

    private static void CreateZip(string path, string entryName, string content)
    {
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        var entry = archive.CreateEntry(entryName, CompressionLevel.SmallestSize);
        using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
        writer.Write(content);
    }

    private static void CreateZipWithEntries(
        string path,
        params (string EntryName, string Content)[] entries)
    {
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var (entryName, content) in entries)
        {
            var entry = archive.CreateEntry(entryName, CompressionLevel.SmallestSize);
            using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
            writer.Write(content);
        }
    }

    private static void CreateRepeatedByteZip(
        string path,
        string entryName,
        int byteCount,
        byte value)
    {
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        var entry = archive.CreateEntry(entryName, CompressionLevel.SmallestSize);
        using var output = entry.Open();
        var buffer = new byte[64 * 1024];
        Array.Fill(buffer, value);
        var remaining = byteCount;
        while (remaining > 0)
        {
            var count = Math.Min(buffer.Length, remaining);
            output.Write(buffer, 0, count);
            remaining -= count;
        }
    }

    private static void ZeroZipCompressedSizes(string path)
    {
        var bytes = File.ReadAllBytes(path);
        var endOfCentralDirectory = -1;
        for (var index = bytes.Length - 22; index >= 0; index--)
        {
            if (BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(index, 4)) != 0x06054B50u)
                continue;
            endOfCentralDirectory = index;
            break;
        }

        Check(endOfCentralDirectory >= 0, "O ZIP de teste não contém o diretório central.");
        var centralDirectory = BinaryPrimitives.ReadInt32LittleEndian(
            bytes.AsSpan(endOfCentralDirectory + 16, 4));
        Check(centralDirectory >= 0
              && BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(centralDirectory, 4)) == 0x02014B50u,
            "O diretório central do ZIP de teste é inválido.");
        var localHeader = BinaryPrimitives.ReadInt32LittleEndian(
            bytes.AsSpan(centralDirectory + 42, 4));
        Check(localHeader >= 0
              && BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(localHeader, 4)) == 0x04034B50u,
            "O cabeçalho local do ZIP de teste é inválido.");

        bytes.AsSpan(centralDirectory + 20, 4).Clear();
        bytes.AsSpan(localHeader + 18, 4).Clear();
        File.WriteAllBytes(path, bytes);
    }

    private static void CreateSolidSevenZip(string path)
    {
        const string solidArchiveBase64 =
            "N3q8ryccAAS1yA3bwwAAAAAAAAAgAAAAAAAAAO+KU3fgANkAXV0APYKAFxwxC6j2kM6olzghAjROdJSvQqC2Q8g5A0DivNoqUOAS4yJ07e4RVQK+jxCItnm1rysP3tt1PB4itC1URUxyRLmHycL8PmFGS4xBnN73oFSfhzM3gc3kojoAAAAAgTMHrg/T0Gg9QMCQ0v99aU2PG7VqZrC3vNbyl8G68XVyBNYCN2LMkHVayjwd6T3U8ZZ8jeilx4FVbMg29YsK32WU3C7Wx/trdWXeOETOIlqJiBBTNLzUE1wI4wAXBmUBCV4ABwsBAAEjAwEBBV0AEAAADHYKAfqERJEAAA==";
        File.WriteAllBytes(path, Convert.FromBase64String(solidArchiveBase64));
    }

    private sealed class InlineProgress(Action<CatalogArchiveExtractionProgress> report)
        : IProgress<CatalogArchiveExtractionProgress>
    {
        public void Report(CatalogArchiveExtractionProgress value) => report(value);
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}

internal static class CatalogArchiveExtractorTestExtensions
{
    public static Task<CatalogArchiveExtractionResult> ExtractAsync(
        this CatalogArchiveExtractor extractor,
        string archivePath,
        string baseDirectory,
        string category,
        string item,
        IProgress<CatalogArchiveExtractionProgress>? progress = null,
        bool baseDirectoryIsGameLibrary = false,
        string? itemId = null,
        CancellationToken cancellationToken = default)
        => extractor.ExtractAsync(
            archivePath,
            baseDirectory,
            category,
            item,
            CreateAuthorizedArtifact(archivePath),
            progress,
            baseDirectoryIsGameLibrary,
            itemId,
            cancellationToken);

    internal static CatalogArtifactDescriptor CreateAuthorizedArtifact(
        string archivePath,
        CatalogExtractPolicy extractPolicy = CatalogExtractPolicy.ExtractArchive)
    {
        var bytes = File.ReadAllBytes(archivePath);
        try
        {
            var sha256 = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
            var extension = Path.GetExtension(archivePath).ToLowerInvariant();
            return new CatalogArtifactDescriptor
            {
                ArtifactId = sha256[..32],
                ArtifactVersion = 1,
                ContentLength = bytes.LongLength,
                Sha256 = sha256,
                SafeFileName = Path.GetFileName(archivePath),
                FileExtension = extension,
                ExtractPolicy = extractPolicy,
                ManifestIdentity = Convert.ToHexString(
                        SHA256.HashData(Encoding.UTF8.GetBytes("manifest:" + sha256)))
                    .ToLowerInvariant()
            };
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }
}
