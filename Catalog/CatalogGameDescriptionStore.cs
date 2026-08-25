using System.IO;
using System.Xml;
using System.Xml.Linq;

namespace TurboBoxManager.Catalog;

internal static class CatalogGameDescriptionStore
{
    private const long MaximumDescriptionFileBytes = 256 * 1024;
    private const long MaximumDescriptionSetBytes = 4 * 1024 * 1024;

    internal static IReadOnlyDictionary<string, string> Load(string manifestPath)
    {
        var descriptions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var manifestDirectory = Path.GetDirectoryName(Path.GetFullPath(manifestPath));
        if (manifestDirectory is null) return descriptions;

        var descriptionDirectory = Path.Combine(manifestDirectory, "GameDescriptions");
        if (!Directory.Exists(descriptionDirectory)) return descriptions;

        long totalBytes = 0;
        foreach (var path in Directory.EnumerateFiles(descriptionDirectory, "*.xml", SearchOption.TopDirectoryOnly)
                     .OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
        {
            var file = new FileInfo(path);
            if (file.Length is <= 0 or > MaximumDescriptionFileBytes)
                throw new InvalidDataException($"Arquivo de descrições inválido: {file.Name}.");
            totalBytes += file.Length;
            if (totalBytes > MaximumDescriptionSetBytes)
                throw new InvalidDataException("O conjunto de descrições excede o limite permitido.");

            using var stream = File.OpenRead(path);
            using var reader = XmlReader.Create(stream, new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
                MaxCharactersInDocument = MaximumDescriptionFileBytes * 2,
                IgnoreComments = true,
                IgnoreWhitespace = true
            });
            var document = XDocument.Load(reader, LoadOptions.None);
            foreach (var game in document.Root?.Elements("game") ?? [])
            {
                var id = ((string?)game.Attribute("id") ?? string.Empty).Trim();
                var description = string.Join(
                    ' ',
                    ((string?)game.Element("description") ?? string.Empty)
                    .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
                if (id.Length == 0 || description.Length == 0 || !descriptions.TryAdd(id, description))
                    throw new InvalidDataException($"Descrição ausente ou duplicada em {file.Name}.");
            }
        }

        return descriptions;
    }
}
