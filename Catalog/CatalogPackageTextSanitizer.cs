using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace TurboBoxManager.Catalog;

internal enum CatalogPackageTextDisposition { Preserve, Rewrite, Drop }

internal sealed record CatalogPackageTextTransformation(
    CatalogPackageTextDisposition Disposition,
    string RelativePath,
    byte[]? PublishedBytes);

/// <summary>
/// Treats only small TXT documents supplied inside an extracted game package.
/// Binary/game payloads and unrelated text files are never modified.
/// </summary>
internal static partial class CatalogPackageTextSanitizer
{
    internal const int MaximumInspectableBytes = 4 * 1024 * 1024;

    internal static bool ShouldInspect(string relativePath, long declaredSize) =>
        declaredSize is >= 0 and <= MaximumInspectableBytes
        && Path.GetExtension(relativePath).Equals(".txt", StringComparison.OrdinalIgnoreCase);

    internal static CatalogPackageTextTransformation Transform(string relativePath, byte[] sourceBytes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);
        ArgumentNullException.ThrowIfNull(sourceBytes);
        if (!ShouldInspect(relativePath, sourceBytes.LongLength))
            return new(CatalogPackageTextDisposition.Preserve, relativePath, sourceBytes);

        var brandedFileName = BrandRegex().IsMatch(Path.GetFileName(relativePath));
        if (!TryDecode(sourceBytes, out var text, out var encoding, out var preamble))
            return brandedFileName || ContainsBrandBytes(sourceBytes)
                ? new(CatalogPackageTextDisposition.Drop, relativePath, null)
                : new(CatalogPackageTextDisposition.Preserve, relativePath, sourceBytes);
        if (!brandedFileName && !BrandRegex().IsMatch(text))
            return new(CatalogPackageTextDisposition.Preserve, relativePath, sourceBytes);

        var rewritten = BrandRegex().Replace(text, "Turbobox");
        if (string.IsNullOrWhiteSpace(rewritten))
            return new(CatalogPackageTextDisposition.Drop, relativePath, null);

        var body = encoding.GetBytes(rewritten);
        var output = new byte[preamble.Length + body.Length];
        preamble.CopyTo(output, 0);
        body.CopyTo(output, preamble.Length);
        var renamed = BrandRegex().Replace(Path.GetFileName(relativePath), "Turbobox");
        var parent = Path.GetDirectoryName(relativePath);
        return new(
            CatalogPackageTextDisposition.Rewrite,
            string.IsNullOrEmpty(parent) ? renamed : Path.Combine(parent, renamed),
            output);
    }

    private static bool ContainsBrandBytes(byte[] bytes)
    {
        ReadOnlySpan<byte> brand = "sambox"u8;
        for (var index = 0; index <= bytes.Length - brand.Length; index++)
        {
            var match = true;
            for (var offset = 0; offset < brand.Length; offset++)
            {
                var value = bytes[index + offset];
                if (value is >= (byte)'A' and <= (byte)'Z') value += 32;
                if (value == brand[offset]) continue;
                match = false;
                break;
            }
            if (match) return true;
        }
        return false;
    }

    private static bool TryDecode(
        byte[] bytes,
        out string text,
        out Encoding encoding,
        out byte[] preamble)
    {
        text = string.Empty;
        encoding = new UTF8Encoding(false, true);
        preamble = [];
        if (bytes.Length == 0) return false;
        try
        {
            if (bytes.AsSpan().StartsWith(Encoding.UTF8.Preamble))
            {
                preamble = Encoding.UTF8.Preamble.ToArray();
                text = encoding.GetString(bytes.AsSpan(preamble.Length));
            }
            else if (bytes.AsSpan().StartsWith(Encoding.Unicode.Preamble))
            {
                encoding = new UnicodeEncoding(false, false, true);
                preamble = Encoding.Unicode.Preamble.ToArray();
                text = encoding.GetString(bytes.AsSpan(preamble.Length));
            }
            else
            {
                text = encoding.GetString(bytes);
            }
            return !text.Contains('\0');
        }
        catch (DecoderFallbackException)
        {
            encoding = Encoding.Latin1;
            preamble = [];
            text = encoding.GetString(bytes);
            return !text.Contains('\0');
        }
    }

    [GeneratedRegex("Sambox", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex BrandRegex();
}
