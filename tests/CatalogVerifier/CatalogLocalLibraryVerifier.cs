using System.Security.Cryptography;
using System.Text.Json;
using TurboBoxManager.Catalog;

namespace TurboBoxManager.CatalogVerifier;

internal static class CatalogLocalLibraryVerifier
{
    internal static async Task RunAsync(string scenarioRoot)
    {
        var libraryRoot = Path.Combine(
            scenarioRoot,
            CatalogArchiveExtractor.GameLibraryFolderName);
        Directory.CreateDirectory(libraryRoot);
        using var downloadService = new CatalogDownloadService();
        var service = new CatalogLocalLibraryService(downloadService);

        await VerifyDirectFileStatesAsync(service, libraryRoot);
        await VerifyDirectMigrationAndAuthorizationWithdrawalAsync(service, libraryRoot);
        await VerifyDirectRelocationSurvivesRestartAsync(
            downloadService,
            scenarioRoot);
        await VerifyExtractedDirectoryStatesAsync(service, libraryRoot);
        await VerifyPhysicalSystemInventoryAsync(service, libraryRoot, scenarioRoot);
        await VerifyCancellationAsync(service, libraryRoot);
    }

    private static async Task VerifyDirectFileStatesAsync(
        CatalogLocalLibraryService service,
        string libraryRoot)
    {
        var payload = "turborama-local-game"u8.ToArray();
        var item = CreateItem("local-direct-game", payload, CatalogExtractPolicy.None, ".rom");
        var missing = (await service.InspectAsync(libraryRoot, [item], CancellationToken.None))[0];
        Assert(missing.Status == CatalogLocalGameStatus.NotDownloaded,
            "Um jogo local ausente precisa aparecer como não baixado.");

        var expectedPath = service.BuildExpectedPath(libraryRoot, item);
        Directory.CreateDirectory(Path.GetDirectoryName(expectedPath)!);
        await File.WriteAllBytesAsync(expectedPath, payload);
        var downloaded = (await service.InspectAsync(libraryRoot, [item], CancellationToken.None))[0];
        Assert(downloaded.Status == CatalogLocalGameStatus.Downloaded,
            "Um arquivo final com tamanho autorizado precisa aparecer como baixado.");

        await File.WriteAllBytesAsync(expectedPath, payload[..^1]);
        var incomplete = (await service.InspectAsync(libraryRoot, [item], CancellationToken.None))[0];
        Assert(incomplete.Status == CatalogLocalGameStatus.Incomplete,
            "Um arquivo truncado precisa aparecer como incompleto.");

        var deleted = await service.DeleteAsync(libraryRoot, item, CancellationToken.None);
        Assert(deleted && !File.Exists(expectedPath),
            "A exclusão validada não removeu o arquivo local esperado.");
    }

    private static async Task VerifyDirectMigrationAndAuthorizationWithdrawalAsync(
        CatalogLocalLibraryService service,
        string libraryRoot)
    {
        var payload = "legacy-direct-download-with-current-proof"u8.ToArray();
        var authorized = CreateItem(
            "direct-migration-withdrawal",
            payload,
            CatalogExtractPolicy.None,
            ".rom",
            category: "Windows");
        var expectedPath = service.BuildExpectedPath(libraryRoot, authorized);
        Directory.CreateDirectory(Path.GetDirectoryName(expectedPath)!);
        await File.WriteAllBytesAsync(expectedPath, payload);
        var statePath = expectedPath + ".part.resume.json";
        var attestationPath = expectedPath + ".part.local-attestation.dpapi";
        Assert(!File.Exists(statePath),
            "O cenário de migração precisa começar sem sidecar legado.");

        var migrated = (await service.InspectAsync(
            libraryRoot,
            [authorized],
            CancellationToken.None))[0];
        Assert(migrated.Status == CatalogLocalGameStatus.Downloaded
               && migrated.ExpectedPath == expectedPath
               && File.Exists(statePath)
               && File.Exists(attestationPath),
            "O arquivo legado autorizado não recebeu estado e atestação local protegida.");

        var withdrawn = new CatalogItem
        {
            Id = authorized.Id,
            CategoryId = authorized.CategoryId,
            Category = authorized.Category,
            Title = authorized.Title,
            Extract = false
        };

        var protectedAttestation = await File.ReadAllBytesAsync(attestationPath);
        File.Delete(attestationPath);
        var legacySidecarOnly = (await service.InspectAsync(
            libraryRoot,
            [withdrawn],
            CancellationToken.None))[0];
        Assert(legacySidecarOnly.Status == CatalogLocalGameStatus.Unavailable,
            "Um sidecar legado sem atestação DPAPI não pode associar conteúdo após retirada do artefato.");
        await File.WriteAllBytesAsync(attestationPath, protectedAttestation);
        CryptographicOperations.ZeroMemory(protectedAttestation);

        var afterWithdrawal = (await service.InspectAsync(
            libraryRoot,
            [withdrawn],
            CancellationToken.None))[0];
        Assert(afterWithdrawal.Status == CatalogLocalGameStatus.Downloaded
               && afterWithdrawal.ExpectedPath == expectedPath,
            "Retirar o artefato do servidor não pode ocultar um download direto previamente comprovado.");

        var sentinel = Path.Combine(libraryRoot, "direct-withdrawal-sentinel.txt");
        await File.WriteAllTextAsync(sentinel, "preserve");
        var deleted = await service.DeleteAsync(
            libraryRoot,
            withdrawn,
            CancellationToken.None);
        Assert(deleted
               && !File.Exists(expectedPath)
               && !File.Exists(statePath)
               && !File.Exists(attestationPath),
            "O download direto sem artefato atual não foi excluído com seu estado protegido confinado.");
        Assert(File.Exists(sentinel),
            "A exclusão do download direto alcançou um sentinela fora do artefato.");
    }

