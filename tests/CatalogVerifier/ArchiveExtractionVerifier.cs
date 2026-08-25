using System.IO.Compression;
using System.Text;
using TurboBoxManager.Catalog;

internal static class ArchiveExtractionVerifier
{
    public static async Task RunAsync(string root)
    {
        Directory.CreateDirectory(root);
        await VerifySuccessfulZipAsync(root);
        await VerifyNamedGameLibraryAsync(root);
        await VerifyTraversalIsBlockedAsync(root);
        await VerifyInsufficientSpacePreservesPackageAsync(root);
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
            "Pacote NES");

        Check(result.Succeeded, result.Message);
        Check(File.Exists(Path.Combine(result.DestinationPath, "roms", "game.txt")),
            "O arquivo do ZIP não foi extraído.");
        Check(File.Exists(archivePath) && result.ArchivePreserved,
            "O extrator não pode apagar o pacote antes da UI confirmar sucesso.");

        var recovered = await new CatalogArchiveExtractor().ExtractAsync(
            archivePath,
            destinationBase,
            "Nintendo",
            "Pacote NES");
        Check(recovered.Succeeded && recovered.DestinationPath == result.DestinationPath,
            "Uma queda após publicar a pasta deveria recuperar a extração já concluída.");
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
            baseDirectoryIsGameLibrary: true);

        Check(result.Succeeded, result.Message);
        Check(Path.GetRelativePath(libraryRoot, result.DestinationPath)
                .Equals(Path.Combine("Jogos retrô", "Pacote SNES"), StringComparison.OrdinalIgnoreCase),
            "A extração não preservou categoria/item dentro da pasta mestre.");
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
        var driveRoot = Path.GetPathRoot(Path.GetFullPath(destinationBase))!;
        var unavailableReserve = checked(new DriveInfo(driveRoot).AvailableFreeSpace + 1);
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

    private static void CreateZip(string path, string entryName, string content)
    {
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        var entry = archive.CreateEntry(entryName, CompressionLevel.SmallestSize);
        using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
        writer.Write(content);
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
