using System.IO;

namespace TurboBoxManager.Catalog;

internal static class CatalogArchivePolicy
{
    private static readonly HashSet<string> SupportedArchiveExtensions =
        new(StringComparer.OrdinalIgnoreCase) { ".zip", ".rar", ".7z" };

    internal static bool IsRecognizedArchive(CatalogArtifactDescriptor artifact)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        var extension = artifact.FileExtension.StartsWith('.')
            ? artifact.FileExtension
            : "." + artifact.FileExtension;
        return SupportedArchiveExtensions.Contains(extension)
               && Path.GetExtension(artifact.SafeFileName).Equals(
                   extension,
                   StringComparison.OrdinalIgnoreCase);
    }

    internal static CatalogArtifactDescriptor ForExtraction(
        CatalogArtifactDescriptor artifact)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        if (artifact.ExtractPolicy == CatalogExtractPolicy.ExtractArchive) return artifact;
        if (!IsRecognizedArchive(artifact))
            throw new InvalidDataException("O arquivo autorizado não é um pacote compactado suportado.");
        return artifact with { ExtractPolicy = CatalogExtractPolicy.ExtractArchive };
    }
}