    private static async Task VerifyDirectRelocationSurvivesRestartAsync(
        CatalogDownloadService downloadService,
        string scenarioRoot)
    {
        var sourceRoot = Path.Combine(scenarioRoot, "relocation-source");
        var destinationRoot = Path.Combine(
            scenarioRoot,
            "relocation-destination",
            CatalogArchiveExtractor.GameLibraryFolderName);
        Directory.CreateDirectory(sourceRoot);
        Directory.CreateDirectory(destinationRoot);
        var payload = "direct-relocation-restart-proof"u8.ToArray();
        var authorized = CreateItem(
            "direct-relocation-restart",
            payload,
            CatalogExtractPolicy.None,
            ".rom",
            category: "Windows");
        var sourcePath = downloadService.BuildSafeDestinationPath(sourceRoot, authorized);
        var destinationPath = downloadService.BuildSafeDestinationPath(
            destinationRoot,
            authorized);
        Directory.CreateDirectory(Path.GetDirectoryName(sourcePath)!);
        Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
        await File.WriteAllBytesAsync(sourcePath, payload);

        var sourceLibrary = new CatalogLocalLibraryService(downloadService);
        var sourceInspection = (await sourceLibrary.InspectAsync(
            sourceRoot,
            [authorized],
            CancellationToken.None))[0];
        Assert(sourceInspection.Status == CatalogLocalGameStatus.Downloaded,
            "O cenário de relocação não conseguiu autenticar a origem.");

        var relocation = downloadService.PrepareCompletedDirectDownloadRelocation(
            sourceRoot,
            destinationRoot,
            authorized,
            sourcePath,
            destinationPath,
            CancellationToken.None);
        Assert(File.Exists(relocation.DestinationStateFilePath)
               && File.Exists(relocation.DestinationAttestationFilePath)
               && !File.Exists(destinationPath),
            "O destino precisa receber estado e atestação antes da remoção da origem.");

        await StoreWindow.MoveFilePreservingSourceOnFailureAsync(
            sourcePath,
            destinationPath,
            authorized.Artifact!,
            CancellationToken.None);
        Assert(!File.Exists(sourcePath)
               && File.Exists(destinationPath)
               && File.Exists(relocation.SourceStateFilePath),
            "A movimentação deve publicar o destino antes de limpar o estado antigo.");

        var withdrawn = new CatalogItem
        {
            Id = authorized.Id,
            CategoryId = authorized.CategoryId,
            Category = authorized.Category,
            Title = authorized.Title
        };
        using (var restartedDownloadService = new CatalogDownloadService())
        {
            var restartedLibrary = new CatalogLocalLibraryService(restartedDownloadService);
            var afterRestart = (await restartedLibrary.InspectAsync(
                destinationRoot,
                [withdrawn],
                CancellationToken.None))[0];
            Assert(afterRestart.Status == CatalogLocalGameStatus.Downloaded
                   && afterRestart.ExpectedPath == destinationPath,
                $"Após reinício e retirada do artefato, a atestação preparada no destino não restaurou a associação: {afterRestart.Status} / {afterRestart.ExpectedPath} / {afterRestart.Detail}");
        }

        var cleaned = downloadService.CompleteCompletedDirectDownloadRelocation(
            relocation,
            CancellationToken.None);
        Assert(cleaned
               && !File.Exists(relocation.SourceStateFilePath)
               && !File.Exists(relocation.SourceAttestationFilePath),
            "A conclusão da relocação não removeu somente o estado antigo depois de validar o destino.");
    }

