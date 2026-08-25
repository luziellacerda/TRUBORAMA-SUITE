using TurboBoxManager.Catalog;

internal static class GameLibraryLocatorVerifier
{
    public static void Run(string root)
    {
        VerifyUniqueDocumentsMatch(root);
        VerifyExplicitPreferenceOrder(root);
        VerifyAmbiguousDriveRootsRequireUser(root);
        VerifyDiscoveryIsNotRecursive(root);
    }

    private static void VerifyUniqueDocumentsMatch(string root)
    {
        var scenario = Path.Combine(root, "unique-documents");
        var install = Path.Combine(scenario, "install", "app");
        var documents = Path.Combine(scenario, "documents");
        Directory.CreateDirectory(install);
        Directory.CreateDirectory(Path.Combine(
            documents,
            CatalogArchiveExtractor.GameLibraryFolderName));

        var result = CatalogGameLibraryLocator.Discover(
            persistedPath: null,
            install,
            documents,
            readyFixedDriveRoots: []);

        Check(result.SelectedPath is not null
              && result.SelectedPath.Equals(
                  Path.Combine(documents, CatalogArchiveExtractor.GameLibraryFolderName),
                  StringComparison.OrdinalIgnoreCase)
              && !result.RequiresUserSelection,
            "Uma única pasta em Documentos deveria ser descoberta automaticamente.");
    }

    private static void VerifyExplicitPreferenceOrder(string root)
    {
        var scenario = Path.Combine(root, "preference");
        var install = Path.Combine(scenario, "install");
        var documents = Path.Combine(scenario, "documents");
        var persisted = Path.Combine(scenario, "saved", CatalogArchiveExtractor.GameLibraryFolderName);
        var installLibrary = Path.Combine(install, CatalogArchiveExtractor.GameLibraryFolderName);
        var documentsLibrary = Path.Combine(documents, CatalogArchiveExtractor.GameLibraryFolderName);
        Directory.CreateDirectory(persisted);
        Directory.CreateDirectory(installLibrary);
        Directory.CreateDirectory(documentsLibrary);

        var result = CatalogGameLibraryLocator.Discover(
            persisted,
            install,
            documents,
            readyFixedDriveRoots: []);

        Check(result.Matches.Count == 3
              && result.SelectedPath?.Equals(persisted, StringComparison.OrdinalIgnoreCase) == true
              && !result.RequiresUserSelection,
            "O caminho persistido deveria prevalecer sobre instalação e Documentos.");
    }

    private static void VerifyAmbiguousDriveRootsRequireUser(string root)
    {
        var scenario = Path.Combine(root, "ambiguous-drives");
        var install = Path.Combine(scenario, "install", "app");
        var documents = Path.Combine(scenario, "documents");
        var driveA = Path.Combine(scenario, "drive-a");
        var driveB = Path.Combine(scenario, "drive-b");
        Directory.CreateDirectory(install);
        Directory.CreateDirectory(documents);
        Directory.CreateDirectory(Path.Combine(driveA, CatalogArchiveExtractor.GameLibraryFolderName));
        Directory.CreateDirectory(Path.Combine(driveB, CatalogArchiveExtractor.GameLibraryFolderName));

        var result = CatalogGameLibraryLocator.Discover(
            persistedPath: null,
            install,
            documents,
            readyFixedDriveRoots: [driveA, driveB]);

        Check(result.SelectedPath is null
              && result.Matches.Count == 2
              && result.RequiresUserSelection,
            "Duas pastas em raízes genéricas precisam exigir escolha do usuário.");
    }

    private static void VerifyDiscoveryIsNotRecursive(string root)
    {
        var scenario = Path.Combine(root, "non-recursive");
        var install = Path.Combine(scenario, "install");
        var documents = Path.Combine(scenario, "documents");
        Directory.CreateDirectory(Path.Combine(
            install,
            "nested",
            CatalogArchiveExtractor.GameLibraryFolderName));
        Directory.CreateDirectory(documents);

        var result = CatalogGameLibraryLocator.Discover(
            persistedPath: null,
            install,
            documents,
            readyFixedDriveRoots: []);

        Check(result.SelectedPath is null && result.Matches.Count == 0,
            "A descoberta não pode varrer subpastas recursivamente.");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
