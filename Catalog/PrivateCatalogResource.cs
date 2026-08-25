using System.Buffers.Binary;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;

namespace TurboBoxManager.Catalog;

internal static class PrivateCatalogResource
{
    private const string ResourceName = "Turborama.PrivateCatalog.bin";
    private static readonly byte[] Magic = "TRBCAT01"u8.ToArray();
    private static readonly byte[] AdditionalData =
        "TURBORAMA-PRIVATE-CATALOG-V1"u8.ToArray();
    private const int NonceLength = 12;
    private const int TagLength = 16;
    private const int MaximumResourceBytes = 16 * 1024 * 1024;

    public static bool TryLoadRepository(
        string resourceBaseManifestPath,
        out CatalogRepository? repository)
    {
        repository = null;
#if PRIVATE_CATALOG_EMBEDDED
        var plaintext = DecryptPayload();
        try
        {
            var catalogOffset = GetCatalogOffset(plaintext);
            using var catalogStream = new MemoryStream(
                plaintext,
                catalogOffset,
                plaintext.Length - catalogOffset,
                writable: false,
                publiclyVisible: false);
            repository = CatalogRepository.Load(
                catalogStream,
                resourceBaseManifestPath,
                usePackResources: true);
            return true;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }
#else
        return false;
#endif
    }

    public static string? TryReadPackagedKey()
    {
#if PRIVATE_CATALOG_EMBEDDED
        var plaintext = DecryptPayload();
        try
        {
            var keyLength = ReadKeyLength(plaintext);
            return Encoding.UTF8.GetString(plaintext, sizeof(int), keyLength).Trim();
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }
#else
        return null;
#endif
    }

#if PRIVATE_CATALOG_EMBEDDED
    private static byte[] DecryptPayload()
    {
        using var resource = Assembly.GetExecutingAssembly()
            .GetManifestResourceStream(ResourceName)
            ?? throw new InvalidDataException("O catálogo privado incorporado não foi encontrado.");
        if (resource.Length < Magic.Length + NonceLength + TagLength + sizeof(int)
            || resource.Length > MaximumResourceBytes)
            throw new InvalidDataException("O catálogo privado incorporado possui tamanho inválido.");

        var encrypted = new byte[checked((int)resource.Length)];
        var read = 0;
        while (read < encrypted.Length)
        {
            var count = resource.Read(encrypted, read, encrypted.Length - read);
            if (count == 0) throw new EndOfStreamException("O catálogo privado está incompleto.");
            read += count;
        }

        var key = PrivateCatalogSecrets.CreateKey();
        try
        {
            if (key.Length != 32
                || !CryptographicOperations.FixedTimeEquals(
                    encrypted.AsSpan(0, Magic.Length),
                    Magic))
                throw new InvalidDataException("O catálogo privado incorporado é inválido.");

            var nonceOffset = Magic.Length;
            var tagOffset = nonceOffset + NonceLength;
            var ciphertextOffset = tagOffset + TagLength;
            var plaintext = new byte[encrypted.Length - ciphertextOffset];
            try
            {
                using var aes = new AesGcm(key, TagLength);
                aes.Decrypt(
                    encrypted.AsSpan(nonceOffset, NonceLength),
                    encrypted.AsSpan(ciphertextOffset),
                    encrypted.AsSpan(tagOffset, TagLength),
                    plaintext,
                    AdditionalData);
                _ = GetCatalogOffset(plaintext);
                return plaintext;
            }
            catch
            {
                CryptographicOperations.ZeroMemory(plaintext);
                throw;
            }
        }
        catch (CryptographicException exception)
        {
            throw new InvalidDataException("Não foi possível abrir o catálogo privado incorporado.", exception);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
            CryptographicOperations.ZeroMemory(encrypted);
        }
    }

    private static int GetCatalogOffset(byte[] plaintext)
    {
        var keyLength = ReadKeyLength(plaintext);
        var catalogOffset = checked(sizeof(int) + keyLength);
        if (catalogOffset >= plaintext.Length)
            throw new InvalidDataException("O catálogo privado não contém dados do catálogo.");
        return catalogOffset;
    }

    private static int ReadKeyLength(byte[] plaintext)
    {
        if (plaintext.Length < sizeof(int))
            throw new InvalidDataException("O catálogo privado está incompleto.");
        var keyLength = BinaryPrimitives.ReadInt32LittleEndian(plaintext);
        if (keyLength < 1 || keyLength > 64 * 1024 || sizeof(int) + keyLength > plaintext.Length)
            throw new InvalidDataException("A chave incorporada possui tamanho inválido.");
        return keyLength;
    }
#endif
}