    private static async Task VerifyExtractedDirectoryStatesAsync(
        CatalogLocalLibraryService service,
        string libraryRoot)
    {
        var archiveBytes = "authenticated-archive-placeholder"u8.ToArray();
        var item = CreateItem(
            "local-extracted-game",
            archiveBytes,
            CatalogExtractPolicy.ExtractArchive,
            ".zip",
            category: "PlayStation 2");
        var destination = service.BuildExpectedPath(libraryRoot, item);
        Directory.CreateDirectory(destination);
        var installedPayload = "installed-rom-payload"u8.ToArray();
        var installedFile = Path.Combine(destination, "rom.bin");
        await File.WriteAllBytesAsync(installedFile, installedPayload);

        var artifact = item.Artifact!;
        object marker = new
        {
            SchemaVersion = 2,
            ArchiveLength = artifact.ContentLength,
            ArchiveSha256 = artifact.Sha256,
            ManifestIdentity = artifact.ManifestIdentity,
            ArtifactId = artifact.ArtifactId,
            ArtifactVersion = artifact.ArtifactVersion,
            Category = item.Category,
            Item = item.Title,
            StableItemId = item.Id,
            Inventory = new[]
            {
                new
                {
                    RelativePath = "rom.bin",
                    Length = installedPayload.LongLength,
                    Sha256 = Convert.ToHexString(SHA256.HashData(installedPayload)).ToLowerInvariant()
                }
            }
        };
        await File.WriteAllBytesAsync(
            Path.Combine(destination, CatalogArchiveExtractor.CompletionMarkerFileName),
            JsonSerializer.SerializeToUtf8Bytes(marker));

        var downloaded = (await service.InspectAsync(libraryRoot, [item], CancellationToken.None))[0];
        Assert(downloaded.Status == CatalogLocalGameStatus.Downloaded,
            "Uma extração publicada com inventário válido precisa aparecer como baixada.");

        var sameLengthTamperedPayload = installedPayload.ToArray();
        sameLengthTamperedPayload[0] ^= 0x5A;
        await File.WriteAllBytesAsync(installedFile, sameLengthTamperedPayload);
        var sameLengthTampering = (await service.InspectAsync(
            libraryRoot,
            [item],
            CancellationToken.None))[0];
        Assert(sameLengthTampering.Status == CatalogLocalGameStatus.Incomplete,
            "Uma adulteração com o mesmo tamanho precisa falhar pela divergência de SHA-256.");

        await File.WriteAllBytesAsync(installedFile, installedPayload);
        var restored = (await service.InspectAsync(
            libraryRoot,
            [item],
            CancellationToken.None))[0];
        Assert(restored.Status == CatalogLocalGameStatus.Downloaded,
            "Restaurar os bytes inventariados precisa restabelecer a instalação concluída.");

        await File.WriteAllTextAsync(
            Path.Combine(destination, CatalogArchiveExtractor.CompletionMarkerFileName),
            "null");
        var nullMarker = (await service.InspectAsync(
            libraryRoot,
            [item],
            CancellationToken.None))[0];
        Assert(nullMarker.Status == CatalogLocalGameStatus.Incomplete,
            "Um comprovante JSON nulo precisa ser tratado como instalação incompleta, sem NRE.");

        await File.WriteAllTextAsync(
            Path.Combine(destination, CatalogArchiveExtractor.CompletionMarkerFileName),
            "{");
        var malformedMarker = (await service.InspectAsync(
            libraryRoot,
            [item],
            CancellationToken.None))[0];
        Assert(malformedMarker.Status == CatalogLocalGameStatus.Incomplete,
            "Um comprovante JSON malformado precisa ser tratado como instalação incompleta.");

        marker = new
        {
            SchemaVersion = 2,
            ArchiveLength = artifact.ContentLength,
            ArchiveSha256 = (string?)null,
            ManifestIdentity = (string?)null,
            ArtifactId = (string?)null,
            ArtifactVersion = artifact.ArtifactVersion,
            Category = (string?)null,
            Item = (string?)null,
            StableItemId = (string?)null,
            Inventory = new object?[] { null }
        };
        await File.WriteAllBytesAsync(
            Path.Combine(destination, CatalogArchiveExtractor.CompletionMarkerFileName),
            JsonSerializer.SerializeToUtf8Bytes(marker));
        var nullFields = (await service.InspectAsync(
            libraryRoot,
            [item],
            CancellationToken.None))[0];
        Assert(nullFields.Status == CatalogLocalGameStatus.Incomplete,
            "Campos nulos no comprovante precisam falhar fechados, sem NullReferenceException.");

        var outsideSentinel = Path.Combine(libraryRoot, "outside-sentinel.bin");
        await File.WriteAllTextAsync(outsideSentinel, "preserve");
        marker = new
        {
            SchemaVersion = 2,
            ArchiveLength = artifact.ContentLength,
            ArchiveSha256 = artifact.Sha256,
            ManifestIdentity = artifact.ManifestIdentity,
            ArtifactId = artifact.ArtifactId,
            ArtifactVersion = artifact.ArtifactVersion,
            Category = item.Category,
            Item = item.Title,
            StableItemId = item.Id,
            Inventory = new[]
            {
                new
                {
                    RelativePath = "../outside-sentinel.bin",
                    Length = 8L,
                    Sha256 = new string('0', 64)
                }
            }
        };
        await File.WriteAllBytesAsync(
            Path.Combine(destination, CatalogArchiveExtractor.CompletionMarkerFileName),
            JsonSerializer.SerializeToUtf8Bytes(marker));
        var traversal = (await service.InspectAsync(libraryRoot, [item], CancellationToken.None))[0];
        Assert(traversal.Status == CatalogLocalGameStatus.Incomplete,
            "Um inventário com travessia precisa ser recusado.");

        var deleted = await service.DeleteAsync(libraryRoot, item, CancellationToken.None);
        Assert(deleted && !Directory.Exists(destination),
            "A limpeza da instalação incompleta não removeu somente o destino esperado.");
        Assert(File.Exists(outsideSentinel),
            "A exclusão seguiu um caminho externo presente no inventário adulterado.");
    }

    private static async Task VerifyPhysicalSystemInventoryAsync(
        CatalogLocalLibraryService service,
        string libraryRoot,
        string scenarioRoot)
    {
        const string category = "Nintendo 64";
        var unavailable = new CatalogItem
        {
            Id = "installed-without-current-artifact",
            CategoryId = "nintendo-64",
            Category = category,
            Title = "Instalação antiga sem artefato atual",
            Extract = true
        };
        var missingUnavailable = new CatalogItem
        {
            Id = "missing-without-current-artifact",
            CategoryId = "nintendo-64",
            Category = category,
            Title = "Ausente sem artefato atual",
            Extract = true
        };
        var knownPath = CatalogArchiveExtractor.BuildGameDestinationPath(
            libraryRoot,
            category,
            unavailable.Id);
        Directory.CreateDirectory(knownPath);
        await File.WriteAllTextAsync(Path.Combine(knownPath, "legacy.rom"), "legacy");

        var categoryPath = CatalogArchiveExtractor.BuildCategoryDestinationPath(
            libraryRoot,
            category);
        var orphanDirectory = Path.Combine(categoryPath, "pasta-sem-catalogo");
        Directory.CreateDirectory(orphanDirectory);
        await File.WriteAllTextAsync(Path.Combine(orphanDirectory, "unknown.rom"), "unknown");
        var orphanFile = Path.Combine(categoryPath, "arquivo-solto.rom");
        await File.WriteAllTextAsync(orphanFile, "unknown-file");
        var outsideSentinel = Path.Combine(scenarioRoot, "physical-inventory-sentinel.txt");
        await File.WriteAllTextAsync(outsideSentinel, "preserve");

        var inventory = await service.InspectSystemAsync(
            libraryRoot,
            category,
            [unavailable, missingUnavailable],
            CancellationToken.None);
        Assert(inventory.CategoryPath == categoryPath,
            "O inventário não ficou confinado à pasta exata da categoria.");
        Assert(inventory.CatalogItems.Count == 2,
            "O inventário físico precisa preservar todos os itens do catálogo.");
        Assert(inventory.CatalogItems[0].Status == CatalogLocalGameStatus.Incomplete,
            "Uma pasta de item sem artefato atual precisa ser reconhecida como conteúdo local revisável.");
        Assert(inventory.CatalogItems[0].ExpectedPath == knownPath,
            "O item sem artefato perdeu seu caminho físico estável.");
        Assert(inventory.CatalogItems[1].Status == CatalogLocalGameStatus.Unavailable,
            "Um item sem artefato e sem conteúdo físico precisa continuar indisponível.");
        Assert(inventory.Orphans.Count == 2,
            "Arquivos e diretórios não reconhecidos da categoria precisam aparecer como órfãos.");
        Assert(inventory.Orphans.All(orphan =>
                orphan.Status == CatalogLocalGameStatus.Unrecognized && orphan.CanDelete),
            "Órfãos físicos comuns precisam ser explicitamente identificados e removíveis.");
        Assert(inventory.Orphans.All(orphan => orphan.LocalPath != knownPath),
            "A instalação estável de item sem artefato foi classificada incorretamente como órfã.");

        var directoryEntry = inventory.Orphans.Single(orphan => orphan.IsDirectory);
        var deletedDirectory = await CatalogLocalLibraryService.DeleteOrphanAsync(
            libraryRoot,
            category,
            directoryEntry,
            [unavailable, missingUnavailable],
            CancellationToken.None);
        Assert(deletedDirectory && !Directory.Exists(orphanDirectory),
            "A exclusão do diretório órfão validado não foi concluída.");
        Assert(Directory.Exists(knownPath) && File.Exists(outsideSentinel),
            "A exclusão do órfão saiu da pasta-alvo ou removeu uma instalação reconhecida.");

        var forgedOutside = new CatalogLocalOrphanInspection(
            Path.GetFileName(outsideSentinel),
            outsideSentinel,
            false,
            CatalogLocalGameStatus.Unrecognized,
            "forjado");
        await AssertThrowsAsync<InvalidDataException>(() => CatalogLocalLibraryService.DeleteOrphanAsync(
            libraryRoot,
            category,
            forgedOutside,
            [unavailable, missingUnavailable],
            CancellationToken.None));
        Assert(File.Exists(outsideSentinel),
            "Um caminho forjado fora da categoria alcançou o sentinela externo.");

        var forgedKnown = new CatalogLocalOrphanInspection(
            Path.GetFileName(knownPath),
            knownPath,
            true,
            CatalogLocalGameStatus.Unrecognized,
            "stale");
        await AssertThrowsAsync<InvalidDataException>(() => CatalogLocalLibraryService.DeleteOrphanAsync(
            libraryRoot,
            category,
            forgedKnown,
            [unavailable, missingUnavailable],
            CancellationToken.None));
        Assert(Directory.Exists(knownPath),
            "Uma entrada órfã obsoleta removeu um item atualmente reconhecido pelo catálogo.");

        var fileEntry = inventory.Orphans.Single(orphan => !orphan.IsDirectory);
        var deletedFile = await CatalogLocalLibraryService.DeleteOrphanAsync(
            libraryRoot,
            category,
            fileEntry,
            [unavailable, missingUnavailable],
            CancellationToken.None);
        Assert(deletedFile && !File.Exists(orphanFile),
            "A exclusão do arquivo órfão validado não foi concluída.");

        var deletedUnavailable = await service.DeleteAsync(
            libraryRoot,
            unavailable,
            CancellationToken.None);
        Assert(deletedUnavailable && !Directory.Exists(knownPath),
            "A instalação antiga sem artefato atual não pôde ser limpa pelo caminho estável.");
        Assert(File.Exists(outsideSentinel),
            "A limpeza de item sem artefato saiu do diretório estável autorizado.");

        await VerifyReparsePointIsNotFollowedAsync(
            service,
            libraryRoot,
            category,
            categoryPath,
            [unavailable, missingUnavailable]);
    }

    private static async Task VerifyReparsePointIsNotFollowedAsync(
        CatalogLocalLibraryService service,
        string libraryRoot,
        string category,
        string categoryPath,
        IReadOnlyList<CatalogItem> items)
    {
        var sentinelDirectory = Path.Combine(
            Path.GetTempPath(),
            "TurboramaLocalLibraryReparse-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(sentinelDirectory);
        var outsideSentinel = Path.Combine(sentinelDirectory, "outside-sentinel.txt");
        await File.WriteAllTextAsync(outsideSentinel, "preserve");
        var linkPath = Path.Combine(categoryPath, "atalho-nao-autorizado");
        var orphanTree = Path.Combine(categoryPath, "pasta-orfa-com-atalho");
        var nestedLinkPath = Path.Combine(orphanTree, "atalho-interno");
        try
        {
            Directory.CreateSymbolicLink(linkPath, sentinelDirectory);
            Directory.CreateDirectory(orphanTree);
            Directory.CreateSymbolicLink(nestedLinkPath, sentinelDirectory);
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException
                                           or IOException
                                           or PlatformNotSupportedException)
        {
            if (Directory.Exists(linkPath)) Directory.Delete(linkPath);
            if (Directory.Exists(nestedLinkPath)) Directory.Delete(nestedLinkPath);
            if (Directory.Exists(orphanTree)) Directory.Delete(orphanTree);
            Directory.Delete(sentinelDirectory, recursive: true);
            return;
        }
        try
        {
            var inventory = await service.InspectSystemAsync(
                libraryRoot,
                category,
                items,
                CancellationToken.None);
            var link = inventory.Orphans.Single(orphan => orphan.LocalPath == linkPath);
            Assert(link.Status == CatalogLocalGameStatus.Unsafe && !link.CanDelete,
                "Um reparse point precisa aparecer como inseguro e nunca como órfão removível.");
            await AssertThrowsAsync<InvalidDataException>(() => CatalogLocalLibraryService.DeleteOrphanAsync(
                libraryRoot,
                category,
                link,
                items,
                CancellationToken.None));
            Assert(File.Exists(outsideSentinel),
                "A análise ou exclusão seguiu o reparse point até o sentinela externo.");

            var tree = inventory.Orphans.Single(orphan => orphan.LocalPath == orphanTree);
            Assert(tree.Status == CatalogLocalGameStatus.Unrecognized && tree.CanDelete,
                "A pasta órfã comum precisa continuar visível antes da pré-validação da limpeza.");
            await AssertThrowsAsync<InvalidDataException>(() => CatalogLocalLibraryService.DeleteOrphanAsync(
                libraryRoot,
                category,
                tree,
                items,
                CancellationToken.None));
            Assert(Directory.Exists(orphanTree) && File.Exists(outsideSentinel),
                "A limpeza não foi interrompida antes de atravessar um reparse point interno.");
        }
        finally
        {
            if (Directory.Exists(linkPath)) Directory.Delete(linkPath);
            if (Directory.Exists(nestedLinkPath)) Directory.Delete(nestedLinkPath);
            if (Directory.Exists(orphanTree)) Directory.Delete(orphanTree);
            if (Directory.Exists(sentinelDirectory))
                Directory.Delete(sentinelDirectory, recursive: true);
        }
    }

    private static async Task VerifyCancellationAsync(
        CatalogLocalLibraryService service,
        string libraryRoot)
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var item = CreateItem(
            "local-canceled-game",
            "cancel"u8.ToArray(),
            CatalogExtractPolicy.None,
            ".rom");
        try
        {
            _ = await service.InspectAsync(libraryRoot, [item], cancellation.Token);
            throw new InvalidDataException("A análise local ignorou o cancelamento prévio.");
        }
        catch (OperationCanceledException)
        {
        }
    }

    private static CatalogItem CreateItem(
        string id,
        byte[] payload,
        CatalogExtractPolicy policy,
        string extension,
        string category = "Windows")
    {
        var artifactId = Convert.ToHexString(
                SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(id)))[..32]
            .ToLowerInvariant();
        return new CatalogItem
        {
            Id = id,
            CategoryId = "windows",
            Category = category,
            Title = id,
            Extract = policy == CatalogExtractPolicy.ExtractArchive,
            Artifact = new CatalogArtifactDescriptor
            {
                ArtifactId = artifactId,
                ArtifactVersion = 1,
                ContentLength = payload.LongLength,
                Sha256 = Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant(),
                SafeFileName = id + extension,
                FileExtension = extension,
                ExtractPolicy = policy,
                ManifestIdentity = new string('a', 64)
            }
        };
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidDataException(message);
    }

    private static async Task AssertThrowsAsync<TException>(Func<Task> action)
        where TException : Exception
    {
        try
        {
            await action();
        }
        catch (TException)
        {
            return;
        }

        throw new InvalidDataException(
            $"A operação deveria falhar com {typeof(TException).Name}.");
    }
}
